using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Course.Core.Security;

namespace Course.Infrastructure.Security;

// HMAC-SHA256 over the exact request body bytes (SPEC "Общая граница подписи
// в C#": "Тело читается один раз в массив байтов, HMAC считается над ним").
// The secret is a byte array captured once at startup from PROVIDER_HMAC_SECRET
// -- never trimmed, never Base64/hex-decoded, exactly the bytes the environment
// variable held (the ticket's own most-common-mistake warning).
public sealed partial class ProviderSignatureVerifier(byte[] secret) : ISignatureVerifier
{
    // "v<version>=<lowercase-hex>", nothing else -- an uppercase hex digit is
    // already a format violation, not a lenient match, because it can never
    // have come from this codebase's own lowercase-hex sender (adapter/tests).
    [GeneratedRegex(@"^v(\d+)=([0-9a-f]+)$")]
    private static partial Regex HeaderPattern { get; }

    // Only version 1 exists on the wire today (receipt-v1's own signature
    // scheme); an unrecognised version is exactly as invalid as a bad HMAC,
    // per contract ("Неизвестная версия подписи даёт 401 signature.invalid").
    private const int KnownVersion = 1;

    public SignatureVerificationResult Verify(string? signatureHeader, byte[] bodyBytes)
    {
        if (signatureHeader is null)
        {
            return SignatureVerificationResult.NotPresent;
        }

        var match = HeaderPattern.Match(signatureHeader);
        if (!match.Success)
        {
            return SignatureVerificationResult.Invalid();
        }

        if (!int.TryParse(match.Groups[1].Value, out var version) || version != KnownVersion)
        {
            return SignatureVerificationResult.Invalid();
        }

        byte[] providedMac;
        try
        {
            providedMac = Convert.FromHexString(match.Groups[2].Value);
        }
        catch (FormatException)
        {
            return SignatureVerificationResult.Invalid();
        }

        var expectedMac = HMACSHA256.HashData(secret, bodyBytes);

        // CryptographicOperations.FixedTimeEquals is constant-time only when
        // both spans have the same length, which it still handles safely (a
        // length mismatch just returns false) -- but the length itself is
        // public information here, not a secret being timed, so that shortcut
        // leaks nothing the header's own hex length did not already reveal.
        if (!CryptographicOperations.FixedTimeEquals(providedMac, expectedMac))
        {
            return SignatureVerificationResult.Invalid();
        }

        return SignatureVerificationResult.Verified(version);
    }
}
