using System.Text.Json.Nodes;

namespace Course.Core.Actions;

// Wraps the parsed manifest document rather than a typed POCO: the document
// is validated against the canonical schema and then forwarded to Postgres
// as-is (jsonb in, jsonb out), so no C# type needs to mirror every field.
public sealed record ActionManifest(string Module, string Action, int Version, JsonNode Document)
{
    public string Key => $"{Module}.{Action}";

    public static ActionManifest FromDocument(JsonNode document)
    {
        var module = document["module"]?.GetValue<string>();
        var action = document["action"]?.GetValue<string>();
        var version = document["version"]?.GetValue<int>();

        if (module is null || action is null || version is null)
        {
            throw new InvalidOperationException("manifest is missing module, action, or version");
        }

        return new ActionManifest(module, action, version.Value, document);
    }
}
