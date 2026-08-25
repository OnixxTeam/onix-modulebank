using Course.Core.Context;
using Course.Infrastructure.Security;
using Course.Tests.Support;

namespace Course.Tests.Unit;

public sealed class TrustedContextTests
{
    // A correct signature never substitutes for shape checking: a claim of the
    // wrong JSON type must still fail as 401 auth.invalid, not slip through and
    // crash later as a framework 500.
    [Theory]
    [InlineData("""
        "consumer": "web", "scope": "payment:write"
        """)] // sub missing entirely
    [InlineData("""
        "sub": "candidate-client", "consumer": 42, "scope": "payment:write"
        """)] // consumer is a number, not a string
    [InlineData("""
        "sub": "candidate-client", "consumer": "web", "scope": ["payment:write"]
        """)] // scope is an array, not a string
    public async Task MalformedClaims_YieldAuthInvalid(string rawClaimsJson)
    {
        var factory = new JwtContextFactory(TestTokens.Options);
        var token = TestTokens.CreateWithRawClaims(rawClaimsJson);

        var result = await factory.AuthenticateAsync($"Bearer {token}", CancellationToken.None);

        Assert.Null(result.Claims);
        Assert.NotNull(result.Error);
        Assert.Equal("auth.invalid", result.Error!.Code);
        Assert.Equal(401, result.Error.HttpStatus);
    }

    // TrustedContext is built solely from the verified JWT claims, a fresh
    // correlationId, and the Idempotency-Key header -- never from the request
    // body, so a payload cannot impersonate a different principal or inject its
    // own correlationId by naming fields the same as the context.
    [Fact]
    public async Task PayloadFields_DoNotOverrideTrustedContext()
    {
        var jwtClaims = new VerifiedClaims("alice-from-jwt", "jwt-consumer", ["payment:write"]);
        var executor = new RecordingActionExecutor();

        var body = """
            {
              "principal": "mallory",
              "consumer": "attacker-consumer",
              "scopes": ["admin"],
              "correlationId": "11111111-1111-1111-1111-111111111111"
            }
            """;

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", body, new StubJwtAuthenticator(jwtClaims), executor);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal("alice-from-jwt", executor.Claims!.Principal);
        Assert.Equal("jwt-consumer", executor.Claims.Consumer);
        Assert.Equal(["payment:write"], executor.Claims.Scopes);
        Assert.NotEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), executor.CorrelationId);
        Assert.Null(executor.RequestId); // no Idempotency-Key header was sent, so it stays null despite the payload's field
    }
}
