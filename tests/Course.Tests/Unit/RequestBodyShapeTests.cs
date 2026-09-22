using System.Text;
using Course.Api.Endpoints;
using Course.Core.Context;
using Course.Tests.Support;

namespace Course.Tests.Unit;

// What the endpoint itself refuses before any action is resolved: only bodies
// that are not a JSON document at all. Anything that parses -- including an
// array or a bare scalar -- is a payload as far as this layer is concerned and
// goes to the executor, where request_schema decides (see
// PayloadValidationTests for the 422 that produces).
public sealed class RequestBodyShapeTests
{
    private static readonly VerifiedClaims Claims = new("candidate-client", "web", ["payment:write"]);

    [Fact]
    public async Task MalformedJson_Rejects400WithoutCallingExecutor()
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{", new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("\"code\":\"request.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task NullDocument_Rejects400WithoutCallingExecutor()
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "null", new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("\"code\":\"request.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task EmptyBody_Rejects400WithoutCallingExecutor()
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "", new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("\"code\":\"request.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task OversizedBody_Rejects413WithoutCallingExecutor()
    {
        var executor = new RecordingActionExecutor();
        var oversized = "{\"padding\":\"" + new string('x', 1_048_576) + "\"}";

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", oversized, new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(413, result.StatusCode);
        Assert.Contains("\"code\":\"request.too_large\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Theory]
    [InlineData("{\"amount\":\"1.00\"}")]
    [InlineData("[]")]
    [InlineData("7")]
    public async Task ParseableBody_ReachesExecutor(string body)
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", body, new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, executor.CallCount);
    }

    // Week 3's signature boundary (ticket 07) verifies HMAC over the exact
    // bytes the sender transmitted -- it can only do that if this endpoint
    // stashed them verbatim rather than re-serializing the parsed JsonNode.
    [Fact]
    public async Task ParseableBody_StashesExactRawBytes()
    {
        var executor = new RecordingActionExecutor();
        const string body = "{ \"amount\" :  \"1.00\" }";

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", body, new StubJwtAuthenticator(Claims), executor);

        var stashed = Assert.IsType<byte[]>(result.Context.Items[ActionEndpoint.RawBodyItemKey]);
        Assert.Equal(Encoding.UTF8.GetBytes(body), stashed);
    }
}
