using System.Text;
using System.Text.Json;
using Course.Core.Context;
using Course.Core.Errors;
using Course.Core.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Course.Infrastructure.Security;

public sealed record JwtOptions(string Issuer, string Audience, string SigningKey);

// Verifies signature, issuer, audience and expiry, then checks claim shape
// explicitly on the raw JSON payload (not via ClaimsPrincipal, which stringifies
// every value and would hide a wrong JSON type). A correct signature never
// substitutes for shape checking: a numeric `sub` or `iss` fails here with the
// same 401 auth.invalid as a bad signature, never a framework 500.
public sealed class JwtContextFactory(JwtOptions options) : IJwtAuthenticator
{
    private readonly JsonWebTokenHandler handler = new();

    private readonly TokenValidationParameters validationParameters = new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    public async Task<JwtAuthenticationResult> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("missing or malformed Authorization header"));
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("missing bearer token"));
        }

        TokenValidationResult result;
        try
        {
            result = await handler.ValidateTokenAsync(token, validationParameters);
        }
        catch (Exception)
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("token validation failed"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jsonToken)
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("token signature or claims are invalid"));
        }

        // JsonWebToken.TryGetPayloadValue<string> silently coerces a wrong JSON type
        // (a number or array becomes its string rendering) instead of failing, which
        // would let a malformed claim slip past shape checking. Parsing the raw
        // base64url payload ourselves gives the real JsonValueKind for each claim.
        using var payload = ParsePayload(jsonToken);

        if (!TryGetNonEmptyString(payload, "sub", out var principal))
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("claim 'sub' must be a non-empty string"));
        }

        if (!TryGetString(payload, "consumer", out var consumer))
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("claim 'consumer' must be a string"));
        }

        if (!TryGetString(payload, "scope", out var scopeRaw))
        {
            return JwtAuthenticationResult.Failure(ActionError.AuthInvalid("claim 'scope' must be a string"));
        }

        var scopes = scopeRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return JwtAuthenticationResult.Success(new VerifiedClaims(principal, consumer, scopes));
    }

    private static JsonDocument ParsePayload(JsonWebToken token)
    {
        var base64 = token.EncodedPayload.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - (base64.Length % 4)) % 4);
        return JsonDocument.Parse(Convert.FromBase64String(base64));
    }

    private static bool TryGetNonEmptyString(JsonDocument payload, string claim, out string value) =>
        TryGetString(payload, claim, out value) && value.Length > 0;

    private static bool TryGetString(JsonDocument payload, string claim, out string value)
    {
        if (payload.RootElement.TryGetProperty(claim, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }

        value = "";
        return false;
    }
}
