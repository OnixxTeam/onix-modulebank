using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Migrations;
using Course.Core.Validation;

namespace Course.Cli.Commands;

public static class BootstrapCommand
{
    // Publish alone is idempotent and flag-safe (migrations 016/017): a repeat
    // run either matches the stored checksum and returns 'ok' before any write
    // (016's early branch), or -- for a brand-new module/action -- takes
    // enabled/is_default straight from the manifest without ever stealing an
    // existing default (017). Every built-in manifest carries enabled=true,
    // is_default=true, so a fresh install is callable from publish alone, no
    // follow-up activate needed.
    //
    // Bootstrap therefore never calls activate. It used to, unconditionally,
    // on every manifest on every run -- which was safe only until the first
    // operator command: after an `action disable` or `action activate` of a
    // different version, that unconditional activate would silently overwrite
    // the operator's own decision on the next `docker compose up` (GitHub
    // issue #2). Publish's own checksum-match branch has no such blind spot,
    // since it returns before touching enabled/is_default at all.
    public static async Task<CliEnvelope> ExecuteAsync(
        IMigrationRunner runner,
        IActionPublisher publisher,
        ISchemaValidator validator,
        JsonNode manifestSchema,
        string migrationsDirectory,
        string manifestsDirectory,
        CancellationToken cancellationToken)
    {
        var migrationCommand = new ParsedCommand("bootstrap", [migrationsDirectory], new Dictionary<string, string>());
        var migrationEnvelope = await MigrationApplyCommand.ExecuteAsync(migrationCommand, runner, cancellationToken);
        if (migrationEnvelope.Status != "ok")
        {
            return migrationEnvelope;
        }

        var publishedActions = new List<object>();

        // Lexicographic order so a repeat run and its logs stay stable regardless
        // of how the filesystem happens to list the directory.
        var manifestPaths = Directory.EnumerateFiles(manifestsDirectory, "*.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);

        foreach (var path in manifestPaths)
        {
            var manifestResult = await ManifestLoader.LoadAsync(path, validator, manifestSchema, cancellationToken);
            if (manifestResult.Envelope is not null)
            {
                return manifestResult.Envelope;
            }

            var manifest = manifestResult.Manifest!;

            var publishRaw = await publisher.PublishAsync(manifest, cancellationToken);
            if (publishRaw["status"]!.GetValue<string>() != "ok")
            {
                return CliEnvelope.Fail(publishRaw["code"]!.GetValue<string>(), publishRaw["message"]!.GetValue<string>());
            }

            publishedActions.Add(new { key = manifest.Key, version = manifest.Version });
        }

        return CliEnvelope.Ok(new
        {
            resource = "bootstrap",
            operation = "completed",
            migration = migrationEnvelope.Result,
            actions = publishedActions,
        });
    }
}
