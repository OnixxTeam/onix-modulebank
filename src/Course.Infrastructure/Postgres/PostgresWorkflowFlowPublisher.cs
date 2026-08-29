using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Publishing;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresWorkflowFlowPublisher(string connectionString) : IWorkflowFlowPublisher
{
    public Task<JsonNode> PublishAsync(JsonNode document, string canonicalHash, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select workflow.publish_flow(@document, @hash)",
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("document", NpgsqlDbType.Jsonb) { Value = document.ToJsonString() });
                command.Parameters.AddWithValue("hash", canonicalHash);
            },
            cancellationToken);

    public Task<JsonNode> ListAsync(CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync("select workflow.list_flows()", static _ => { }, cancellationToken);

    public Task<JsonNode> ActivateAsync(string flowName, int version, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select workflow.activate_flow(@flowName, @version)",
            command =>
            {
                command.Parameters.AddWithValue("flowName", flowName);
                command.Parameters.AddWithValue("version", version);
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
