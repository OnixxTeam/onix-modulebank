using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Tests.Support;
using Workflow.Core.Canonicalization;

namespace Course.Tests.Integration;

// Ticket 03 acceptance: idempotent publish by canonical hash (including
// JSON vs. YAML of the same map), conflict on a real content change, no
// trace left by a rejected publish, exactly one active version at a time,
// and the SQL-side catalog recheck workflow.publish_flow does right before
// the write (CatalogRules already proves the C# validator catches the same
// facts earlier -- these tests go straight at the publisher, bypassing the
// validator, to prove the SQL function's own defense holds independently).
[Collection("integration")]
public sealed class FlowPublishListActivateTests
{
    private readonly ApiFactory factory;

    public FlowPublishListActivateTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Publish_ValidMap_Succeeds_AndIsNotActive()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();

        var published = await PublishAsync(flowName, version: 1);

        Assert.Equal("ok", published["status"]!.GetValue<string>());
        Assert.Equal(flowName, published["flowName"]!.GetValue<string>());
        Assert.Equal(1, published["flowVersion"]!.GetValue<int>());
        Assert.False(await IsActiveAsync(flowName, 1));
    }

    [Fact]
    public async Task Publish_SameMapAsJsonThenYaml_IsIdempotent_AndWritesExactlyOneVersion()
    {
        await EnsureProbeActionPublishedAsync();

        var jsonDocument = JsonNode.Parse(
            await File.ReadAllTextAsync(FixturePath("3c363d117b40-v1.flow.json")))!;
        var yamlDocument = Workflow.Core.Maps.MapLoader.Parse(
            await File.ReadAllTextAsync(FixturePath("3c363d117b40-v1.flow.yaml")));

        var flowName = jsonDocument["flow_name"]!.GetValue<string>();
        var version = jsonDocument["version"]!.GetValue<int>();

        var fromJson = await factory.Postgres.FlowPublisher.PublishAsync(
            jsonDocument, JsonCanonicalizer.Sha256Hex(jsonDocument), CancellationToken.None);
        var fromYaml = await factory.Postgres.FlowPublisher.PublishAsync(
            yamlDocument, JsonCanonicalizer.Sha256Hex(yamlDocument), CancellationToken.None);

        Assert.Equal("ok", fromJson["status"]!.GetValue<string>());
        Assert.Equal("ok", fromYaml["status"]!.GetValue<string>());
        Assert.Equal(1, await VersionCountAsync(flowName, version));
    }

    [Fact]
    public async Task Publish_ChangedNumericField_IsRefusedAsConflict_AndWritesNothing()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();
        await PublishAsync(flowName, version: 1);

        var document = BuildDocument(flowName, version: 1);
        document["steps"]![0]!["task"]!["timeout_ms"] = 1601;
        var refused = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);

        Assert.Equal("error", refused["status"]!.GetValue<string>());
        Assert.Equal("flow.conflict", refused["code"]!.GetValue<string>());
        Assert.Equal(1, await VersionCountAsync(flowName, 1));
    }

    [Fact]
    public async Task Publish_UnknownActionVersion_IsRejected_AndWritesNothing()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();

        var document = BuildDocument(flowName, version: 1);
        document["steps"]![0]!["task"]!["action_version"] = 99;
        var raw = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);

        Assert.Equal("error", raw["status"]!.GetValue<string>());
        Assert.Equal("flow.unknown_action", raw["code"]!.GetValue<string>());
        Assert.Equal(0, await VersionCountAsync(flowName, 1));
    }

    [Fact]
    public async Task Publish_DisabledActionVersion_IsRejected_AndWritesNothing()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();

        var document = BuildDocument(flowName, version: 1);
        document["steps"]![0]!["task"]!["action_version"] = 2;
        var raw = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);

        Assert.Equal("error", raw["status"]!.GetValue<string>());
        Assert.Equal("flow.disabled_action", raw["code"]!.GetValue<string>());
        Assert.Equal(0, await VersionCountAsync(flowName, 1));
    }

    [Fact]
    public async Task Publish_PolicyMismatch_IsRejected_AndWritesNothing()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();

        var document = BuildDocument(flowName, version: 1);
        document["steps"]![0]!["task"]!["required_policy"] = new JsonArray("other:scope");
        var raw = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);

        Assert.Equal("error", raw["status"]!.GetValue<string>());
        Assert.Equal("flow.policy_mismatch", raw["code"]!.GetValue<string>());
        Assert.Equal(0, await VersionCountAsync(flowName, 1));
    }

    [Fact]
    public async Task Activate_UnknownVersion_ReturnsControlledError()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();
        await PublishAsync(flowName, version: 1);

        var raw = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 2, CancellationToken.None);

        Assert.Equal("error", raw["status"]!.GetValue<string>());
        Assert.Equal("flow.not_found", raw["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Activate_SwitchesTheSingleActiveVersion_AndListReflectsIt()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();
        await PublishAsync(flowName, version: 1);
        await PublishAsync(flowName, version: 2);

        var activated1 = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        Assert.Equal("ok", activated1["status"]!.GetValue<string>());
        Assert.True(await IsActiveAsync(flowName, 1));
        Assert.False(await IsActiveAsync(flowName, 2));

        var activated2 = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 2, CancellationToken.None);
        Assert.Equal("ok", activated2["status"]!.GetValue<string>());
        Assert.False(await IsActiveAsync(flowName, 1));
        Assert.True(await IsActiveAsync(flowName, 2));

        var listed = await factory.Postgres.FlowPublisher.ListAsync(CancellationToken.None);
        var v2Entry = listed!.AsArray().Single(
            e => e!["flowName"]!.GetValue<string>() == flowName && e["flowVersion"]!.GetValue<int>() == 2);
        Assert.True(v2Entry!["isActive"]!.GetValue<bool>());
        Assert.Equal("PUBLISHED", v2Entry["status"]!.GetValue<string>());
    }

    // "Активной может быть строго одна версия flow, и это обеспечено
    // ограничением базы" -- proves flow_version_one_active (migration 020)
    // rejects a second active row even when a bug bypassed activate_flow's
    // clear-then-set dance, not just that activate_flow behaves correctly.
    [Fact]
    public async Task DatabaseConstraint_RejectsTwoActiveVersionsOfTheSameFlow()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();
        await PublishAsync(flowName, version: 1);
        await PublishAsync(flowName, version: 2);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);

        var violation = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            factory.Postgres.QueryAsOwnerAsync(async connection =>
            {
                await using var command = new Npgsql.NpgsqlCommand(
                    "update workflow.flow_version set is_active = true where flow_name = @flowName and version = 2",
                    connection);
                command.Parameters.AddWithValue("flowName", flowName);
                return await command.ExecuteNonQueryAsync();
            }));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, violation.SqlState);
    }

    // "Представление версий отдаёт объявленные колонки нужных типов" --
    // queried through course_runtime, the only role autocheck.flow_versions
    // is granted to (migration 020), proving the view is actually readable
    // through that boundary and not just declared correctly on paper.
    [Fact]
    public async Task AutocheckFlowVersionsView_ExposesTheDeclaredColumns_ThroughCourseRuntime()
    {
        await EnsureProbeActionPublishedAsync();
        var flowName = NewFlowName();
        await PublishAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);

        var row = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "select flow_name, flow_version, status, is_active, published_at "
                + "from autocheck.flow_versions where flow_name = @flowName and flow_version = @version",
                connection);
            command.Parameters.AddWithValue("flowName", flowName);
            command.Parameters.AddWithValue("version", 1);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                FlowName: reader.GetString(0),
                FlowVersion: reader.GetInt32(1),
                Status: reader.GetString(2),
                IsActive: reader.GetBoolean(3),
                PublishedAt: reader.GetFieldValue<DateTimeOffset>(4));
        });

        Assert.Equal(flowName, row.FlowName);
        Assert.Equal(1, row.FlowVersion);
        Assert.Equal("PUBLISHED", row.Status);
        Assert.True(row.IsActive);
    }

    private static string NewFlowName() => $"probe-flow-{Guid.NewGuid():N}";

    private static string FixturePath(string fileName) =>
        RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "maps", fileName);

    private static JsonNode BuildDocument(string flowName, int version)
    {
        var document = JsonNode.Parse(File.ReadAllText(FixturePath("3c363d117b40-v1.flow.json")))!;
        document["flow_name"] = flowName;
        document["version"] = version;
        return document;
    }

    private Task<JsonNode> PublishAsync(string flowName, int version) => PublishAsync(BuildDocument(flowName, version));

    private Task<JsonNode> PublishAsync(JsonNode document) =>
        factory.Postgres.FlowPublisher.PublishAsync(document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);

    private Task<bool> IsActiveAsync(string flowName, int version) =>
        factory.Postgres.ScalarAsOwnerAsync<bool>(
            "select is_active from workflow.flow_version where flow_name = @flowName and version = @version",
            ("flowName", flowName), ("version", version));

    private Task<long> VersionCountAsync(string flowName, int version) =>
        factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow.flow_version where flow_name = @flowName and version = @version",
            ("flowName", flowName), ("version", version));

    private async Task EnsureProbeActionPublishedAsync()
    {
        foreach (var name in new[] { "3c363d117b40.action.json", "3c363d117b40-disabled.action.json" })
        {
            var path = RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "manifests", name);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var manifest = ActionManifest.FromDocument(document);

            var result = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
            if (result["status"]!.GetValue<string>() != "ok")
            {
                throw new InvalidOperationException($"failed to publish fixture manifest {name}: {result.ToJsonString()}");
            }
        }
    }
}
