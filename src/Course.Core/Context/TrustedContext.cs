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
// SignatureVersion/SignatureBodyHashHex exist only for the network path
// (ticket 07): both non-null exactly when ActionEndpoint's ISignatureVerifier
// verified an X-Provider-Signature header over the exact request bytes.
// SignatureVersion serializes into api.invoke's context as nested
// transport.signatureVerified/signatureVersion (SPEC fixes that object at
// exactly those two fields); SignatureBodyHashHex serializes as a sibling
// top-level rawBodyHash, not a third transport field, so a signed action that
// needs "the hash of the exact bytes the signature covered" (ticket 08's
// receipt.accept) never has to re-derive it from a reserialized payload. Both
// use the same "only when set" mechanism the worker fields already use, so a
// target that never looks for either sees no shape change at all.
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
    int? SignatureVersion = null,
    string? SignatureBodyHashHex = null);
