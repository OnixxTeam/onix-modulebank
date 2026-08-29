using System.Text.Json.Nodes;

namespace Workflow.Core.Maps;

// Loads the canonical course-1 workflow-map schema from the assembly it was
// compiled into (see Workflow.Core.csproj EmbeddedResource), not from the
// repository checkout, so `flow validate`/`flow publish` work without a bind
// mount -- same pattern as Course.Cli.ManifestSchema for the action manifest.
public static class MapSchema
{
    private const string ResourceName = "workflow-map.schema.json";

    public static JsonNode Load()
    {
        using var stream = typeof(MapSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource not found: {ResourceName}");

        return JsonNode.Parse(stream) ?? throw new InvalidOperationException("embedded workflow map schema is not valid JSON");
    }
}
