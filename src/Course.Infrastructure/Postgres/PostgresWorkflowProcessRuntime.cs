using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Runtime;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresWorkflowProcessRuntime(string connectionString) : IWorkflowProcessRuntime
{
    public Task<JsonNode> StartAsync(
        string flowName, string businessKey, JsonNode startData, string startDataHash, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select workflow.start_process(@flowName, @businessKey, @startData, @startDataHash)",
            command =>
            {
                command.Parameters.AddWithValue("flowName", flowName);
                command.Parameters.AddWithValue("businessKey", businessKey);
                command.Parameters.Add(new NpgsqlParameter("startData", NpgsqlDbType.Jsonb) { Value = startData.ToJsonString() });
                command.Parameters.AddWithValue("startDataHash", startDataHash);
            },
            cancellationToken);

    public Task<JsonNode> GetCompactAsync(Guid processId, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select workflow.get_process_compact(@processId)",
            command => command.Parameters.AddWithValue("processId", processId),
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
