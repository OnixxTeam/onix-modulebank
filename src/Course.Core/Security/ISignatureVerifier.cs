using Course.Core.Errors;

namespace Course.Core.Security;

// Same Api-depends-on-Core-interface boundary as IJwtAuthenticator/IActionExecutor:
// the endpoint depends on this, ProviderSignatureVerifier (Course.Infrastructure)
// implements it. The check is deliberately generic -- it never sees a module or
// action name, only the raw header value and the exact body bytes the sender
// transmitted (ticket 02's own RawBodyItemKey).
public interface ISignatureVerifier
{
    SignatureVerificationResult Verify(string? signatureHeader, byte[] bodyBytes);
}

// Version is non-null exactly when a signature was present and verified --
// the one fact ActionEndpoint needs to decide whether to stamp trusted-context
// markers at all. Absent header is not an error (most actions never sign
// anything); a present-but-wrong header always is.
public sealed record SignatureVerificationResult(int? Version, ActionError? Error)
{
    public static SignatureVerificationResult NotPresent { get; } = new(null, null);

    public static SignatureVerificationResult Verified(int version) => new(version, null);

    public static SignatureVerificationResult Invalid() => new(null, ActionError.SignatureInvalid());
}
