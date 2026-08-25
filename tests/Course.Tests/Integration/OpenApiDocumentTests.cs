using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Course.Core.Actions;

namespace Course.Tests.Integration;

// contract-reference: "Версия передаётся только в `X-Action-Version`. Без
// заголовка выбирается default version." A document that describes one version
// of a route has to say which one, and how a caller asks for it -- otherwise the
// exact-version documents are indistinguishable from each other.
[Collection("integration")]
public sealed class OpenApiDocumentTests(ApiFactory factory)
{
    [Fact]
    public async Task DefaultDocument_DeclaresTheVersionHeaderOfTheDefaultVersion()
    {
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonNode>("/openapi/default.json");

        var parameter = VersionParameter(document!, "/api/payment/request");
        Assert.Equal("header", parameter["in"]!.GetValue<string>());
        Assert.False(parameter["required"]!.GetValue<bool>());
        Assert.Equal(1, parameter["schema"]!["const"]!.GetValue<int>());
    }

    [Fact]
    public async Task VersionDocument_DeclaresTheRequestedVersion()
    {
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonNode>("/openapi/actions/payment/request/1.json");

        Assert.Equal(1, VersionParameter(document!, "/api/payment/request")["schema"]!["const"]!.GetValue<int>());
    }

    // The document is assembled from the catalog on every request, so a default
    // moved by activate shows up without rebuilding or restarting the API.
    [Fact]
    public async Task DefaultDocument_FollowsActivateWithoutRestart()
    {
        var action = $"openapi_probe_{Guid.NewGuid():N}"[..24];
        await PublishAsync(action, version: 1, isDefault: true);
        await PublishAsync(action, version: 2, isDefault: false);

        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<JsonNode>("/openapi/default.json");
        Assert.Equal(1, VersionParameter(before!, $"/api/testfixture/{action}")["schema"]!["const"]!.GetValue<int>());

        var activated = await factory.Postgres.Publisher.ActivateAsync("testfixture", action, 2, CancellationToken.None);
        Assert.Equal("ok", activated["status"]!.GetValue<string>());

        var after = await client.GetFromJsonAsync<JsonNode>("/openapi/default.json");
        Assert.Equal(2, VersionParameter(after!, $"/api/testfixture/{action}")["schema"]!["const"]!.GetValue<int>());
    }

    // Version documents are addressed by exact (module, action, version) and are
    // not restricted to enabled rows, so a disabled version still describes the
    // header that reaches it.
    [Fact]
    public async Task VersionDocument_DeclaresTheVersionEvenWhenNotDefault()
    {
        var action = $"openapi_probe_{Guid.NewGuid():N}"[..24];
        await PublishAsync(action, version: 1, isDefault: true);
        await PublishAsync(action, version: 2, isDefault: false);

        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonNode>($"/openapi/actions/testfixture/{action}/2.json");

        Assert.Equal(2, VersionParameter(document!, $"/api/testfixture/{action}")["schema"]!["const"]!.GetValue<int>());
    }

    private static JsonNode VersionParameter(JsonNode document, string path)
    {
        var parameters = document["paths"]![path]!["post"]!["parameters"]!.AsArray();
        return Assert.Single(parameters, p => p!["name"]!.GetValue<string>() == "X-Action-Version")!;
    }

    private async Task PublishAsync(string action, int version, bool isDefault)
    {
        var manifest = ActionManifest.FromDocument(TestManifests.Probe(action, version, isDefault));
        var result = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
        Assert.Equal("ok", result["status"]!.GetValue<string>());
    }
}
