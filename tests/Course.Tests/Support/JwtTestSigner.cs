using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Course.Tests.Support;

// Shared low-level JWT minting for both TestTokens (unit tests exercising
// JwtContextFactory directly) and Integration.ApiFactory (integration tests
// hitting the live host). Both need a raw JSON payload string rather than a
// ClaimsIdentity, so a claim can be given the "wrong" JSON type (a number or
// array where a string is expected) -- ClaimsIdentity would stringify such a
// value away before it ever reached JwtContextFactory's shape checks.
internal static class JwtTestSigner
{
    public static string Sign(string issuer, string audience, string signingKey, string rawClaimsJson)
    {
        var handler = new JsonWebTokenHandler();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);

        var now = DateTimeOffset.UtcNow;
        var payload = $$"""
            {
              "iss": "{{issuer}}",
              "aud": "{{audience}}",
              "iat": {{now.ToUnixTimeSeconds()}},
              "exp": {{now.AddMinutes(5).ToUnixTimeSeconds()}},
              {{rawClaimsJson}}
            }
            """;

        return handler.CreateToken(payload, credentials);
    }
}
