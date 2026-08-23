using Course.Core.Context;
using Course.Core.Errors;

namespace Course.Core.Security;

// Same Api-depends-on-Core-interface boundary as IActionExecutor/ISchemaValidator:
// the endpoint depends on this, JwtContextFactory (Course.Infrastructure) implements it.
public interface IJwtAuthenticator
{
    Task<JwtAuthenticationResult> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken);
}

public sealed record JwtAuthenticationResult(VerifiedClaims? Claims, ActionError? Error)
{
    public static JwtAuthenticationResult Success(VerifiedClaims claims) => new(claims, null);

    public static JwtAuthenticationResult Failure(ActionError error) => new(null, error);
}
