using System.Text.Json.Nodes;

namespace Course.Core.Actions;

// The manifest fields workflow.claim_jobs already returned for the exact
// pinned action version a job carries (migration 022's own jsonb_build_object:
// version, requestSchema, responseSchema, outcomes, requiredPolicy). Supplying
// these lets ExecuteInTransactionAsync skip its own api.get_action_manifest
// read for a worker-driven call -- ticket 10 (SPEC: "claim_jobs уже
// возвращает ... необходимые shared executor; отдельное чтение каталога роли
// не требуется") closes workflow_worker's EXECUTE list to exactly four
// functions, and a second direct grant on get_action_manifest would be a
// fifth. Idempotency mode is deliberately absent: the one check it drives
// (IdempotencyRequired) can never fire for a caller that always supplies a
// request id, which every worker call does (TrustedContext.ExecutionId).
//
// Version lives here, not in a separate "explicit version" argument: the
// manifest and its version are one claimed fact from the same claim_jobs row,
// and ExecuteInTransactionAsync's prefetched-manifest branch trusts this
// field exclusively (ticket 02: an unchecked `explicitVersion!.Value` there
// used to NRE whenever a caller supplied a manifest without also supplying a
// matching version argument -- a property that cannot go missing has no null
// to check).
public sealed record PrefetchedManifest(
    int Version, JsonNode RequestSchema, JsonNode ResponseSchema, IReadOnlyList<string> Outcomes, IReadOnlyList<string> RequiredPolicy);

// Extra input only a non-HTTP caller (the workflow worker) supplies to the
// pipeline: workflow identifiers to stamp onto TrustedContext, a timeout the
// caller's own map declared (which outranks the manifest's timeout_ms when
// set), and the manifest fields above so the pipeline never re-reads the
// catalog on this caller's behalf.
public sealed record ActionCallerContext(
    Guid? ProcessId = null,
    Guid? JobId = null,
    string? ExecutionId = null,
    Guid? AttemptId = null,
    TimeSpan? TimeoutOverride = null,
    PrefetchedManifest? Manifest = null);

// TargetReturnedError marks the one failure branch where api.invoke ran and the
// target itself reported status=error: the dispatch row is the only thing left
// in the transaction (the domain effect rolled back to a savepoint already), and
// whether to keep it by committing is the transaction owner's call, not the
// pipeline's -- the network path keeps it, a retrying worker must not.
public sealed record ActionPipelineOutcome(ActionExecutionResult Result, bool TargetReturnedError);
