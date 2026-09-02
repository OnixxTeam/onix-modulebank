using System.Text.Json.Nodes;
using Workflow.Core.Runtime;

namespace Course.Tests.Unit;

// Ticket 08's own mapping requirement: input_mapping/input_constants applied
// in C# against a claimed job's process data, RFC 6901 pointers in both
// families, escaping included, and a missing source reported rather than
// guessed at.
public sealed class WorkflowStepPayloadMapperTests
{
    [Fact]
    public void Constants_AreCopiedVerbatimIntoThePayload()
    {
        var constants = JsonNode.Parse("""{"mode": "SUCCESS"}""")!.AsObject();

        var result = WorkflowStepPayloadMapper.BuildPayload(constants, new Dictionary<string, string>(), new JsonObject());

        Assert.True(result.IsSuccess);
        Assert.Equal("SUCCESS", result.Payload!["mode"]!.GetValue<string>());
    }

    [Fact]
    public void Mapping_ResolvesASourcePointer_FromProcessData_IntoTheTargetPointer()
    {
        var processData = JsonNode.Parse("""{"start": {"value": "from-process-data"}}""")!;
        var mapping = new Dictionary<string, string> { ["/value"] = "/start/value" };

        var result = WorkflowStepPayloadMapper.BuildPayload(new JsonObject(), mapping, processData);

        Assert.True(result.IsSuccess);
        Assert.Equal("from-process-data", result.Payload!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Mapping_BuildsANestedTargetPointer_CreatingIntermediateObjects()
    {
        var processData = JsonNode.Parse("""{"source": 42}""")!;
        var mapping = new Dictionary<string, string> { ["/nested/target"] = "/source" };

        var result = WorkflowStepPayloadMapper.BuildPayload(new JsonObject(), mapping, processData);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Payload!["nested"]!["target"]!.GetValue<int>());
    }

    [Fact]
    public void Mapping_MissingSourcePointer_ReportsItInstead_OfCallingTheActionOrGuessing()
    {
        var mapping = new Dictionary<string, string> { ["/value"] = "/does/not/exist" };

        var result = WorkflowStepPayloadMapper.BuildPayload(new JsonObject(), mapping, new JsonObject());

        Assert.False(result.IsSuccess);
        Assert.Equal("/does/not/exist", result.MissingSourcePointer);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Mapping_SourceValuePresentButExplicitlyNull_ResolvesToNull_NotMissing()
    {
        var processData = JsonNode.Parse("""{"maybe": null}""")!;
        var mapping = new Dictionary<string, string> { ["/value"] = "/maybe" };

        var result = WorkflowStepPayloadMapper.BuildPayload(new JsonObject(), mapping, processData);

        Assert.True(result.IsSuccess);
        Assert.True(result.Payload!.TryGetPropertyValue("value", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Mapping_UnescapesTildeAndSlash_InBothTargetAndSourcePointers()
    {
        // "a/b" -> "a~1b", "c~d" -> "c~0d" per RFC 6901.
        var processData = JsonNode.Parse("""{"c~d": "escaped-source"}""")!;
        var mapping = new Dictionary<string, string> { ["/a~1b"] = "/c~0d" };

        var result = WorkflowStepPayloadMapper.BuildPayload(new JsonObject(), mapping, processData);

        Assert.True(result.IsSuccess);
        Assert.Equal("escaped-source", result.Payload!["a/b"]!.GetValue<string>());
    }

    [Fact]
    public void Mapping_ResolvesAnArrayIndexSegment_InTheSourcePointer()
    {
        var processData = JsonNode.Parse("""{"items": ["first", "second"]}""")!;
        var mapping = new Dictionary<string, string> { ["/value"] = "/items/1" };

        var result = WorkflowStepPayloadMapper.BuildPayload(new JsonObject(), mapping, processData);

        Assert.True(result.IsSuccess);
        Assert.Equal("second", result.Payload!["value"]!.GetValue<string>());
    }
}
