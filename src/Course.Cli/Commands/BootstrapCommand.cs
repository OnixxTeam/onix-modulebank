using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Migrations;
using Course.Core.Validation;

namespace Course.Cli.Commands;

public static class BootstrapCommand
{
    // Baked in by the Dockerfile (COPY db/migrations -> /app/migrations, COPY
    // db/manifests -> /app/manifests), since the bootstrap service runs
    // unattended with no operator to pass directory arguments.
    private const string MigrationsDirectory = "/app/migrations";
    private const string ManifestsDirectory = "/app/manifests";

    // Publish and activate are both idempotent (migration 004/005), so a
    // repeat bootstrap on an already-provisioned database is safe: every
    // manifest either publishes clean or matches its stored checksum, and
    // activating an already-default version is a no-op update.
    public static async Task<CliEnvelope> ExecuteAsync(
        IMigrationRunner runner,
        IActionPublisher publisher,
        ISchemaValidator validator,
        JsonNode manifestSchema,
        CancellationToken cancellationToken)
    {
        var migrationCommand = new ParsedCommand("bootstrap", [MigrationsDirectory], new Dictionary<string, string>());
        var migrationEnvelope = await MigrationApplyCommand.ExecuteAsync(migrationCommand, runner, cancellationToken);
        if (migrationEnvelope.Status != "ok")
        {
            return migrationEnvelope;
        }

        var publishedActions = new List<object>();

        // Lexicographic order so a repeat run and its logs stay stable regardless
        // of how the filesystem happens to list the directory.
        var manifestPaths = Directory.EnumerateFiles(ManifestsDirectory, "*.json")
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

            var activateRaw = await publisher.ActivateAsync(manifest.Module, manifest.Action, manifest.Version, cancellationToken);
            if (activateRaw["status"]!.GetValue<string>() != "ok")
            {
                return CliEnvelope.Fail(activateRaw["code"]!.GetValue<string>(), activateRaw["message"]!.GetValue<string>());
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
