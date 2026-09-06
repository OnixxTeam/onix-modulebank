using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

// Ticket 07: builds the same "v1=<lowercase-hex-hmac>" header value the real
// receipt-adapter will send (ticket 12), over whatever exact body bytes the
// test is about to POST -- never over a re-serialized JsonNode, which is
// exactly the bug the boundary itself exists to reject.
internal static class ProviderSignature
{
    public static string Header(string secret, byte[] bodyBytes, int version = 1) =>
        $"v{version}={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bodyBytes))}";
}

// Thin POST /api/{module}/{action} wrapper shared by every integration test:
// builds the request exactly as contract-reference's "HTTP API" section
// describes it (bearer token, optional Idempotency-Key/X-Action-Version) and
// parses the JSON envelope back.
internal static class ActionClient
{
    public static async Task<ActionResponse> PostActionAsync(
        this HttpClient client,
        string path,
        string token,
        JsonNode payload,
        string? idempotencyKey = null,
        int? actionVersion = null,
        string? signatureHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (actionVersion is not null)
        {
            request.Headers.Add("X-Action-Version", actionVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (signatureHeader is not null)
        {
            request.Headers.Add("X-Provider-Signature", signatureHeader);
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new ActionResponse((int)response.StatusCode, JsonNode.Parse(text)!);
    }

    // Same request, but with the body written out by hand: property order and
    // whitespace are what some tests are about, and a JsonNode round trip would
    // normalise exactly those away.
    public static async Task<ActionResponse> PostRawActionAsync(
        this HttpClient client, string path, string token, string body, string? idempotencyKey = null, string? signatureHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (signatureHeader is not null)
        {
            request.Headers.Add("X-Provider-Signature", signatureHeader);
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new ActionResponse((int)response.StatusCode, JsonNode.Parse(text)!);
    }
}

internal sealed record ActionResponse(int StatusCode, JsonNode Body);
