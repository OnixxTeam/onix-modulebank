namespace Course.Core.Actions;

// Extra input only a non-HTTP caller (the workflow worker) supplies to the
// pipeline: workflow identifiers to stamp onto TrustedContext, and a timeout the
// caller's own map declared -- which outranks the manifest's timeout_ms when set.
public sealed record ActionCallerContext(
    Guid? ProcessId = null,
    Guid? JobId = null,
    string? ExecutionId = null,
    Guid? AttemptId = null,
    TimeSpan? TimeoutOverride = null);

// TargetReturnedError marks the one failure branch where api.invoke ran and the
// target itself reported status=error: the dispatch row is the only thing left
// in the transaction (the domain effect rolled back to a savepoint already), and
// whether to keep it by committing is the transaction owner's call, not the
// pipeline's -- the network path keeps it, a retrying worker must not.
public sealed record ActionPipelineOutcome(ActionExecutionResult Result, bool TargetReturnedError);
