using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

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
        int? actionVersion = null)
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

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new ActionResponse((int)response.StatusCode, JsonNode.Parse(text)!);
    }
}

internal sealed record ActionResponse(int StatusCode, JsonNode Body);
