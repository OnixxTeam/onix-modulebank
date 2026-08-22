using System.Security.Cryptography;
using System.Text;
using Course.Core.Migrations;
using Npgsql;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresMigrationRunner(string connectionString) : IMigrationRunner
{
    public async Task<MigrationApplyResult> ApplyAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(directoryPath, "*.sql")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();

        var applied = new List<string>();
        var skipped = new List<string>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var checksum = ComputeChecksum(content);

            var wasApplied = await ApplyOneFileAsync(connection, fileName, checksum, content, cancellationToken);
            (wasApplied ? applied : skipped).Add(fileName);
        }

        return new MigrationApplyResult(applied, skipped);
    }

    // One transaction per file: check the journal, then either roll back (already
    // applied with a matching checksum, or a conflict) or run the file's DDL and record
    // it, then commit. The one exception is the very first file ever applied: it creates
    // migration.applied_files itself, so the initial lookup fails with "table does not
    // exist" and aborts that transaction — it has to be rolled back and replaced with a
    // fresh one before the DDL can run on this connection.
    private static async Task<bool> ApplyOneFileAsync(
        NpgsqlConnection connection, string fileName, string checksum, string content, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            string? existingChecksum;
            try
            {
                await using var select = new NpgsqlCommand(
                    "select checksum from migration.applied_files where file_name = @file_name",
                    connection,
                    transaction);
                select.Parameters.AddWithValue("file_name", fileName);
                existingChecksum = (string?)await select.ExecuteScalarAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
            {
                await transaction.RollbackAsync(cancellationToken);
                transaction = await connection.BeginTransactionAsync(cancellationToken);
                existingChecksum = null;
            }

            if (existingChecksum == checksum)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            if (existingChecksum is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new MigrationConflictException(fileName);
            }

            // `set local role` is scoped to this transaction and reverts automatically on
            // commit or rollback, so newly created objects are owned by course_owner
            // without permanently elevating the migrator's session role.
            await using (var apply = new NpgsqlCommand($"set local role course_owner;\n{content}", connection, transaction))
            {
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = new NpgsqlCommand(
                "insert into migration.applied_files (file_name, checksum) values (@file_name, @checksum)",
                connection,
                transaction))
            {
                record.Parameters.AddWithValue("file_name", fileName);
                record.Parameters.AddWithValue("checksum", checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    private static string ComputeChecksum(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
