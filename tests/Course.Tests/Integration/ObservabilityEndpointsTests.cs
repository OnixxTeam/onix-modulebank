using System.Net;
using Course.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Course.Tests.Integration;

// Ticket 07 (week 4): the three routes as the verifying run sees them -- over
// HTTP, on a real Kestrel, with the status code, the media type and the body it
// reads literally. The readiness predicate is the only thing each process
// supplies, so both of its answers are proven here once rather than per process.
public sealed class ObservabilityEndpointsTests
{
    [Fact]
    public async Task LivenessIsTwoHundredJsonAndSaysLive()
    {
        await using var host = await ObservabilityHost.StartAsync(readiness: () => true);

        var response = await host.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"live"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadinessIsTwoHundredJsonAndSaysReadyWhenTheDependencyAnswers()
    {
        await using var host = await ObservabilityHost.StartAsync(readiness: () => true);

        var response = await host.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"ready"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadinessIsFiveOhThreeWithTheContractCodeWhenTheDependencyIsDown()
    {
        await using var host = await ObservabilityHost.StartAsync(readiness: () => false);

        var response = await host.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """{"status":"not_ready","code":"dependency.unavailable"}""",
            await response.Content.ReadAsStringAsync());
    }

    // Liveness stays 200 while the dependency is gone: a probe that fails when
    // PostgreSQL is down turns one database outage into a restart loop across
    // every process that depends on it.
    [Fact]
    public async Task LivenessStaysTwoHundredWhileReadinessIsDown()
    {
        await using var host = await ObservabilityHost.StartAsync(readiness: () => false);

        var live = await host.GetAsync("/health/live");
        var ready = await host.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task MetricsUsesTheExactOpenMetricsMediaTypeAndEndsWithEof()
    {
        await using var host = await ObservabilityHost.StartAsync(
            readiness: () => true,
            metrics: () =>
            [
                new MetricFamily("workflow_jobs_ready", MetricType.Gauge, "", [new MetricSample(2)]),
                new MetricFamily("workflow_failures", MetricType.Counter, "", [new MetricSample(1)]),
            ]);

        var response = await host.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/openmetrics-text; version=1.0.0; charset=utf-8",
            response.Content.Headers.ContentType?.ToString());
        Assert.Contains("workflow_jobs_ready 2\n", body, StringComparison.Ordinal);
        Assert.Contains("workflow_failures_total 1\n", body, StringComparison.Ordinal);
        Assert.EndsWith("# EOF\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', body);
    }

    [Fact]
    public async Task MetricsIsAValidDocumentWithNoFamiliesAtAll()
    {
        await using var host = await ObservabilityHost.StartAsync(readiness: () => true);

        var response = await host.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("# EOF\n", await response.Content.ReadAsStringAsync());
    }

    // One real Kestrel on a loopback port the OS picks: the media type and the
    // exact bytes of the body are what the contract fixes, and TestServer's
    // in-memory pipeline is one layer short of proving them.
    private sealed class ObservabilityHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly HttpClient client;

        private ObservabilityHost(WebApplication app, HttpClient client)
        {
            this.app = app;
            this.client = client;
        }

        public static async Task<ObservabilityHost> StartAsync(
            Func<bool> readiness,
            Func<IReadOnlyList<MetricFamily>>? metrics = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            app.MapObservability(new ObservabilityOptions(
                _ => Task.FromResult(readiness()),
                _ => Task.FromResult(metrics is null ? [] : metrics())));

            await app.StartAsync();

            var address = app.Urls.First();
            return new ObservabilityHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public Task<HttpResponseMessage> GetAsync(string path) => client.GetAsync(path);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
