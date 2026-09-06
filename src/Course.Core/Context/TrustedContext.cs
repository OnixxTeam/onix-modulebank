namespace Course.Core.Context;

// Everything a JWT contributes to the trusted context, extracted and shape-checked
// before the request payload is even parsed, so a same-named payload field can never
// reach TrustedContext by merging into it later.
public sealed record VerifiedClaims(string Principal, string Consumer, IReadOnlyList<string> Scopes);

// Built once the manifest is known (Deadline needs manifest.TimeoutMs), never from
// payload. CorrelationId is always a fresh runtime UUID; RequestId mirrors the
// Idempotency-Key header verbatim, or null if the caller sent none.
//
// ProcessId/JobId/ExecutionId/AttemptId exist only for the workflow worker: the
// network path leaves all four null, and the pipeline serializes each into
// api.invoke's context only when set, so target functions see the exact same
// context shape as before. ExecutionId is the subject-effect idempotency key --
// target functions read it under this name.
//
// SignatureVersion exists only for the network path (ticket 07): non-null
// exactly when ActionEndpoint's ISignatureVerifier verified an
// X-Provider-Signature header over the exact request bytes. It serializes into
// api.invoke's context as nested transport.signatureVerified/signatureVersion,
// the same "only when set" mechanism the worker fields already use, so a
// target that never looks for it sees no shape change at all.
public sealed record TrustedContext(
    string Principal,
    string Consumer,
    IReadOnlyList<string> Scopes,
    Guid CorrelationId,
    string? RequestId,
    DateTimeOffset Deadline,
    Guid? ProcessId = null,
    Guid? JobId = null,
    string? ExecutionId = null,
    Guid? AttemptId = null,
    int? SignatureVersion = null);
