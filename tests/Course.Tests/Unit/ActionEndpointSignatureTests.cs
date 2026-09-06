using System.Text;
using Course.Core.Context;
using Course.Tests.Integration;
using Course.Tests.Support;

namespace Course.Tests.Unit;

// Ticket 07 acceptance, exercised at the one place the boundary is visible to
// a caller -- the route -- per the ticket's own testing note ("подпись
// проверяется там, где её видит клиент", no separate unit tests for the
// verifier class). RecordingActionExecutor stands in for the target so these
// stay true unit tests: no database, no HTTP. Mac computation reuses
// ProviderSignature (Integration/ActionClient.cs) rather than re-deriving the
// same HMAC shape a second time.
public sealed class ActionEndpointSignatureTests
{
    private const string Secret = "harness-hmac-secret";
    private static readonly VerifiedClaims Claims = new("candidate-client", "web", ["payment:write"]);

    [Fact]
    public async Task NoSignatureHeader_ReachesExecutor_WithNoVersionStamped()
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{}", new StubJwtAuthenticator(Claims), executor);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, executor.CallCount);
        Assert.Null(executor.SignatureVersion);
        Assert.Null(executor.SignatureBodyHashHex);
    }

    [Theory]
    [InlineData("not-even-shaped-like-one")]
    [InlineData("v1=")]
    [InlineData("v1=not-hex")]
    [InlineData("V1=aabb")] // uppercase version marker: not the fixed lowercase format
    public async Task MalformedHeader_Rejects401WithoutCallingExecutor(string header)
    {
        var executor = new RecordingActionExecutor();

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{}", new StubJwtAuthenticator(Claims), executor, signatureHeader: header);

        Assert.Equal(401, result.StatusCode);
        Assert.Contains("\"code\":\"signature.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task UnknownVersion_Rejects401WithoutCallingExecutor()
    {
        var executor = new RecordingActionExecutor();
        const string body = "{}";
        var header = ProviderSignature.Header(Secret, Encoding.UTF8.GetBytes(body), version: 2);

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", body, new StubJwtAuthenticator(Claims), executor, signatureHeader: header);

        Assert.Equal(401, result.StatusCode);
        Assert.Contains("\"code\":\"signature.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task WrongMac_Rejects401WithoutCallingExecutor()
    {
        var executor = new RecordingActionExecutor();
        var header = ProviderSignature.Header(Secret, Encoding.UTF8.GetBytes("{\"different\":true}"));

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", "{}", new StubJwtAuthenticator(Claims), executor, signatureHeader: header);

        Assert.Equal(401, result.StatusCode);
        Assert.Contains("\"code\":\"signature.invalid\"", result.Body);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task ResignedReserializedBody_Rejects401()
    {
        // The ticket's own most-common mistake: signing the re-serialized
        // JSON object instead of the exact bytes on the wire. Same content,
        // different whitespace -- the MAC must not match.
        var executor = new RecordingActionExecutor();
        const string wireBody = "{ \"amount\" :  \"1.00\" }";
        var reserializedHeader = ProviderSignature.Header(Secret, Encoding.UTF8.GetBytes("{\"amount\":\"1.00\"}"));

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", wireBody, new StubJwtAuthenticator(Claims), executor, signatureHeader: reserializedHeader);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task ValidSignature_ReachesExecutor_WithVersionAndExactBodyHashStamped()
    {
        var executor = new RecordingActionExecutor();
        const string body = "{}";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = ProviderSignature.Header(Secret, bodyBytes);

        var result = await ActionEndpointHarness.PostAsync(
            "/api/payment/request", body, new StubJwtAuthenticator(Claims), executor, signatureHeader: header);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(1, executor.SignatureVersion);
        // Ticket 08's own pitfall: the stamped hash must be of the exact wire
        // bytes, not of anything reserialized -- proven here against a plain
        // SHA-256 of the literal body this test sent.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bodyBytes)), executor.SignatureBodyHashHex);
    }
}
