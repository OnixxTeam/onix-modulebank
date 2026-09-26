using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Course.Observability;

// What a process has to answer to be observable: whether it can serve a probe at
// all, whether its critical dependency is there, and what its queues look like.
//
// Readiness is the process's own predicate, not a fixed PostgreSQL check. The
// published contract assigns each process one critical dependency -- PostgreSQL
// for `api`, the workers, the dispatchers and the reconcilers, the gateway for the
// receipt adapter -- and assigns the provider to nobody, because a growing Outbox
// is a normal mode rather than an outage. This is the canonical statement of that
// rule; the call sites do not repeat it.
public sealed record ObservabilityOptions(
    Func<CancellationToken, Task<bool>> Readiness,
    Func<CancellationToken, Task<IReadOnlyList<MetricFamily>>> Metrics)
{
    // For a process that publishes no metric family of its own: the document is
    // still rendered, and still valid, just empty.
    public static ObservabilityOptions WithoutMetrics(Func<CancellationToken, Task<bool>> readiness) =>
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
    // A hung PostgreSQL is not the same failure as a refused connection: without a
    // deadline the probe would wait as long as the TCP stack does and the request
    // would stall instead of answering. Readiness has to answer something, and the
    // contract's answer for an absent dependency is 503, so the probe carries its
    // own timeout.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // One connection per probe, taken from the pool and returned: readiness
    // recovers by itself once PostgreSQL comes back, with no state to reset after
    // a stop/start cycle.
    public static Func<CancellationToken, Task<bool>> Probe(NpgsqlDataSource dataSource, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var deadline = timeout ?? DefaultTimeout;

        return async cancellationToken =>
        {
            using var timeoutSource = new CancellationTokenSource(deadline);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(linked.Token);
                await using var command = new NpgsqlCommand("select 1", connection);
                await command.ExecuteScalarAsync(linked.Token);
                return true;
            }
            catch (NpgsqlException)
            {
                return false;
            }
            // Our own deadline means "dependency unavailable". The caller's
            // cancellation (the client gave up, the host is shutting down) is not a
            // readiness answer at all, so it keeps propagating.
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                return false;
            }
        };
    }
}
