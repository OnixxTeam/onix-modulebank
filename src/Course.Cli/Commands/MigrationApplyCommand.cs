using Course.Core.Cli;
using Course.Core.Migrations;

namespace Course.Cli.Commands;

public static class MigrationApplyCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IMigrationRunner runner, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "migration apply requires exactly one directory argument");
        }

        try
        {
            var result = await runner.ApplyAsync(command.Positionals[0], cancellationToken);

            return CliEnvelope.Ok(new
            {
                resource = "migration",
                operation = "applied",
                appliedFiles = result.AppliedFiles,
                skippedFiles = result.SkippedFiles,
            });
        }
        catch (MigrationConflictException ex)
        {
            return CliEnvelope.Fail("migration.conflict", ex.Message);
        }
    }
}
