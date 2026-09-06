using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

// Ticket 07 acceptance, proven end to end through the real HTTP route and a
// real PostgreSQL transaction: testfixture.echo_context (required_policy: [],
// idempotency none) hands back exactly the trusted context api.invoke saw, so
// this is the one place that can prove the transport.* markers actually reach
// a target function's p_context -- not just that ActionEndpoint computed them.
[Collection("integration")]
public sealed class SignatureBoundaryTests
{
    private readonly ApiFactory factory;

    public SignatureBoundaryTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task SignedRequest_StampsTransportMarkers_OnTheTrustedContext()
    {
        using var client = factory.CreateClient();
        var body = new JsonObject().ToJsonString();
        var header = ProviderSignature.Header(ApiFactory.ProviderHmacSecret, System.Text.Encoding.UTF8.GetBytes(body));

        var response = await client.PostRawActionAsync(
            "/api/testfixture/echo_context", ApiFactory.CreateToken("candidate-client", "web", ""), body, signatureHeader: header);

        Assert.Equal(200, response.StatusCode);
        var transport = response.Body["result"]!["transport"]!;
        Assert.True(transport["signatureVerified"]!.GetValue<bool>());
        Assert.Equal(1, transport["signatureVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnsignedRequest_CarriesNoTransportMarker_AndStillSucceeds()
    {
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/testfixture/echo_context", ApiFactory.CreateToken("candidate-client", "web", ""), new JsonObject());

        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.Body["result"]!["transport"]);
    }

    [Fact]
    public async Task WrongSignature_Rejects401_TargetNeverInvoked()
    {
        using var client = factory.CreateClient();
        var body = new JsonObject().ToJsonString();

        var response = await client.PostRawActionAsync(
            "/api/testfixture/echo_context", ApiFactory.CreateToken("candidate-client", "web", ""), body,
            signatureHeader: "v1=" + new string('0', 64));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("signature.invalid", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task RegularAction_StillWorksWithoutASignatureHeader()
    {
        // Regression guard for the ticket's own biggest risk: buffering the
        // body once (ticket 02) plus checking for a signature (ticket 07)
        // must not change behavior for every week 1-2 action that never signs
        // anything at all.
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/operation/get", ApiFactory.CreateToken("candidate-client", "web", "payment:read"),
            new JsonObject { ["operationId"] = Guid.NewGuid().ToString() });

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("operation.not_found", response.Body["code"]!.GetValue<string>());
    }
}
