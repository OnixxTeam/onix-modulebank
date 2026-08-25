using System.Reflection;
using System.Text.Json.Nodes;

namespace Course.Cli;

// Loads the canonical action-manifest schema from the assembly it was
// compiled into (see Course.Cli.csproj EmbeddedResource), not from the
// repository checkout, so the cli image validates manifests without a bind
// mount.
public static class ManifestSchema
{
    private const string ResourceName = "action-manifest.schema.json";

    public static JsonNode Load()
    {
        using var stream = typeof(ManifestSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource not found: {ResourceName}");

        return JsonNode.Parse(stream) ?? throw new InvalidOperationException("embedded manifest schema is not valid JSON");
    }
}
