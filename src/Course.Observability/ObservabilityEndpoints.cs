using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Course.Observability;

// What a process has to answer to be observable: whether it can serve a probe at
// all, whether its critical dependency is there, and what its queues look like.
//
// Readiness is the process's own predicate, not a fixed PostgreSQL check: the
// published contract gives `api`, both workers, both dispatchers and both
// reconcilers PostgreSQL as their critical dependency and the receipt adapter the
// gateway -- and gives *nobody* the provider, because a growing Outbox is a
// normal mode, not an outage.
public sealed record ObservabilityOptions(
    Func<CancellationToken, Task<bool>> Readiness,
    Func<CancellationToken, Task<IReadOnlyList<MetricFamily>>> Metrics)
{
    public static ObservabilityOptions ForDependency(Func<CancellationToken, Task<bool>> readiness) =>
        new(readiness, _ => Task.FromResult<IReadOnlyList<MetricFamily>>([]));
}

public static class ObservabilityEndpoints
{
    // Written out rather than serialized from an anonymous object: the verifying
    // run reads these two bodies literally, and a JSON serializer's naming or
    // property order is one refactor away from changing them.
    private const string LiveBody = """{"status":"live"}""";
    private const string ReadyBody = """{"status":"ready"}""";
    private const string NotReadyBody = """{"status":"not_ready","code":"dependency.unavailable"}""";
    private const string JsonMediaType = "application/json";

    public static void MapObservability(this IEndpointRouteBuilder routes, ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);

        // Liveness touches nothing outside the process on purpose: a probe that
        // fails when PostgreSQL is down turns one database outage into a restart
        // loop across every service that depends on it.
        routes.MapGet("/health/live", () => Results.Content(LiveBody, JsonMediaType));

        routes.MapGet("/health/ready", async (CancellationToken cancellationToken) =>
            await options.Readiness(cancellationToken)
                ? Results.Content(ReadyBody, JsonMediaType)
                : Results.Content(NotReadyBody, JsonMediaType, statusCode: StatusCodes.Status503ServiceUnavailable));

        routes.MapGet("/metrics", async (CancellationToken cancellationToken) =>
        {
            var families = await options.Metrics(cancellationToken);
            return Results.Content(OpenMetricsDocument.Render(families), OpenMetricsDocument.MediaType);
        });
    }
}

public static class PostgresReadiness
{
    // One connection per probe, taken from the pool and returned: readiness
    // recovers by itself once PostgreSQL comes back, with no state to reset after
    // a stop/start cycle.
    public static Func<CancellationToken, Task<bool>> Probe(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        return async cancellationToken =>
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("select 1", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return true;
            }
            catch (NpgsqlException)
            {
                return false;
            }
        };
    }
}
