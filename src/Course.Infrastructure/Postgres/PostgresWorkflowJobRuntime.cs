using System.Text.Json.Nodes;
using Npgsql;
using Workflow.Core.Runtime;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresWorkflowJobRuntime(string connectionString) : IWorkflowJobRuntime
{
    public async Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("select workflow.claim_job(@owner, @leaseMs)", connection);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("leaseMs", leaseMs);

        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        return JsonNode.Parse(raw)!;
    }
}
