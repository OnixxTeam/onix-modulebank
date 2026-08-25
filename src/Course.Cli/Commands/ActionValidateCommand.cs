using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Validation;

namespace Course.Cli.Commands;

public static class ActionValidateCommand
{
    // Never opens a database connection: validation is a pure read of the
    // manifest file against the embedded schema.
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, ISchemaValidator validator, JsonNode manifestSchema, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "action validate requires exactly one manifest path argument");
        }

        var manifestResult = await ManifestLoader.LoadAsync(command.Positionals[0], validator, manifestSchema, cancellationToken);
        if (manifestResult.Envelope is not null)
        {
            return manifestResult.Envelope;
        }

        var manifest = manifestResult.Manifest!;
        return CliEnvelope.Ok(new
        {
            resource = "action",
            operation = "validated",
            key = manifest.Key,
            version = manifest.Version,
        });
    }
}
