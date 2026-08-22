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

            var existingChecksum = await TryGetAppliedChecksumAsync(connection, fileName, cancellationToken);

            if (existingChecksum == checksum)
            {
                skipped.Add(fileName);
                continue;
            }

            if (existingChecksum is not null)
            {
                throw new MigrationConflictException(fileName);
            }

            await ApplyFileAsync(connection, fileName, checksum, content, cancellationToken);
            applied.Add(fileName);
        }

        return new MigrationApplyResult(applied, skipped);
    }

    // migration.applied_files does not exist until 001_schemas.sql itself has run,
    // so the very first lookup must treat "table missing" the same as "not applied yet".
    private static async Task<string?> TryGetAppliedChecksumAsync(
        NpgsqlConnection connection, string fileName, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var command = new NpgsqlCommand(
                "select checksum from migration.applied_files where file_name = @file_name",
                connection,
                transaction);
            command.Parameters.AddWithValue("file_name", fileName);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result as string;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
    }

    private static async Task ApplyFileAsync(
        NpgsqlConnection connection, string fileName, string checksum, string content, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // `set local role` is scoped to this transaction and reverts automatically on
        // commit or rollback, so newly created objects are owned by course_owner without
        // permanently elevating the migrator's session role.
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
    }

    private static string ComputeChecksum(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
