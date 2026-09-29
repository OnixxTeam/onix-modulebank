using Course.Cli;
using Course.Cli.Commands;
using Course.Tests.Support;
using Workflow.Core.Catalog;
using Workflow.Core.Maps;
using Workflow.Core.Security;
using Workflow.Core.Validation;

namespace Course.Tests.Integration;

// Ticket 02 acceptance: "Проверить командой все карты из открытых фикстур:
// обе годные принимаются, все негодные отвергаются с осмысленным кодом."
// Runs WorkflowMapValidator over every fixture under autocheck/fixtures/maps
// against the real probe action published into the catalog -- the same
// module_5abe942c84.action_5cd90325f0 v1 every invalid/semantic fixture
// targets, so the catalog-dependent rules (unknown action, disabled version,
// policy mismatch, outcome coverage) exercise a real DB read, not a stub.
[Collection("integration")]
public sealed class FlowValidateFixtureTests
{
    // Compiled exactly once for the whole test process: Json.Schema.Net
    // registers a schema's $id into its process-wide registry the first time
    // it is deserialized and throws on a second registration of the same
    // $id (see SchemaValidationTests), so every fixture case in this class
    // must share one JsonSchemaValidator/schema pair rather than build fresh.
    private static readonly Course.Infrastructure.Validation.JsonSchemaValidator SchemaValidator = new();
    private static readonly System.Text.Json.Nodes.JsonNode MapSchemaDocument = MapSchema.Load();

    private readonly ApiFactory factory;

    public FlowValidateFixtureTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    public static IEnumerable<object[]> ValidFixtures() =>
        Directory.GetFiles(RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "maps"), "*.flow.*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { path });

