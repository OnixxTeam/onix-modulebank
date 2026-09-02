using System.Text.Json.Nodes;
using Course.Tests.Support;
using Workflow.Core.Maps;
using Workflow.Core.Validation;

namespace Course.Tests.Unit;

public sealed class JsonPointerTests
{
    [Theory]
    [InlineData("/a", "/a", true)] // equal
    [InlineData("/a", "/a/b", true)] // ancestor/descendant
    [InlineData("/a/b", "/a", true)] // order-independent
    [InlineData("/a", "/ab", false)] // shared text prefix, different segment
    [InlineData("/marker/nested", "/marker", true)]
    public void Overlaps_DecidesBySegmentsNotStringPrefix(string a, string b, bool expected)
    {
        Assert.True(JsonPointer.TryParse(a, out var segmentsA));
        Assert.True(JsonPointer.TryParse(b, out var segmentsB));

        Assert.Equal(expected, JsonPointer.Overlaps(segmentsA, segmentsB));
    }

    [Theory]
    [InlineData("no-leading-slash")]
    [InlineData("/bad~escape")]
    public void TryParse_RejectsMalformedPointers(string raw)
    {
        Assert.False(JsonPointer.TryParse(raw, out _));
    }

    [Fact]
    public void TryParse_UnescapesTildeAndSlash()
    {
        Assert.True(JsonPointer.TryParse("/a~1b~0c", out var segments));
        Assert.Equal(["a/b~c"], segments);
    }
}

public sealed class MapLoaderTests
{
    // The fixture pair proves the JSON and YAML encodings of the same map
    // converge into one representation before validation ever runs -- not just
    // that YAML parses, but that it parses to exactly the same document.
    [Fact]
    public async Task JsonAndYamlFixture_ParseToTheSameDocument()
    {
        var jsonPath = RepoPaths.Combine("autocheck", "fixtures", "maps", "3c363d117b40-v1.flow.json");
        var yamlPath = RepoPaths.Combine("autocheck", "fixtures", "maps", "3c363d117b40-v1.flow.yaml");

        var fromJson = await MapLoader.LoadAsync(jsonPath, CancellationToken.None);
        var fromYaml = await MapLoader.LoadAsync(yamlPath, CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(fromJson, fromYaml));
    }

    [Fact]
    public void Parse_RejectsGarbageInput()
    {
        Assert.Throws<MapLoadException>(() => MapLoader.Parse("not: [valid : yaml-or-json"));
    }
}

public sealed class GraphRulesTests
{
    [Theory]
    [InlineData("cycle.flow.json", "flow.cycle_detected")]
    [InlineData("dead-end.flow.json", "flow.dead_end_step")]
    [InlineData("unreachable-step.flow.json", "flow.unreachable_step")]
    // The fixture drops the end step's definition entirely while transitions
    // still target its key -- an unknown reference, caught before reachability
    // ever runs (that phase requires every referenced key to resolve first).
    [InlineData("missing-end.flow.json", "flow.unknown_transition_target")]
    [InlineData("transition-from-end.flow.json", "flow.transition_from_end")]
    [InlineData("duplicate-step-key.flow.json", "flow.duplicate_step_key")]
    [InlineData("unknown-start-step.flow.json", "flow.unknown_start_step")]
    public async Task SemanticFixture_TriggersExpectedGraphViolation(string fixtureName, string expectedCode)
    {
        var path = RepoPaths.Combine("autocheck", "fixtures", "maps", "invalid", "semantic", fixtureName);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var map = MapParser.Parse(document);

        var violations = GraphRules.Check(map);

        Assert.Contains(violations, v => v.Code == expectedCode);
    }

    [Fact]
    public async Task ValidFixture_HasNoGraphViolations()
    {
        var path = RepoPaths.Combine("autocheck", "fixtures", "maps", "3c363d117b40-v1.flow.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var map = MapParser.Parse(document);

        Assert.Empty(GraphRules.Check(map));
    }
}

public sealed class MappingRulesTests
{
    [Fact]
    public async Task OverlappingMappingFixture_IsRejected()
    {
        var path = RepoPaths.Combine("autocheck", "fixtures", "maps", "invalid", "semantic", "overlapping-mapping.flow.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var map = MapParser.Parse(document);

        var violations = MappingRules.Check(map);

        Assert.Contains(violations, v => v.Code == "flow.mapping_overlap");
    }

    [Fact]
    public async Task ValidFixture_HasNoMappingViolations()
    {
        var path = RepoPaths.Combine("autocheck", "fixtures", "maps", "3c363d117b40-v1.flow.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var map = MapParser.Parse(document);

        Assert.Empty(MappingRules.Check(map));
    }
}

public sealed class RetryRulesTests
{
    [Fact]
    public async Task InvalidRetryFixture_IsRejected()
    {
        var path = RepoPaths.Combine("autocheck", "fixtures", "maps", "invalid", "semantic", "invalid-retry.flow.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var map = MapParser.Parse(document);

        var violations = RetryRules.Check(map);

        Assert.Contains(violations, v => v.Code == "flow.retry_invalid");
    }

    [Fact]
    public async Task ValidFixture_HasNoRetryViolations()
    {
        var path = RepoPaths.Combine("autocheck", "fixtures", "maps", "3c363d117b40-v1.flow.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var map = MapParser.Parse(document);

        Assert.Empty(RetryRules.Check(map));
    }
}

public sealed class OutcomeCoverageRulesTests
{
    // No open fixture isolates a manual/wait_signal coverage break from the
    // catalog-dependent automatic case (duplicate-outcome.flow.json /
    // incomplete-outcomes.flow.json both target the automatic step), so this
    // builds the smallest map that exercises the manual branch directly.
    [Fact]
    public void ManualStep_MissingTransitionForAllowedOutcome_IsRejected()
    {
        var map = new MapDocument(
            "flow", 1, "review",
            [
                new StepDefinition("review", StepKind.Manual, AllowedOutcomes: ["APPROVED", "REJECTED"]),
                new StepDefinition("done", StepKind.End, EndOutcome: "COMPLETED"),
            ],
            [new TransitionDefinition("review", "APPROVED", "done")]);

        var violations = OutcomeCoverageRules.Check(map);

        Assert.Contains(violations, v => v.Code == "flow.outcome_coverage_mismatch");
    }

    [Fact]
    public void ManualStep_FullCoverage_HasNoViolations()
    {
        var map = new MapDocument(
            "flow", 1, "review",
            [
                new StepDefinition("review", StepKind.Manual, AllowedOutcomes: ["APPROVED"]),
                new StepDefinition("done", StepKind.End, EndOutcome: "COMPLETED"),
            ],
            [new TransitionDefinition("review", "APPROVED", "done")]);

        Assert.Empty(OutcomeCoverageRules.Check(map));
    }
}
