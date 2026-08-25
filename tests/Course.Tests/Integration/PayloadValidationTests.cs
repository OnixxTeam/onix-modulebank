using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class PayloadValidationTests(ApiFactory factory)
{
    // contract-reference's error matrix splits the two rejections by cause:
    // "Невалидный JSON, route или version header" is 400 request.invalid, while
    // "Payload не соответствует request schema" is 422 payload.invalid. A JSON
    // array parses, so it is a payload that violates the schema, not a broken
    // request -- 422.
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"operationKind\":\"PAYMENT_APPROVAL\",\"amount\":\"1.00\",\"currency\":\"RUB\"}]")]
    [InlineData("\"payload\"")]
    [InlineData("7")]
    public async Task NonObjectPayload_Returns422PayloadInvalid(string body)
    {
        var response = await PostRawAsync(body);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("payload.invalid", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedJson_Returns400RequestInvalid()
    {
        var response = await PostRawAsync("{");

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("request.invalid", response.Body["code"]!.GetValue<string>());
    }

    // ActionClient only sends JsonNode payloads; these cases need bodies that are
    // deliberately not an object, so the request is built by hand here.
    private async Task<ActionResponse> PostRawAsync(string body)
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payment/request")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", $"payload-shape-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new ActionResponse((int)response.StatusCode, JsonNode.Parse(text)!);
    }
}
