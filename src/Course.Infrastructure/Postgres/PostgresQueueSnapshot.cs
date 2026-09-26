using System.Text.Json.Nodes;
using Npgsql;

namespace Course.Infrastructure.Postgres;

/// <summary>One scrape's worth of queue state, as PostgreSQL saw it in one snapshot.</summary>
public sealed record QueueSnapshot(
    long JobsReady,
    double JobOldestAgeSeconds,
    long ProcessesWaiting,
    long OutboxPending,
    double OutboxOldestAgeSeconds,
    long FailuresTotal)
{
    public static QueueSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

// Reads the six required series from the one registered function that computes
// them. The C# side does no arithmetic and knows no table: which states count as
// claimable, and what a terminal workflow failure is, are decisions that belong
// next to the data (migration 047) rather than in a scrape handler.
public sealed class PostgresQueueSnapshot(NpgsqlDataSource dataSource)
{
    public async Task<QueueSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select observability.queue_snapshot()", connection);

        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (raw is null)
        {
            return QueueSnapshot.Empty;
        }

        var snapshot = JsonNode.Parse(raw)!;

        return new QueueSnapshot(
            JobsReady: snapshot["workflowJobsReady"]!.GetValue<long>(),
            JobOldestAgeSeconds: snapshot["workflowJobOldestAgeSeconds"]!.GetValue<double>(),
            ProcessesWaiting: snapshot["workflowProcessesWaiting"]!.GetValue<long>(),
            OutboxPending: snapshot["outboxPending"]!.GetValue<long>(),
            OutboxOldestAgeSeconds: snapshot["outboxOldestAgeSeconds"]!.GetValue<double>(),
            FailuresTotal: snapshot["workflowFailuresTotal"]!.GetValue<long>());
    }
}
