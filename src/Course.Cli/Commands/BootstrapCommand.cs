using Course.Core.Cli;
using Course.Core.Migrations;

namespace Course.Cli.Commands;

public static class BootstrapCommand
{
    // Baked in by the Dockerfile (COPY db/migrations -> /app/migrations), since the
    // bootstrap service runs unattended with no operator to pass a directory argument.
    private const string MigrationsDirectory = "/app/migrations";

    public static Task<CliEnvelope> ExecuteAsync(IMigrationRunner runner, CancellationToken cancellationToken)
    {
        var command = new ParsedCommand("bootstrap", [MigrationsDirectory], new Dictionary<string, string>());
        return MigrationApplyCommand.ExecuteAsync(command, runner, cancellationToken);
    }
}
