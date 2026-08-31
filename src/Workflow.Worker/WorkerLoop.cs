using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Runtime;
using Workflow.Core.Security;

namespace Workflow.Worker;

public enum WorkerStepOutcome
{
    // Claimed nothing: workflow.claim_job returned an empty job. RunAsync's poll
    // loop is the only caller allowed to treat this as "sleep and try again".
    NoJob,

    // Mapping, request-schema, envelope or response-schema validation rejected the
    // step before or after invoking the target -- every one of these leaves the
    // job LEASED (no finish_job call), the same as an unhandled exception would,
    // because none of them is this ticket's to retry or fail (ticket 10 owns that).
    MappingMissing,
    RequestInvalid,
    ActionRejected,

    // The target succeeded and finish_job accepted the completion in the same
    // transaction, which is now committed.
    Succeeded,

    // The target succeeded but finish_job itself rejected the completion (stale
    // lease, unknown outcome) on a re-check inside the very transaction that
    // produced it -- the whole transaction rolls back, discarding the domain
    // effect along with the record of the call, so a reclaiming worker starts
    // clean instead of inheriting a half-applied attempt.
    FinishRejected,
}

// ErrorCode is set only for MappingMissing today, carrying the one declared
// code SPEC fixes by name (Workflow.Core.Runtime.WorkflowErrorCodes) -- ticket
// 10's fail_job wiring reads it from here rather than re-deriving it from the
// outcome enum.
public sealed record WorkerStepResult(WorkerStepOutcome Outcome, string? ErrorCode = null)
{
    public static readonly WorkerStepResult NoJob = new(WorkerStepOutcome.NoJob);
    public static readonly WorkerStepResult RequestInvalid = new(WorkerStepOutcome.RequestInvalid);
    public static readonly WorkerStepResult ActionRejected = new(WorkerStepOutcome.ActionRejected);
    public static readonly WorkerStepResult FinishRejected = new(WorkerStepOutcome.FinishRejected);
    public static readonly WorkerStepResult Succeeded = new(WorkerStepOutcome.Succeeded);
    public static readonly WorkerStepResult MappingMissing = new(WorkerStepOutcome.MappingMissing, WorkflowErrorCodes.MappingMissing);
}

// The whole automatic-step boundary as one instantiable class: claim (via the
// existing IWorkflowJobRuntime -- its own short, separately-committed
// transaction, SPEC "Lease, attempts и fencing"), map, validate, invoke through
// the shared executor's foreign-transaction path, finish -- with external clock
// and interval profile injected so a test can run two instances against one
// database without a container per instance or a real poll delay. Finish runs
// as its own raw call rather than through IWorkflowJobRuntime.FinishAsync
// because it must share the connection and transaction the invoke just ran in,
// which that interface's own (separately-connecting) method cannot do. Carries
// no map, step or action name of its own: every branch below reads from the
// claimed job's own JSON, never from a literal drawn from a specific flow.
public sealed class WorkerLoop(
    string owner,
    NpgsqlDataSource dataSource,
    IWorkflowJobRuntime jobRuntime,
    PostgresActionExecutor actionExecutor,
    ISchemaValidator validator,
    TimeProvider clock,
    IntervalProfile intervals,
    IReadOnlyList<string> scopes)
{
    public async Task<WorkerStepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await jobRuntime.ClaimAsync(owner, intervals.LeaseMs, cancellationToken);
        var job = claimed["job"];
        if (job is null || job.GetValueKind() == JsonValueKind.Null)
        {
            return WorkerStepResult.NoJob;
        }

        return await ProcessJobAsync(job, cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await RunOnceAsync(cancellationToken);
            if (result.Outcome == WorkerStepOutcome.NoJob)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(intervals.PollIntervalMs), clock, cancellationToken);
            }
        }
    }

    private async Task<WorkerStepResult> ProcessJobAsync(JsonNode job, CancellationToken cancellationToken)
    {
        var task = job["task"]!;
        var processData = job["process"]!["data"];

        var inputConstants = task["inputConstants"]!.AsObject();
        var inputMapping = task["inputMapping"]!.AsObject()
            .ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);

        var mapping = WorkflowStepPayloadMapper.BuildPayload(inputConstants, inputMapping, processData);
        if (!mapping.IsSuccess)
        {
            return WorkerStepResult.MappingMissing;
        }

        var requestSchema = task["requestSchema"]!;
        if (!validator.Validate(requestSchema, mapping.Payload!).IsValid)
        {
            return WorkerStepResult.RequestInvalid;
        }

        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());
        var executionId = job["executionId"]!.GetValue<string>();
        var attemptId = Guid.Parse(job["attemptId"]!.GetValue<string>());
        var leaseVersion = job["leaseVersion"]!.GetValue<int>();
        var processId = Guid.Parse(job["process"]!["processId"]!.GetValue<string>());
        var timeoutMs = task["timeoutMs"]!.GetValue<int>();

        var claims = new VerifiedClaims(WorkflowWorkerPrincipal.Name, WorkflowWorkerPrincipal.Name, scopes);
        var callerContext = new ActionCallerContext(
            ProcessId: processId,
            JobId: jobId,
            ExecutionId: executionId,
            AttemptId: attemptId,
            TimeoutOverride: TimeSpan.FromMilliseconds(timeoutMs));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var pipelineOutcome = await actionExecutor.ExecuteInTransactionAsync(
            connection,
            transaction,
            task["module"]!.GetValue<string>(),
            task["action"]!.GetValue<string>(),
            task["actionVersion"]!.GetValue<int>(),
            claims,
            correlationId: Guid.NewGuid(),
            requestId: executionId,
            mapping.Payload!,
            callerContext,
            cancellationToken);

        // Any rejection, including a TargetReturnedError the week-1 network path
        // would keep -- this transaction must roll back whole, taking the dispatch
        // row with it, so a retry never inherits a stale action_dispatches row
        // stamped with an executionId it is about to reuse.
        if (!pipelineOutcome.Result.IsSuccess)
        {
            return WorkerStepResult.ActionRejected;
        }

        var finish = await FinishAsync(
            connection, transaction, jobId, owner, leaseVersion, pipelineOutcome.Result.Outcome!, pipelineOutcome.Result.Result!,
            cancellationToken);

        if (finish["status"]!.GetValue<string>() != "ok")
        {
            return WorkerStepResult.FinishRejected;
        }

        await transaction.CommitAsync(cancellationToken);
        return WorkerStepResult.Succeeded;
    }

    private static async Task<JsonNode> FinishAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, string owner, int leaseVersion, string outcome,
        JsonNode result, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select workflow.finish_job(@jobId, @owner, @leaseVersion, @outcome, @result)", connection, transaction);
        command.Parameters.AddWithValue("jobId", jobId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("leaseVersion", leaseVersion);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = result.ToJsonString() });

        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        return JsonNode.Parse(raw)!;
    }
}
