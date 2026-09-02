using System.Text.Json.Nodes;
using Course.Core.Cli;
using Workflow.Core.Maps;
using Workflow.Core.Validation;

namespace Course.Cli.Commands;

// Shared by flow validate and flow publish: both need the same read-parse-
// validate sequence before they diverge (validate stops there, publish goes
// on to canonicalize and call the database). Mirrors ManifestLoader.
internal static class FlowMapLoader
{
    public static async Task<FlowMapLoadResult> LoadAsync(
        string path, WorkflowMapValidator validator, CancellationToken cancellationToken)
    {
        JsonNode document;
        try
        {
            document = await MapLoader.LoadAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MapLoadException)
        {
            return FlowMapLoadResult.Failed(CliEnvelope.Fail("map.invalid", $"cannot read map file: {ex.Message}"));
        }

        var result = await validator.ValidateAsync(document, cancellationToken);
        if (!result.IsValid)
        {
            var first = result.Violations[0];
            var message = result.Violations.Count == 1
                ? first.Message
                : $"{first.Message} (and {result.Violations.Count - 1} more violation(s))";
            return FlowMapLoadResult.Failed(CliEnvelope.Fail(first.Code, message));
        }

        return FlowMapLoadResult.Succeeded(document, result.Map!);
    }
}

internal sealed record FlowMapLoadResult(JsonNode? Document, MapDocument? Map, CliEnvelope? Envelope)
{
    public static FlowMapLoadResult Succeeded(JsonNode document, MapDocument map) => new(document, map, null);

    public static FlowMapLoadResult Failed(CliEnvelope envelope) => new(null, null, envelope);
}
