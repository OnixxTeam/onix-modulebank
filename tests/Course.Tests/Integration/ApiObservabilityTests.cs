using System.Diagnostics;
using System.Net;
using Course.Observability;
using Npgsql;

namespace Course.Tests.Integration;

// Ticket 07 (week 4): the wiring, not the module. ObservabilityEndpointsTests
// proves the three routes in isolation; this proves that the real `api` host
// serves them, with its own readiness predicate (PostgreSQL answering) and a
// valid -- if still empty -- metrics document. The six required series arrive
// with ticket 08.
[Collection("integration")]
public sealed class ApiObservabilityTests
{
    private readonly ApiFactory factory;

    public ApiObservabilityTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ApiLivenessSaysLive()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("""{"status":"live"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiReadinessSaysReadyWhilePostgresAnswers()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ready"}""", await response.Content.ReadAsStringAsync());
    }

    // A refused connection and a hung one are different failures: without a
    // deadline of its own the probe would wait as long as the TCP stack does and
    // the request would stall instead of answering. 10.255.255.1 is the
    // conventional blackhole address -- whichever way the attempt fails, the probe
    // owes an answer, and the answer is "not ready".
    [Fact]
    public async Task ReadinessAnswersNotReadyRatherThanHangingOnAnUnreachablePostgres()
    {
        await using var unreachable = NpgsqlDataSource.Create(
            "Host=10.255.255.1;Port=5432;Database=course;Username=course_runtime;Password=irrelevant;Timeout=60");

        var probe = PostgresReadiness.Probe(unreachable, TimeSpan.FromMilliseconds(300));

        var started = Stopwatch.StartNew();
        var ready = await probe(CancellationToken.None);
        started.Stop();

        Assert.False(ready);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"probe took {started.Elapsed}");
    }

    [Fact]
    public async Task ApiMetricsIsAValidOpenMetricsDocument()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/openmetrics-text; version=1.0.0; charset=utf-8",
            response.Content.Headers.ContentType?.ToString());
        Assert.EndsWith("# EOF\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', body);
    }
}
