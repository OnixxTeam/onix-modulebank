using System.Net;

namespace Course.Tests.Integration;

// Ticket 07 (week 4): the wiring, not the module. ObservabilityEndpointsTests
// proves the three routes in isolation; this proves that the real `api` host
// serves them, with its own readiness predicate (host initialized *and*
// PostgreSQL answering) and a valid -- if still empty -- metrics document. The
// six required series arrive with ticket 08.
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
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
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
