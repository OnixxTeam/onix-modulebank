using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class ActionDisableTests(ApiFactory factory)
{
    // contract-reference: "action disable требует replacement, если route иначе
    // останется без default". api.disable_action (migration 005) names that
    // refusal action.replacement_required -- the contract mandates the refusal,
    // not a code for it.
    [Fact]
    public async Task DisablingTheDefault_WithoutReplacement_IsRefused()
    {
        var action = ProbeRoute.NewAction("disable_probe");
        await PublishAsync(action, version: 1, isDefault: true);
        await PublishAsync(action, version: 2, isDefault: false);

        var refused = await factory.Postgres.Publisher.DisableAsync(
            ProbeRoute.Module, action, 1, null, CancellationToken.None);

        Assert.Equal("error", refused["status"]!.GetValue<string>());
        Assert.Equal("action.replacement_required", refused["code"]!.GetValue<string>());

        // Refusal leaves the route exactly as it was: v1 still enabled and default.
        var stillDefault = await factory.Postgres.ScalarAsOwnerAsync<bool>(
            "select is_default and enabled from api.action_catalog "
            + "where module = 'testfixture' and action = @action and version = 1",
            ("action", action));

        Assert.True(stillDefault);
    }

    [Fact]
    public async Task DisablingTheDefault_WithReplacement_PromotesTheReplacement()
    {
        var action = ProbeRoute.NewAction("disable_probe");
        await PublishAsync(action, version: 1, isDefault: true);
        await PublishAsync(action, version: 2, isDefault: false);

        var disabled = await factory.Postgres.Publisher.DisableAsync(
            ProbeRoute.Module, action, 1, 2, CancellationToken.None);

        Assert.Equal("ok", disabled["status"]!.GetValue<string>());

        var defaultVersion = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select version from api.action_catalog "
            + "where module = 'testfixture' and action = @action and is_default",
            ("action", action));

        Assert.Equal(2, defaultVersion);
    }

    // The last enabled version needs no replacement: the route simply stops
    // existing for clients.
    [Fact]
    public async Task DisablingTheOnlyVersion_NeedsNoReplacement()
    {
        var action = ProbeRoute.NewAction("disable_probe");
        await PublishAsync(action, version: 1, isDefault: true);

        var disabled = await factory.Postgres.Publisher.DisableAsync(
            ProbeRoute.Module, action, 1, null, CancellationToken.None);

        Assert.Equal("ok", disabled["status"]!.GetValue<string>());
    }

    private Task<JsonNode> PublishAsync(string action, int version, bool isDefault) =>
        ProbeRoute.PublishAsync(factory, action, version, isDefault);
}
