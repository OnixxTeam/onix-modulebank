using System.Text.Json.Nodes;
using Course.Core.Actions;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class CatalogTests(ApiFactory factory)
{
    // contract-reference matrix: "Новый зарегистрированный action -> 200,
    // объявленный outcome, schema-valid result -> Commit target-изменения ->
    // Action доступен без изменения images gateway/API." Publishes and
    // activates testfixture.echo through the same CLI-layer code path
    // (IActionPublisher) BootstrapCommand uses, against the already-running
    // Course.Api host -- no rebuild, no restart.
    [Fact]
    public async Task NewlyPublishedAction_IsCallableWithoutRebuilding()
    {
        var manifest = ActionManifest.FromDocument(TestManifests.Echo());

        var publishResult = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
        Assert.Equal("ok", publishResult["status"]!.GetValue<string>());

        var activateResult = await factory.Postgres.Publisher.ActivateAsync(
            manifest.Module, manifest.Action, manifest.Version, CancellationToken.None);
        Assert.Equal("ok", activateResult["status"]!.GetValue<string>());

        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var response = await client.PostActionAsync(
            "/api/testfixture/echo", token, new JsonObject { ["note"] = "no rebuild needed" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ECHOED", response.Body["outcome"]!.GetValue<string>());
        Assert.Equal("no rebuild needed", response.Body["result"]!["note"]!.GetValue<string>());
    }
}
