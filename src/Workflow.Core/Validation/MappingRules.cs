using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

// input_mapping/input_constants rules for every automatic step's task: both
// pointer families must be valid RFC 6901, and no target pointer -- from
// either mapping or constants -- may overlap another. input_constants has no
// pointer syntax of its own (it is a plain JSON object payload), so each of
// its top-level keys is treated as the one-segment target pointer `/<key>`
// for the purpose of this check.
public static class MappingRules
{
    public static IReadOnlyList<MapRuleViolation> Check(MapDocument map)
    {
        var violations = new List<MapRuleViolation>();

        foreach (var step in map.Steps)
        {
            if (step.Kind != StepKind.Automatic)
            {
                continue;
            }

            CheckStep(step.Key, step.Task!, violations);
        }

        return violations;
    }

    private static void CheckStep(string stepKey, TaskDefinition task, List<MapRuleViolation> violations)
    {
        var targets = new List<(string Pointer, IReadOnlyList<string> Segments)>();
        var invalidPointers = new List<string>();

        foreach (var targetPointer in task.InputMapping.Keys)
        {
            if (JsonPointer.TryParse(targetPointer, out var segments))
            {
                targets.Add((targetPointer, segments));
            }
            else
            {
                invalidPointers.Add(targetPointer);
            }
        }

        foreach (var sourcePointer in task.InputMapping.Values)
        {
            if (!JsonPointer.TryParse(sourcePointer, out _))
            {
                invalidPointers.Add(sourcePointer);
            }
        }

        foreach (var key in task.InputConstants.Select(pair => pair.Key))
        {
            targets.Add(($"/{key}", [key]));
        }

        if (invalidPointers.Count > 0)
        {
            violations.Add(new MapRuleViolation(
                "flow.mapping_pointer_invalid",
                $"step '{stepKey}': invalid RFC 6901 pointer(s): {string.Join(", ", invalidPointers.Distinct(StringComparer.Ordinal))}"));
            return;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            for (var j = i + 1; j < targets.Count; j++)
            {
                if (JsonPointer.Overlaps(targets[i].Segments, targets[j].Segments))
                {
                    violations.Add(new MapRuleViolation(
                        "flow.mapping_overlap",
                        $"step '{stepKey}': target pointers '{targets[i].Pointer}' and '{targets[j].Pointer}' overlap"));
                    return;
                }
            }
        }
    }
}
