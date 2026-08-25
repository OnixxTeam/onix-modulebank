using Course.Core.Context;
using Course.Tests.Support;

namespace Course.Tests.Unit;

public sealed class ActionVersionHeaderTests
{
    private static readonly VerifiedClaims Claims = new("candidate-client", "web", ["payment:write"]);

    [Fact]
    public async Task NoHeader_PassesNullExplicitVersion()
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{}", new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, executor.CallCount);
        Assert.Null(executor.ExplicitVersion);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    public async Task InvalidHeader_Rejects400WithoutCallingExecutor(string headerValue)
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{}", new StubJwtAuthenticator(Claims), executor, headerValue);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("\"code\":\"request.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task ValidHeader_PassesParsedVersion()
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{}", new StubJwtAuthenticator(Claims), executor, "2");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, executor.ExplicitVersion);
    }
}
