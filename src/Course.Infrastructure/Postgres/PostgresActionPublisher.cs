using System.Text.Json.Nodes;
using Course.Core.Actions;
using Npgsql;
using NpgsqlTypes;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresActionPublisher(string connectionString) : IActionPublisher
{
    public Task<JsonNode> PublishAsync(ActionManifest manifest, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select api.publish_action(@manifest)",
            command => command.Parameters.Add(new NpgsqlParameter("manifest", NpgsqlDbType.Jsonb)
            {
                Value = manifest.Document.ToJsonString(),
            }),
            cancellationToken);

    public Task<JsonNode> ListAsync(CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync("select api.list_actions()", static _ => { }, cancellationToken);

    public Task<JsonNode> ActivateAsync(string moduleName, string actionName, int version, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select api.activate_action(@module, @action, @version)",
            command =>
            {
                command.Parameters.AddWithValue("module", moduleName);
                command.Parameters.AddWithValue("action", actionName);
                command.Parameters.AddWithValue("version", version);
            },
            cancellationToken);

    public Task<JsonNode> DisableAsync(
        string moduleName, string actionName, int version, int? replacementVersion, CancellationToken cancellationToken) =>
        ExecuteScalarJsonAsync(
            "select api.disable_action(@module, @action, @version, @replacementVersion)",
            command =>
            {
                command.Parameters.AddWithValue("module", moduleName);
                command.Parameters.AddWithValue("action", actionName);
                command.Parameters.AddWithValue("version", version);
                command.Parameters.Add(new NpgsqlParameter("replacementVersion", NpgsqlDbType.Integer)
                {
                    Value = (object?)replacementVersion ?? DBNull.Value,
                });
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
