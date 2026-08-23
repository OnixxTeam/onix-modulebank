using System.Text.Json.Nodes;
using Course.Core.Context;
using Course.Core.Errors;

namespace Course.Core.Actions;

// The whole HTTP-triggered pipeline (manifest read, idempotency/policy checks,
// statement timeout, request validation, api.invoke, envelope/response validation,
// commit) lives behind this one call, so CommitAsync appears exactly once in the
// implementation regardless of how many endpoints end up calling it.
public interface IActionExecutor
{
    Task<ActionExecutionResult> ExecuteAsync(
        string moduleName,
        string actionName,
        int? explicitVersion,
        VerifiedClaims claims,
        Guid correlationId,
        string? requestId,
        JsonNode payload,
        CancellationToken cancellationToken);
}

public sealed record ActionExecutionResult(bool IsSuccess, int? ActionVersion, string? Outcome, JsonNode? Result, ActionError? Error)
{
    public static ActionExecutionResult Success(int actionVersion, string outcome, JsonNode result) =>
        new(true, actionVersion, outcome, result, null);

    public static ActionExecutionResult Failure(ActionError error, int? actionVersion = null) =>
        new(false, actionVersion, null, null, error);
}
