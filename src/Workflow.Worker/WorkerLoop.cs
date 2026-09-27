using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Core.Errors;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // The claim call itself threw (cold-start race against bootstrap's
    // migrations: workflow.claim_jobs undefined or workflow_worker not yet
    // granted EXECUTE on it, both PostgresException). No job was ever claimed,
    // so there is nothing to roll back or fail -- RunAsync's poll loop treats
    // this exactly like NoJob and tries again after the same pause.
    ClaimFailed,

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
    public static readonly WorkerStepResult ClaimFailed = new(WorkerStepOutcome.ClaimFailed);
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
public sealed partial class WorkerLoop(
    string owner,
    NpgsqlDataSource dataSource,
    IWorkflowJobRuntime jobRuntime,
    PostgresActionExecutor actionExecutor,
    ISchemaValidator validator,
    TimeProvider clock,
    IntervalProfile intervals,
    IReadOnlyList<string> scopes,
    IFailpointGate? failpoints = null,
    ILogger<WorkerLoop>? logger = null)
{
    private readonly IFailpointGate failpointGate = failpoints ?? NoopFailpointGate.Instance;

    // NullLogger by default so every existing caller -- and every test written
    // before week 4 -- keeps compiling and running unchanged.
    private readonly ILogger<WorkerLoop> log = logger ?? NullLogger<WorkerLoop>.Instance;

    public async Task<WorkerStepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        JsonNode claimed;
        try
        {
            claimed = await jobRuntime.ClaimAsync(owner, intervals.LeaseMs, cancellationToken);
        }
        // Cold-start race (SPEC ticket): bootstrap's migrations may not have run
        // yet, so workflow.claim_jobs can be undefined or not yet granted to
        // workflow_worker. Caught around the claim alone, not around job
        // processing -- that has its own error handling below and must keep
        // doing it. A genuine external cancellation (worker shutdown) is left
        // to propagate rather than swallowed as a claim failure.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogClaimFailed(log, owner, exception.GetType().Name, exception.Message);
            return WorkerStepResult.ClaimFailed;
        }

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
            if (result.Outcome is WorkerStepOutcome.NoJob or WorkerStepOutcome.ClaimFailed)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(intervals.PollIntervalMs), clock, cancellationToken);
            }
        }
    }

    private async Task<WorkerStepResult> ProcessJobAsync(JsonNode job, CancellationToken cancellationToken)
    {
        // SPEC failpoint "after_job_claim": commit of lease and attempt has
        // already happened inside ClaimAsync -- this is the earliest point
        // after that commit and strictly before api.invoke, so it fires on a
        // successful claim only, never on an empty-poll iteration (RunOnceAsync
        // returns WorkerStepResult.NoJob before ever calling this method).
        await failpointGate.ReachAsync("after_job_claim", owner, cancellationToken);

        var task = job["task"]!;
        var processData = job["process"]!["data"];

        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());
        var executionId = job["executionId"]!.GetValue<string>();
        var attemptId = Guid.Parse(job["attemptId"]!.GetValue<string>());
        var leaseVersion = job["leaseVersion"]!.GetValue<int>();
        var processId = Guid.Parse(job["process"]!["processId"]!.GetValue<string>());
        var timeoutMs = task["timeoutMs"]!.GetValue<int>();

        // Week 4, ticket 10: one structured line per attempt needs the flow, its
        // version and the step key by name. Migration 050 sends all three with the
        // claim; the null-tolerant reads keep an older claim payload working rather
        // than turning a logging concern into a claim failure.
        var step = new StepIdentity(
            Owner: owner,
            CorrelationId: Guid.NewGuid(),
            ProcessId: processId,
            FlowName: job["flowName"]?.GetValue<string>() ?? "",
            FlowVersion: job["flowVersion"]?.GetValue<int>() ?? 0,
            StepKey: job["stepKey"]?.GetValue<string>() ?? "",
            JobId: jobId,
            ExecutionId: executionId,
            AttemptId: attemptId,
            LeaseVersion: leaseVersion,
            Module: task["module"]!.GetValue<string>(),
            Action: task["action"]!.GetValue<string>(),
            ActionVersion: task["actionVersion"]!.GetValue<int>());

        var started = clock.GetTimestamp();

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
            LogStep(step, started, outcome: null, WorkflowErrorCodes.MappingMissing);
            return WorkerStepResult.MappingMissing;
        }

        var requestSchema = task["requestSchema"]!;
        if (!validator.Validate(requestSchema, mapping.Payload!).IsValid)
        {
            await jobRuntime.FailAsync(jobId, owner, leaseVersion, WorkflowErrorCodes.RequestInvalid, retryable: false, cancellationToken);
            LogStep(step, started, outcome: null, WorkflowErrorCodes.RequestInvalid);
            return WorkerStepResult.RequestInvalid;
        }

        var claims = new VerifiedClaims(WorkflowWorkerPrincipal.Name, WorkflowWorkerPrincipal.Name, scopes);
        var manifest = new PrefetchedManifest(
            Version: task["actionVersion"]!.GetValue<int>(),
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
                correlationId: step.CorrelationId,
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
            LogStep(step, started, outcome: null, actionError.Code);
            return new WorkerStepResult(WorkerStepOutcome.ActionRejected, actionError.Code);
        }

        // SPEC failpoint "after_action_before_finish": the effect and the
        // response-schema check both already happened inside
        // ExecuteInTransactionAsync above, and the transaction is still open --
        // this call must not commit or roll it back either way.
        await failpointGate.ReachAsync("after_action_before_finish", owner, cancellationToken);

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

            LogStep(step, started, outcome: null, code);
            return new WorkerStepResult(WorkerStepOutcome.FinishRejected, code);
        }

        await transaction.CommitAsync(cancellationToken);
        LogStep(step, started, pipelineOutcome.Result.Outcome, errorCode: null);
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

    private void LogStep(StepIdentity step, long started, string? outcome, string? errorCode)
    {
        // The duration is measured only if the line will actually be written; the
        // analyzer is right that computing an argument for a disabled log is waste.
        if (!log.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var durationMs = (long)clock.GetElapsedTime(started).TotalMilliseconds;

        LogStepFinished(
            log,
            step.Owner,
            step.CorrelationId,
            step.ProcessId,
            step.FlowName,
            step.FlowVersion,
            step.StepKey,
            step.JobId,
            step.ExecutionId,
            step.AttemptId,
            step.LeaseVersion,
            step.Module,
            step.Action,
            step.ActionVersion,
            outcome,
            errorCode,
            durationMs);
    }

    // One line per finished attempt, carrying every identifier the published log
    // contract asks for and nothing else: no payload, no result, no secret. The
    // error code is the safe dotted identifier the runtime already returns, and
    // outcome and errorCode are mutually exclusive by construction.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "step finished owner={Owner} correlationId={CorrelationId} processId={ProcessId} "
            + "flow={FlowName} flowVersion={FlowVersion} stepKey={StepKey} jobId={JobId} "
            + "executionId={ExecutionId} attemptId={AttemptId} leaseVersion={LeaseVersion} "
            + "action={Module}.{Action} actionVersion={ActionVersion} outcome={Outcome} "
            + "errorCode={ErrorCode} duration_ms={DurationMs}")]
    private static partial void LogStepFinished(
        ILogger logger, string owner, Guid correlationId, Guid processId, string flowName, int flowVersion,
        string stepKey, Guid jobId, string executionId, Guid attemptId, int leaseVersion, string module,
        string action, int actionVersion, string? outcome, string? errorCode, long durationMs);

    // A claim that failed has no job to name yet, so it carries what it has: which
    // worker, and the failure by type and message. No connection string, and no
    // password: the message of an Npgsql failure names the host and the role, never
    // the credential.
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "job claim failed, will retry owner={Owner} error={Error} errorMessage={ErrorMessage}")]
    private static partial void LogClaimFailed(ILogger logger, string owner, string error, string errorMessage);

    private sealed record StepIdentity(
        string Owner,
        Guid CorrelationId,
        Guid ProcessId,
        string FlowName,
        int FlowVersion,
        string StepKey,
        Guid JobId,
        string ExecutionId,
        Guid AttemptId,
        int LeaseVersion,
        string Module,
        string Action,
        int ActionVersion);

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