    public static IEnumerable<object[]> InvalidFixtures() =>
        Directory.GetFiles(RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "maps", "invalid"), "*.flow.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public async Task ValidFixture_IsAccepted(string path)
    {
        await EnsureProbeManifestPublishedAsync();

        var validator = BuildValidator();
        var document = await MapLoader.LoadAsync(path, CancellationToken.None);
        var result = await validator.ValidateAsync(document, CancellationToken.None);

        Assert.True(result.IsValid, $"{Path.GetFileName(path)}: {string.Join("; ", result.Violations.Select(v => v.Message))}");
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public async Task InvalidFixture_IsRejected(string path)
    {
        await EnsureProbeManifestPublishedAsync();

        var validator = BuildValidator();
        var document = await MapLoader.LoadAsync(path, CancellationToken.None);
        var result = await validator.ValidateAsync(document, CancellationToken.None);

        Assert.False(result.IsValid, $"{Path.GetFileName(path)} was expected to be rejected");
        Assert.NotEmpty(result.Violations);
        Assert.All(result.Violations, v => Assert.False(string.IsNullOrWhiteSpace(v.Code)));
    }

    // Ticket 13 acceptance: after a clean boot, "опубликованные и активированные
    // карты" must already be true with no manual host command. Runs the real
    // BootstrapCommand.ExecuteAsync -- same schema-validator instances as the
    // fixture tests above, reused for the same $id-registry reason -- against
    // an empty manifests directory (this test only cares about the flow-map
    // half of bootstrap; PostgresFixture's own setup already published every
    // real manifest with a matching checksum, so re-running that half here
    // would just be redundant, not wrong).
    [Fact]
    public async Task Bootstrap_WithFlowMapsDirectory_PublishesAndActivatesTheProductionPaymentFlows()
    {
        var emptyManifestsDirectory = Directory.CreateTempSubdirectory("bootstrap-flow-activation-tests-").FullName;

        var envelope = await BootstrapCommand.ExecuteAsync(
            factory.Postgres.MigrationRunner,
            factory.Postgres.Publisher,
            new Course.Infrastructure.Validation.JsonSchemaValidator(),
            ManifestSchema.Load(),
            RepoPaths.Combine("db", "migrations"),
            emptyManifestsDirectory,
            CancellationToken.None,
            flowMapValidator: BuildValidator(),
            flowPublisher: factory.Postgres.FlowPublisher,
            flowMapsDirectory: RepoPaths.Combine("db", "maps"));

        Assert.Equal("ok", envelope.Status);

        var flows = await factory.Postgres.FlowPublisher.ListAsync(CancellationToken.None);
        var activeFlowNames = flows!.AsArray()
            .Where(entry => entry!["isActive"]!.GetValue<bool>())
            .Select(entry => entry!["flowName"]!.GetValue<string>())
            .ToHashSet();

        Assert.Contains("payment-processing", activeFlowNames);
        Assert.Contains("payment-review", activeFlowNames);
    }

    // Ticket 19: bootstrap used to call ActivateAsync unconditionally for each
    // bundled map, on the assumption that no other version would ever exist.
    // Once an operator publishes and activates v2, the next `docker compose up`
    // silently moved the active version back to the bundled v1 -- the same
    // defect the action half of bootstrap was already fixed for.
    //
    // Two throwaway flows under the production file names: bootstrap picks the
    // files by name, and the flow name comes from the document, so this stays
    // clear of the real payment-processing/payment-review rows the rest of the
    // collection runs on.
    [Fact]
    public async Task RepeatBootstrap_AfterAnOperatorActivatedAnotherVersion_LeavesTheActiveVersionAlone()
    {
        await EnsureProbeManifestPublishedAsync();
        var pinnedFlow = $"bootstrap-pinned-{Guid.NewGuid():N}";
        var untouchedFlow = $"bootstrap-untouched-{Guid.NewGuid():N}";
        var mapsDirectory = WriteProductionMaps(
            ("payment-processing-v1.flow.yaml", ProbeFlow(pinnedFlow, version: 1)),
            ("payment-review-v1.flow.yaml", ProbeFlow(untouchedFlow, version: 1)));
        var emptyManifestsDirectory = Directory.CreateTempSubdirectory("bootstrap-flow-pin-manifests-").FullName;

        var first = await RunBootstrapAsync(emptyManifestsDirectory, mapsDirectory);
        Assert.Equal("ok", first.Status);
        Assert.Equal(1, await ActiveVersionAsync(pinnedFlow));
        Assert.Equal(1, await ActiveVersionAsync(untouchedFlow));

        var publishedV2 = await factory.Postgres.FlowPublisher.PublishAsync(
            ProbeFlow(pinnedFlow, version: 2),
            Workflow.Core.Canonicalization.JsonCanonicalizer.Sha256Hex(ProbeFlow(pinnedFlow, version: 2)),
            CancellationToken.None);
        Assert.Equal("ok", publishedV2["status"]!.GetValue<string>());
        var activatedV2 = await factory.Postgres.FlowPublisher.ActivateAsync(pinnedFlow, 2, CancellationToken.None);
        Assert.Equal("ok", activatedV2["status"]!.GetValue<string>());

        var second = await RunBootstrapAsync(emptyManifestsDirectory, mapsDirectory);
        Assert.Equal("ok", second.Status);

        Assert.Equal(2, await ActiveVersionAsync(pinnedFlow));
        Assert.Equal(1, await ActiveVersionAsync(untouchedFlow));
    }

    private Task<Course.Core.Cli.CliEnvelope> RunBootstrapAsync(string manifestsDirectory, string mapsDirectory) =>
        BootstrapCommand.ExecuteAsync(
            factory.Postgres.MigrationRunner,
            factory.Postgres.Publisher,
            SchemaValidator,
            ManifestSchema.Load(),
            RepoPaths.Combine("db", "migrations"),
            manifestsDirectory,
            CancellationToken.None,
            flowMapValidator: BuildValidator(),
            flowPublisher: factory.Postgres.FlowPublisher,
            flowMapsDirectory: mapsDirectory);

    private async Task<int?> ActiveVersionAsync(string flowName)
    {
        var flows = await factory.Postgres.FlowPublisher.ListAsync(CancellationToken.None);
        return flows!.AsArray()
            .Where(entry => entry!["flowName"]!.GetValue<string>() == flowName && entry["isActive"]!.GetValue<bool>())
            .Select(entry => (int?)entry!["flowVersion"]!.GetValue<int>())
            .SingleOrDefault();
    }

    private static string WriteProductionMaps(params (string FileName, System.Text.Json.Nodes.JsonNode Document)[] maps)
    {
        var directory = Directory.CreateTempSubdirectory("bootstrap-flow-pin-maps-").FullName;
        foreach (var (fileName, document) in maps)
        {
            File.WriteAllText(Path.Combine(directory, fileName), document.ToJsonString());
        }

        return directory;
    }

    // One automatic workflow.probe step and an end: the smallest document the
    // real WorkflowMapValidator accepts against the published catalog.
    private static System.Text.Json.Nodes.JsonNode ProbeFlow(string flowName, int version) =>
        System.Text.Json.Nodes.JsonNode.Parse($$"""
            {
              "contract_version": "course-1",
              "flow_name": "{{flowName}}",
              "version": {{version}},
              "start_step": "probe",
              "steps": [
                {
                  "key": "probe",
                  "type": "automatic",
                  "task": {
                    "service": "postgres",
                    "module": "workflow",
                    "action": "probe",
                    "action_version": 1,
                    "required_policy": ["workflow:execute"],
                    "timeout_ms": 5000,
                    "retry": {"max_attempts": 1, "delays_ms": []},
                    "input_mapping": {},
                    "input_constants": {"mode": "SUCCESS", "value": "{{flowName}}"}
                  }
                },
                {"key": "review", "type": "manual", "allowed_outcomes": ["APPROVED"]},
                {"key": "end", "type": "end", "outcome": "DONE"}
              ],
              "transitions": [
                {"from": "probe", "outcome": "SUCCEEDED", "to": "end"},
                {"from": "probe", "outcome": "ROUTE_MANUAL", "to": "review"},
                {"from": "review", "outcome": "APPROVED", "to": "end"}
              ]
            }
            """)!;

    private WorkflowMapValidator BuildValidator() =>
        new(SchemaValidator, MapSchemaDocument, new ActionPublisherCatalog(factory.Postgres.Publisher), WorkflowWorkerPrincipal.DefaultScopes);

    private async Task EnsureProbeManifestPublishedAsync()
    {
        foreach (var name in new[] { "3c363d117b40.action.json", "3c363d117b40-disabled.action.json" })
        {
            var path = RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "manifests", name);
            var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var manifest = Course.Core.Actions.ActionManifest.FromDocument(document);

            var result = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
            if (result["status"]!.GetValue<string>() != "ok")
            {
                throw new InvalidOperationException($"failed to publish fixture manifest {name}: {result.ToJsonString()}");
            }
        }
    }
}
