using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Core.Errors;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Runtime;
using Workflow.Core.Security;

namespace Workflow.Worker;

public enum WorkerStepOutcome
{
    // Claimed nothing: workflow.claim_jobs returned an empty job. RunAsync's poll
    // loop is the only caller allowed to treat this as "sleep and try again".
    NoJob,

    // Mapping or request-schema validation rejected the step before the target
    // was ever invoked -- both non-retryable (SPEC: mapping/contract errors never
    // retry), both recorded through fail_job with no action transaction to roll
    // back first.
    MappingMissing,
    RequestInvalid,

    // The target itself was never invoked successfully: an error envelope, a
    // pipeline-level contract rejection (undeclared outcome, response schema
    // violation), or a thrown exception this loop mapped the same way the HTTP
    // path's ErrorEnvelopeMiddleware does (statement timeout, transient
    // connectivity). ErrorCode and whether it retries both come from the same
    // ActionError the pipeline or the exception mapper produced -- fail_job
    // decides retry-vs-DEAD from that Retryable flag, never from this enum case.
    ActionRejected,

    // The target succeeded and finish_job accepted the completion in the same
    // transaction, which is now committed.
    Succeeded,

    // The target succeeded but finish_job itself rejected the completion. A
    // stale lease means this worker no longer owns the job (already reclaimed),
    // so nothing here calls fail_job -- the new owner's own attempt is the one
    // that gets to record an outcome. An undeclared outcome is this worker's own
    // attempt to fail, non-retryably, since the map will never grow a transition
    // for it on a later try.
    FinishRejected,
}

// ErrorCode carries whatever code this attempt was failed with (or null for
// NoJob/Succeeded) -- fail_job already recorded it by the time this returns,
// so a caller inspecting the result is reading history, not deciding anything.
public sealed record WorkerStepResult(WorkerStepOutcome Outcome, string? ErrorCode = null)
{
    public static readonly WorkerStepResult NoJob = new(WorkerStepOutcome.NoJob);
    public static readonly WorkerStepResult Succeeded = new(WorkerStepOutcome.Succeeded);
    public static readonly WorkerStepResult MappingMissing = new(WorkerStepOutcome.MappingMissing, WorkflowErrorCodes.MappingMissing);
    public static readonly WorkerStepResult RequestInvalid = new(WorkerStepOutcome.RequestInvalid, WorkflowErrorCodes.RequestInvalid);
}

