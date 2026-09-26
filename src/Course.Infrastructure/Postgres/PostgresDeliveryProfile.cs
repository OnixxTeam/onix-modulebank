using System.Text.Json.Nodes;
using Course.Core.Delivery;
using Npgsql;

namespace Course.Infrastructure.Postgres;

public sealed class PostgresDeliveryProfile(string connectionString) : IDeliveryProfile
{
    public async Task<JsonNode> SetLeaseMsAsync(int leaseMs, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("select delivery.set_lease_ms(@leaseMs)", connection);
        command.Parameters.AddWithValue("leaseMs", leaseMs);

        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        return JsonNode.Parse(raw)!;
    }
}
