using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Workflow.Core.Maps;

// The one place JSON and YAML converge into a single JsonNode representation
// before validation ever sees the document -- so the schema, the semantic
// validator, and the publisher each run against exactly one shape regardless
// of which encoding the author submitted. `path` may be a real file or a
// device path like /dev/stdin; File.ReadAllTextAsync reads either the same way.
public static class MapLoader
{
    public static async Task<JsonNode> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return Parse(text);
    }

    public static JsonNode Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) ?? throw new MapLoadException("document is not valid JSON");
        }
        catch (System.Text.Json.JsonException)
        {
            return ParseYaml(text);
        }
    }

    private static JsonNode ParseYaml(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new MapLoadException($"document is not valid JSON or YAML: {ex.Message}");
        }

        if (stream.Documents.Count == 0)
        {
            throw new MapLoadException("document is empty");
        }

        return ToJsonNode(stream.Documents[0].RootNode) ?? throw new MapLoadException("document is empty");
    }

    // A quoted scalar (`"1"`, `'true'`) is always a string per the YAML spec --
    // only a Plain-style scalar gets the JSON-core-schema implicit typing
    // (null/bool/int/float), matching how a JSON parser would read the same
    // literal. YamlDotNet's representation model does not resolve tags on its
    // own, so this reimplements that one narrow slice of it rather than
    // pulling in the full resolver machinery.
    private static JsonNode? ToJsonNode(YamlNode node) => node switch
    {
        YamlScalarNode scalar => ScalarToJsonNode(scalar),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJsonNode).ToArray()),
        YamlMappingNode mapping => ToJsonObject(mapping),
        _ => throw new MapLoadException($"unsupported YAML node kind: {node.NodeType}"),
    };

    private static JsonObject ToJsonObject(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode key || key.Value is null)
            {
                throw new MapLoadException("YAML mapping keys must be scalar strings");
            }

            result[key.Value] = ToJsonNode(valueNode);
        }

        return result;
    }

    private static JsonValue? ScalarToJsonNode(YamlScalarNode scalar)
    {
        if (scalar.Value is null)
        {
            return null;
        }

        if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain)
        {
            return JsonValue.Create(scalar.Value);
        }

        var value = scalar.Value;
        return value switch
        {
            "" or "~" or "null" or "Null" or "NULL" => null,
            "true" or "True" or "TRUE" => JsonValue.Create(true),
            "false" or "False" or "FALSE" => JsonValue.Create(false),
            // Prefer int over long: JsonNode.Parse's own number nodes expose
            // GetValue<int>() for any in-range integer literal, and MapParser
            // calls GetValue<int>() -- a YAML-sourced JsonValue<long> would
            // throw there even for a value that fits in an int.
            _ when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => JsonValue.Create(i),
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
            _ => JsonValue.Create(value),
        };
    }
}

public sealed class MapLoadException(string message) : Exception(message);