// The whole automatic-step boundary as one instantiable class: claim (via the
// existing IWorkflowJobRuntime -- its own short, separately-committed
// transaction, SPEC "Lease, attempts и fencing"), map, validate, invoke through
// the shared executor's foreign-transaction path, then either finish or fail --
// with external clock and interval profile injected so a test can run two
// instances against one database without a container per instance or a real
// poll delay. Finish runs as its own raw call rather than through
// IWorkflowJobRuntime.FinishAsync because it must share the connection and
// transaction the invoke just ran in, which that interface's own
// (separately-connecting) method cannot do; fail is the opposite shape on
// purpose -- IWorkflowJobRuntime.FailAsync always opens its own connection,
// because by the time it runs the action transaction is already rolled back
// (ticket 10). Carries no map, step or action name of its own: every branch
// below reads from the claimed job's own JSON, never from a literal drawn from
// a specific flow.
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

        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());
        var executionId = job["executionId"]!.GetValue<string>();
        var attemptId = Guid.Parse(job["attemptId"]!.GetValue<string>());
        var leaseVersion = job["leaseVersion"]!.GetValue<int>();
        var processId = Guid.Parse(job["process"]!["processId"]!.GetValue<string>());
        var timeoutMs = task["timeoutMs"]!.GetValue<int>();

        var inputConstants = task["inputConstants"]!.AsObject();
        var inputMapping = task["inputMapping"]!.AsObject()
            .ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);

        // Both checks below run before any action transaction opens, so a
        // rejection here has nothing to roll back first -- fail_job runs
        // directly on its own connection, non-retryably (SPEC: mapping and
        // request-contract errors never retry; a map that cannot resolve its
        // own declared source or shape a schema-valid payload will not resolve
        // or shape it any differently on a later attempt).
        var mapping = WorkflowStepPayloadMapper.BuildPayload(inputConstants, inputMapping, processData);
        if (!mapping.IsSuccess)
        {
            await jobRuntime.FailAsync(jobId, owner, leaseVersion, WorkflowErrorCodes.MappingMissing, retryable: false, cancellationToken);
            return WorkerStepResult.MappingMissing;
        }

        var requestSchema = task["requestSchema"]!;
        if (!validator.Validate(requestSchema, mapping.Payload!).IsValid)
        {
            await jobRuntime.FailAsync(jobId, owner, leaseVersion, WorkflowErrorCodes.RequestInvalid, retryable: false, cancellationToken);
            return WorkerStepResult.RequestInvalid;
        }

        var claims = new VerifiedClaims(WorkflowWorkerPrincipal.Name, WorkflowWorkerPrincipal.Name, scopes);
        var manifest = new PrefetchedManifest(
            RequestSchema: task["requestSchema"]!,
            ResponseSchema: task["responseSchema"]!,
            Outcomes: task["outcomes"]!.AsArray().Select(node => node!.GetValue<string>()).ToList(),
            RequiredPolicy: task["requiredPolicy"]!.AsArray().Select(node => node!.GetValue<string>()).ToList());
        var callerContext = new ActionCallerContext(
            ProcessId: processId,
            JobId: jobId,
            ExecutionId: executionId,
            AttemptId: attemptId,
            TimeoutOverride: TimeSpan.FromMilliseconds(timeoutMs),
            Manifest: manifest);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        ActionPipelineOutcome? pipelineOutcome = null;
        ActionError? thrownError = null;
        try
        {
            pipelineOutcome = await actionExecutor.ExecuteInTransactionAsync(
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
        }
        // Statement timeout, transient connectivity -- the same exception-driven
        // half of the error matrix ErrorEnvelopeMiddleware maps for the HTTP path
        // (NpgsqlErrorMapper), caught here instead because this loop calls the
        // pipeline directly rather than through that middleware. A genuine
        // external cancellation (worker shutdown) is left to propagate rather
        // than reported as a retryable timeout.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            thrownError = NpgsqlErrorMapper.Map(exception);
        }

        // Both sources land on the same ActionError shape (Code, Retryable) --
        // a thrown exception via NpgsqlErrorMapper, a pipeline rejection via its
        // own Result.Error -- so there is exactly one rollback+fail+return
        // shape below regardless of which one actually failed the attempt.
        // Every path from here on found the action transaction dead one way or
        // another (never committed, or about to be rolled back below) -- the
        // dispatch row and any domain effect it carried never survive it, so a
        // retry's next attempt starts from the same clean state this one did.
        var actionError = thrownError ?? (!pipelineOutcome!.Result.IsSuccess ? pipelineOutcome.Result.Error! : null);
        if (actionError is not null)
        {
            await RollbackAndFailAsync(transaction, jobId, leaseVersion, actionError.Code, actionError.Retryable, cancellationToken);
            return new WorkerStepResult(WorkerStepOutcome.ActionRejected, actionError.Code);
        }

        var finish = await FinishAsync(
            connection, transaction, jobId, owner, leaseVersion, pipelineOutcome!.Result.Outcome!, pipelineOutcome.Result.Result!,
            cancellationToken);

        if (finish["status"]!.GetValue<string>() != "ok")
        {
            var code = finish["code"]!.GetValue<string>();

            // lease_stale means some other owner already reclaimed this job --
            // its own attempt gets to record the outcome, not this stale one.
            // Every other rejection (unknown_outcome) is still this worker's own
            // attempt to fail, non-retryably: the map will never grow a
            // transition for an outcome it does not declare.
            if (code != "workflow.lease_stale")
            {
                await RollbackAndFailAsync(transaction, jobId, leaseVersion, code, retryable: false, cancellationToken);
            }

            return new WorkerStepResult(WorkerStepOutcome.FinishRejected, code);
        }

        await transaction.CommitAsync(cancellationToken);
        return WorkerStepResult.Succeeded;
    }

    // Rolled back explicitly here, not left for `await using` to unwind at
    // method exit: fail_job's own fencing select takes the same job row
    // finish_job (or the failed invoke) just locked FOR UPDATE, on a brand new
    // connection (ticket 10: "на отдельном соединении") -- calling it before
    // this rollback commits would block on a lock this same worker is still
    // holding. The transaction must actually be dead, not merely doomed, by the
    // time fail_job runs.
    private async Task RollbackAndFailAsync(
        NpgsqlTransaction transaction, Guid jobId, int leaseVersion, string errorCode, bool retryable, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        await jobRuntime.FailAsync(jobId, owner, leaseVersion, errorCode, retryable, cancellationToken);
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
