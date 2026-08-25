using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Validation;

namespace Course.Cli.Commands;

// Shared by action validate and action publish: both need the same file-read,
// parse, and schema-validate sequence before they diverge (validate stops
// there, publish goes on to call the database).
internal static class ManifestLoader
{
    public static async Task<ManifestLoadResult> LoadAsync(
        string path, ISchemaValidator validator, JsonNode manifestSchema, CancellationToken cancellationToken)
    {
        JsonNode? document;
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            document = JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ManifestLoadResult.Failed(CliEnvelope.Fail("manifest.invalid", $"cannot read manifest file: {ex.Message}"));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ManifestLoadResult.Failed(CliEnvelope.Fail("manifest.invalid", $"manifest is not valid JSON: {ex.Message}"));
        }

        if (document is null)
        {
            return ManifestLoadResult.Failed(CliEnvelope.Fail("manifest.invalid", "manifest is not valid JSON"));
        }

        var validation = validator.Validate(manifestSchema, document);
        if (!validation.IsValid)
        {
            return ManifestLoadResult.Failed(CliEnvelope.Fail(
                "manifest.invalid", $"manifest does not match action-manifest schema: {string.Join("; ", validation.Errors)}"));
        }

        return ManifestLoadResult.Succeeded(ActionManifest.FromDocument(document));
    }
}

internal sealed record ManifestLoadResult(ActionManifest? Manifest, CliEnvelope? Envelope)
{
    public static ManifestLoadResult Succeeded(ActionManifest manifest) => new(manifest, null);

    public static ManifestLoadResult Failed(CliEnvelope envelope) => new(null, envelope);
}
