using Course.Core.Context;
using Course.Core.Security;

namespace Course.Tests.Support;

// Bypasses real signature/claims verification entirely: unit tests that exercise
// ActionEndpoint use this to hand it a fixed VerifiedClaims regardless of what
// (if anything) is in the Authorization header, so they can isolate ActionEndpoint's
// own logic from JwtContextFactory's (covered separately in TrustedContextTests).
internal sealed class StubJwtAuthenticator(VerifiedClaims claims) : IJwtAuthenticator
{
    public Task<JwtAuthenticationResult> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken) =>
        Task.FromResult(JwtAuthenticationResult.Success(claims));
}
