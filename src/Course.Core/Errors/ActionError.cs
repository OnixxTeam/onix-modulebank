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
        ["workflow.decision_conflict"] = 409,
        ["signature.invalid"] = 401,
        // These two are receipt.accept_v1's own business codes (ticket 08), not
        // runtime ones -- but their contract-fixed statuses (403, 422) don't
        // match what the generic business-code fallback below would derive
        // (400, since neither code's last segment is literally "not_found"),
        // so they need an explicit entry same as payload.invalid already has.
        ["receipt.signature_required"] = 403,
        ["payload.invalid"] = 422,
        ["receipt.external_request_not_found"] = 422,
        ["dependency.unavailable"] = 503,
        ["action.contract_violation"] = 500,
        ["action.timeout"] = 504,
        ["internal.error"] = 500,
    };

    public static ActionError AuthInvalid(string message) => new(401, "auth.invalid", message);

    public static ActionError RequestInvalid(string message) => new(400, "request.invalid", message);

    public static ActionError RequestTooLarge(int maxBytes) =>
        new(413, "request.too_large", $"request body exceeds {maxBytes} bytes");

    public static ActionError IdempotencyRequired() =>
        new(400, "idempotency.required", "Idempotency-Key header is required for this action");

    public static ActionError AccessDenied() => new(403, "access.denied", "insufficient policy for this action");

    // Raised by the generic signature boundary itself (ticket 07), never by a
    // target: wrong header format, unknown version, or a mismatched HMAC all
    // collapse into this one code, before the target is ever called.
    public static ActionError SignatureInvalid() => new(401, "signature.invalid", "X-Provider-Signature is missing, malformed, or does not verify");

    public static ActionError ActionNotFound() => new(404, "action.not_found", "action or version not found");

    public static ActionError PayloadInvalid(string message) => new(422, "payload.invalid", message);

    public static ActionError DependencyUnavailable() =>
        new(503, "dependency.unavailable", "postgresql is temporarily unavailable", Retryable: true);

    public static ActionError ContractViolation() =>
        new(500, "action.contract_violation", "action result violated its manifest");

    public static ActionError Timeout() => new(504, "action.timeout", "action exceeded its timeout", Retryable: true);

    public static ActionError InternalError() => new(500, "internal.error", "unexpected runtime failure");

    // A dotted business code, same shape the checker requires of forcedErrorCode
    // (autocheck/public_check.py:328): a target's own vocabulary for a domain
    // rejection it decided on deliberately, as opposed to the runtime's fixed
    // transport matrix above.
    private static readonly Regex BusinessCode = new(
        @"^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$",
        RegexOptions.Compiled);

    // Maps a status=error envelope's own `code` (as returned by api.invoke or by the
    // target function) to its contract HTTP status.
    //
    // The fixed matrix wins. Outside it, a target's own dotted business code is a
    // deliberate domain rejection, and the contract's execution matrix calls for a
    // "контролируемый error envelope" -- so it keeps its code and message at 400,
    // the status for "the caller asked for something this action refuses". A code
    // whose last segment is not_found is the one domain rejection the contract
    // gives a status of its own (operation.get promises a controlled error, not a
    // 500, for an unknown id), so it lands on 404 instead.
    //
    // Anything not shaped like a business code cannot have come from a target that
    // knows the contract: that is a runtime failure and reports as internal.error,
    // dropping the message with it rather than forwarding text of unknown origin.
    public static ActionError ForCode(string code, string? message, bool retryable)
    {
        if (KnownCodeStatus.TryGetValue(code, out var httpStatus))
        {
            return new ActionError(httpStatus, code, message ?? code, retryable);
        }

        if (!BusinessCode.IsMatch(code))
        {
            return InternalError();
        }

        var businessStatus = code.EndsWith(".not_found", StringComparison.Ordinal) ? 404 : 400;
        return new ActionError(businessStatus, code, message ?? code, retryable);
    }
}
