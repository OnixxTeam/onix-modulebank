using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Validation;

namespace Course.Cli.Commands;

public static class ActionPublishCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command,
        IActionPublisher publisher,
        ISchemaValidator validator,
        JsonNode manifestSchema,
        CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "action publish requires exactly one manifest path argument");
        }

        var manifestResult = await ManifestLoader.LoadAsync(command.Positionals[0], validator, manifestSchema, cancellationToken);
        if (manifestResult.Envelope is not null)
        {
            return manifestResult.Envelope;
        }

        var manifest = manifestResult.Manifest!;
        var raw = await publisher.PublishAsync(manifest, cancellationToken);

        var status = raw["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "action",
                operation = "published",
                key = raw["key"]!.GetValue<string>(),
                version = raw["version"]!.GetValue<int>(),
            });
        }

        return CliEnvelope.Fail(raw["code"]!.GetValue<string>(), raw["message"]!.GetValue<string>());
    }
}
