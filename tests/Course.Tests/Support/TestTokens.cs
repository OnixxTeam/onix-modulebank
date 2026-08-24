using Course.Infrastructure.Security;

namespace Course.Tests.Support;

// Fixed test-only signing identity for unit tests that exercise
// JwtContextFactory directly (TrustedContextTests) -- unrelated to the
// issuer/audience/key ApiFactory's integration tests use, which must match
// whatever Course.Api's own JwtContextFactory was configured with.
internal static class TestTokens
{
    private const string Issuer = "course-tests";
    private const string Audience = "course-tests-audience";
    private const string SigningKey = "test-signing-key-at-least-32-bytes-long!!";

    public static JwtOptions Options { get; } = new(Issuer, Audience, SigningKey);

    public static string CreateWithRawClaims(string rawClaimsJson) =>
        JwtTestSigner.Sign(Issuer, Audience, SigningKey, rawClaimsJson);
}
