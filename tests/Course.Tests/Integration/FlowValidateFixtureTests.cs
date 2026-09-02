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
        Directory.GetFiles(RepoPaths.Combine("autocheck", "fixtures", "maps"), "*.flow.*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { path });

    public static IEnumerable<object[]> InvalidFixtures() =>
        Directory.GetFiles(RepoPaths.Combine("autocheck", "fixtures", "maps", "invalid"), "*.flow.json", SearchOption.AllDirectories)
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

    private WorkflowMapValidator BuildValidator() =>
        new(SchemaValidator, MapSchemaDocument, new ActionPublisherCatalog(factory.Postgres.Publisher), WorkflowWorkerPrincipal.DefaultScopes);

    private async Task EnsureProbeManifestPublishedAsync()
    {
        foreach (var name in new[] { "3c363d117b40.action.json", "3c363d117b40-disabled.action.json" })
        {
            var path = RepoPaths.Combine("autocheck", "fixtures", "manifests", name);
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
