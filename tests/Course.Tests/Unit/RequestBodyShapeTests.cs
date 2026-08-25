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
}
