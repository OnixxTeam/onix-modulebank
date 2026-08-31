using System.Text.Json.Nodes;
using Workflow.Core.Validation;

namespace Workflow.Core.Runtime;

// Applies a claimed job's input_mapping/input_constants against process data to
// build the payload a target action will be called with. Direction is target
// payload pointer -> source process-data pointer (SPEC "Mapping"); constants are
// merged first, mapping pointers second, so a mapping target can never be
// silently shadowed by a constant applied afterward (MappingRules already
// proved at publish time that no two targets -- mapping or constants -- overlap,
// so write order between the two groups never matters for a valid map).
public static class WorkflowStepPayloadMapper
{
    public static MappingResult BuildPayload(
        JsonObject inputConstants, IReadOnlyDictionary<string, string> inputMapping, JsonNode? processData)
    {
        var payload = new JsonObject();

        foreach (var (key, value) in inputConstants)
        {
            payload[key] = value?.DeepClone();
        }

        foreach (var (targetPointer, sourcePointer) in inputMapping)
        {
            // Both pointers already passed MappingRules at publish time (valid RFC
            // 6901, non-overlapping targets) -- a parse failure here would mean a
            // published map that never should have been accepted, not a runtime
            // condition this method needs its own error code for.
            if (!JsonPointer.TryParse(sourcePointer, out var sourceSegments))
            {
                throw new InvalidOperationException($"published task carries an invalid source pointer: {sourcePointer}");
            }

            if (!JsonPointer.TryResolve(processData, sourceSegments, out var value))
            {
                return MappingResult.Missing(sourcePointer);
            }

            if (!JsonPointer.TryParse(targetPointer, out var targetSegments))
            {
                throw new InvalidOperationException($"published task carries an invalid target pointer: {targetPointer}");
            }

            SetAtPointer(payload, targetSegments, value?.DeepClone());
        }

        return MappingResult.Success(payload);
    }

    private static void SetAtPointer(JsonObject root, IReadOnlyList<string> segments, JsonNode? value)
    {
        var current = root;

        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[segments[i]] = next;
            }

            current = next;
        }

        current[segments[^1]] = value;
    }
}

public sealed record MappingResult(bool IsSuccess, JsonObject? Payload, string? MissingSourcePointer)
{
    public static MappingResult Success(JsonObject payload) => new(true, payload, null);

    public static MappingResult Missing(string sourcePointer) => new(false, null, sourcePointer);
}
