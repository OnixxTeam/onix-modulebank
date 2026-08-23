namespace Course.Core.Context;

// Everything a JWT contributes to the trusted context, extracted and shape-checked
// before the request payload is even parsed, so a same-named payload field can never
// reach TrustedContext by merging into it later.
public sealed record VerifiedClaims(string Principal, string Consumer, IReadOnlyList<string> Scopes);

// Built once the manifest is known (Deadline needs manifest.TimeoutMs), never from
// payload. CorrelationId is always a fresh runtime UUID; RequestId mirrors the
// Idempotency-Key header verbatim, or null if the caller sent none.
public sealed record TrustedContext(
    string Principal,
    string Consumer,
    IReadOnlyList<string> Scopes,
    Guid CorrelationId,
    string? RequestId,
    DateTimeOffset Deadline);
