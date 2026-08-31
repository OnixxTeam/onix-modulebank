using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Runtime;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresWorkflowJobRuntime(string connectionString) : IWorkflowJobRuntime
{
    public Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select workflow.claim_job(@owner, @leaseMs)",
            command =>
            {
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("leaseMs", leaseMs);
            },
            cancellationToken);

    public Task<JsonNode> FinishAsync(
        Guid jobId, string owner, int leaseVersion, string outcome, JsonNode result, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select workflow.finish_job(@jobId, @owner, @leaseVersion, @outcome, @result)",
            command =>
            {
                command.Parameters.AddWithValue("jobId", jobId);
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("leaseVersion", leaseVersion);
                command.Parameters.AddWithValue("outcome", outcome);
                command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = result.ToJsonString() });
            },
            cancellationToken);

    private async Task<JsonNode> ExecuteScalarJsonAsync(
        string sql, Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        addParameters(command);

        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        return JsonNode.Parse(raw)!;
    }
}
