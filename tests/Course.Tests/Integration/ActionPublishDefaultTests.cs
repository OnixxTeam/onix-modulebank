using Course.Core.Actions;

namespace Course.Tests.Integration;

// contract-reference: "`enabled` и `is_default` являются операционным состоянием
// и меняются только CLI-командами. Для route с включёнными версиями существует
// ровно одна default-версия." Publishing is not one of those commands: it may
// give a route its first default, but it may never take one away from a version
// that already holds it -- that transition belongs to activate/disable.
[Collection("integration")]
public sealed class ActionPublishDefaultTests(ApiFactory factory)
{
    [Fact]
    public async Task FirstVersion_WithDefaultManifest_BecomesTheDefault()
    {
        var action = NewAction();

        var published = await PublishAsync(action, version: 1, isDefault: true);

        Assert.Equal("ok", published["status"]!.GetValue<string>());
        Assert.Equal(1, await DefaultVersionAsync(action));
    }

    [Fact]
    public async Task FirstVersion_WithoutDefaultManifest_LeavesTheRouteWithoutOne()
    {
        var action = NewAction();

        var published = await PublishAsync(action, version: 1, isDefault: false);

        Assert.Equal("ok", published["status"]!.GetValue<string>());
        Assert.Null(await DefaultVersionAsync(action));
    }

    [Fact]
    public async Task SecondVersion_ClaimingDefault_IsRefusedAsConflict()
    {
        var action = NewAction();
        await PublishAsync(action, version: 1, isDefault: true);

        var refused = await PublishRawAsync(action, version: 2, isDefault: true);

        Assert.Equal("error", refused["status"]!.GetValue<string>());
        Assert.Equal("manifest.conflict", refused["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task SecondVersion_ClaimingDefault_WritesNothing()
    {
        var action = NewAction();
        await PublishAsync(action, version: 1, isDefault: true);

        await PublishRawAsync(action, version: 2, isDefault: true);

        // The refused version never entered the catalog, and the route's default
        // is untouched -- the refusal is atomic, not a partial write.
        Assert.Equal(0, await VersionCountAsync(action, version: 2));
        Assert.Equal(1, await DefaultVersionAsync(action));
    }

    [Fact]
    public async Task SecondVersion_NotClaimingDefault_PublishesAndLeavesTheDefaultAlone()
    {
        var action = NewAction();
        await PublishAsync(action, version: 1, isDefault: true);

        var published = await PublishAsync(action, version: 2, isDefault: false);

        Assert.Equal("ok", published["status"]!.GetValue<string>());
        Assert.Equal(1, await VersionCountAsync(action, version: 2));
        Assert.Equal(1, await DefaultVersionAsync(action));
    }

    // A republish of an unchanged manifest returns before any write, so the
    // default-claim guard must not turn the idempotent case into a conflict.
    [Fact]
    public async Task RepublishingTheSameDefaultManifest_StaysIdempotent()
    {
        var action = NewAction();
        await PublishAsync(action, version: 1, isDefault: true);

        var republished = await PublishAsync(action, version: 1, isDefault: true);

        Assert.Equal("ok", republished["status"]!.GetValue<string>());
        Assert.Equal(1, await DefaultVersionAsync(action));
    }

    // Changing a published manifest is still the immutability conflict it always
    // was, whichever guard reports it first.
    [Fact]
    public async Task RepublishingAChangedManifest_IsRefusedAsConflict()
    {
        var action = NewAction();
        await PublishAsync(action, version: 1, isDefault: true);

        var manifest = ActionManifest.FromDocument(TestManifests.Probe(action, 1, isDefault: true, timeoutMs: 6000));
        var refused = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);

        Assert.Equal("error", refused["status"]!.GetValue<string>());
        Assert.Equal("manifest.conflict", refused["code"]!.GetValue<string>());
    }

    // activate stays the way a route's default moves: it is allowed to take the
    // flag off the version that currently holds it.
    [Fact]
    public async Task Activate_StillMovesTheDefault()
    {
        var action = NewAction();
        await PublishAsync(action, version: 1, isDefault: true);
        await PublishAsync(action, version: 2, isDefault: false);

        var activated = await factory.Postgres.Publisher.ActivateAsync(
            "testfixture", action, 2, CancellationToken.None);

        Assert.Equal("ok", activated["status"]!.GetValue<string>());
        Assert.Equal(2, await DefaultVersionAsync(action));
    }

    private static string NewAction() => $"publish_probe_{Guid.NewGuid():N}"[..24];

    private async Task<JsonNode> PublishAsync(string action, int version, bool isDefault)
    {
        var result = await PublishRawAsync(action, version, isDefault);
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        return result;
    }

    private Task<JsonNode> PublishRawAsync(string action, int version, bool isDefault) =>
        factory.Postgres.Publisher.PublishAsync(
            ActionManifest.FromDocument(TestManifests.Probe(action, version, isDefault)), CancellationToken.None);

    private async Task<int?> DefaultVersionAsync(string action)
    {
        var version = await factory.Postgres.ScalarAsOwnerAsync<object>(
            "select coalesce((select version from api.action_catalog "
            + "where module = 'testfixture' and action = @action and is_default), -1)",
            ("action", action));

        return (int)version == -1 ? null : (int)version;
    }

    private async Task<int> VersionCountAsync(string action, int version) =>
        (int)await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from api.action_catalog "
            + "where module = 'testfixture' and action = @action and version = @version",
            ("action", action), ("version", version));
}
