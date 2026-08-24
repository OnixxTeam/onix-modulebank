using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Course.Core.Errors;

// One shape for every failure the action runtime can report, HTTP status included,
// so ErrorEnvelopeMiddleware and the endpoint's inline branches build the exact same
// envelope regardless of where the error was raised.
public sealed record ActionError(int HttpStatus, string Code, string Message, bool Retryable = false, JsonNode? Details = null)
{
    // The fixed HTTP API error matrix (contract-reference, "HTTP API"). A status=error
    // envelope coming back from api.invoke carries one of these codes when it originates
    // at the runtime boundary (api.invoke's own early-exit checks); a code outside this
    // table is a target's own business error, which this runtime has no contract
    // knowledge of and reports as internal.error rather than guessing a status.
    private static readonly Dictionary<string, int> KnownCodeStatus = new()
    {
        ["auth.invalid"] = 401,
        ["request.invalid"] = 400,
        ["idempotency.required"] = 400,
        ["access.denied"] = 403,
        ["action.not_found"] = 404,
        ["idempotency.conflict"] = 409,
        ["payload.invalid"] = 422,
        ["dependency.unavailable"] = 503,
        ["action.contract_violation"] = 500,
        ["action.timeout"] = 504,
        ["internal.error"] = 500,
    };

    public static ActionError AuthInvalid(string message) => new(401, "auth.invalid", message);

    public static ActionError RequestInvalid(string message) => new(400, "request.invalid", message);

    public static ActionError IdempotencyRequired() =>
        new(400, "idempotency.required", "Idempotency-Key header is required for this action");

    public static ActionError AccessDenied() => new(403, "access.denied", "insufficient policy for this action");

    public static ActionError ActionNotFound() => new(404, "action.not_found", "action or version not found");

    public static ActionError PayloadInvalid(string message) => new(422, "payload.invalid", message);

    public static ActionError DependencyUnavailable() =>
        new(503, "dependency.unavailable", "postgresql is temporarily unavailable", Retryable: true);

    public static ActionError ContractViolation() =>
        new(500, "action.contract_violation", "action result violated its manifest");

    public static ActionError Timeout() => new(504, "action.timeout", "action exceeded its timeout", Retryable: true);

    public static ActionError InternalError() => new(500, "internal.error", "unexpected runtime failure");

    // A dotted business code, same shape the checker requires of forcedErrorCode
    // (autocheck/public_check.py:328), whose last segment is not_found. Some contract
    // sections (e.g. operation.get) promise a controlled domain error rather than a 500
    // for "no such record", so this narrow suffix is let through as 404 even though it
    // is not in KnownCodeStatus. Only the .not_found suffix opens this door: a target
    // still cannot pick its own status by inventing an arbitrary code.
    private static readonly Regex BusinessNotFoundCode = new(
        @"^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)*\.not_found$",
        RegexOptions.Compiled);

    // Maps a status=error envelope's own `code` (as returned by api.invoke or, for now
    // untouched, the target function) to its contract HTTP status. Anything outside the
    // fixed matrix collapses to internal.error, except a BusinessNotFoundCode match.
    public static ActionError ForCode(string code, string? message, bool retryable)
    {
        if (KnownCodeStatus.TryGetValue(code, out var httpStatus))
        {
            return new ActionError(httpStatus, code, message ?? code, retryable);
        }

        if (BusinessNotFoundCode.IsMatch(code))
        {
            return new ActionError(404, code, message ?? code, retryable);
        }

        return InternalError();
    }
}
