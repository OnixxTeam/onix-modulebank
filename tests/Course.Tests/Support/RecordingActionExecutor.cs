using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;

namespace Course.Tests.Support;

// A stub IActionExecutor that never touches PostgreSQL: it records every argument
// ActionEndpoint.HandleAsync passed it and returns a fixed successful result, so the
// HTTP-shaped decisions in ActionEndpoint (auth, header parsing, envelope writing)
// can be unit tested without a database.
internal sealed class RecordingActionExecutor : IActionExecutor
{
    public int CallCount { get; private set; }

    public int? ExplicitVersion { get; private set; }

    public VerifiedClaims? Claims { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public string? RequestId { get; private set; }

    public Task<ActionExecutionResult> ExecuteAsync(
        string moduleName,
        string actionName,
        int? explicitVersion,
        VerifiedClaims claims,
        Guid correlationId,
        string? requestId,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        CallCount++;
        ExplicitVersion = explicitVersion;
        Claims = claims;
        CorrelationId = correlationId;
        RequestId = requestId;

        return Task.FromResult(ActionExecutionResult.Success(1, "CREATED", new JsonObject()));
    }
}
