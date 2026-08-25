using System.Text.Json.Nodes;
using Course.Core.Actions;

namespace Course.Tests.Integration;

// One disposable route per test: publish/activate/disable transitions mutate
// catalog state that outlives the test, so tests that exercise them must not
// share a route with each other or with the domain actions PostgresFixture
// publishes. Every route made here is named after the test that made it and is
// simply abandoned afterwards.
internal static class ProbeRoute
{
    public const string Module = "testfixture";

    // Prefix plus a full uuid stays inside the 63-character sqlIdentifier
    // pattern the manifest schema enforces (action-manifest.schema.json,
    // $defs/sqlIdentifier), so no truncation is needed to keep it publishable.
    public static string NewAction(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public static async Task<JsonNode> PublishAsync(
        ApiFactory factory, string action, int version, bool isDefault, bool enabled = true)
    {
        var result = await PublishRawAsync(factory, action, version, isDefault, enabled);
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        return result;
    }

    public static Task<JsonNode> PublishRawAsync(
        ApiFactory factory, string action, int version, bool isDefault, bool enabled = true, int timeoutMs = 5000)
    {
        var manifest = ActionManifest.FromDocument(
            TestManifests.Probe(action, version, isDefault, enabled, timeoutMs));

        return factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
    }

    // -1 rather than null: ScalarAsOwnerAsync casts the scalar to T, so a route
    // with no default row has to come back as a value, not as sql null.
    public static async Task<int?> DefaultVersionAsync(ApiFactory factory, string action)
    {
        var version = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select coalesce((select version from api.action_catalog "
            + $"where module = '{Module}' and action = @action and is_default), -1)",
            ("action", action));

        return version == -1 ? null : version;
    }

    public static async Task<int> VersionCountAsync(ApiFactory factory, string action, int version) =>
        (int)await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from api.action_catalog "
            + $"where module = '{Module}' and action = @action and version = @version",
            ("action", action), ("version", version));
}
