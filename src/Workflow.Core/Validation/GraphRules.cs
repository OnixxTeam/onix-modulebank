using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

// Structural rules over the step/transition graph -- everything the JSON
// Schema cannot see because it validates one object at a time and has no
// notion of a key referencing another key. Checks that would crash on a
// dangling reference (reachability, cycle detection) run only among steps
// the earlier checks in this file already proved exist, so one bad reference
// never throws instead of reporting a violation.
public static class GraphRules
{
    public static IReadOnlyList<MapRuleViolation> Check(MapDocument map)
    {
        var violations = new List<MapRuleViolation>();

        CheckDuplicateStepKeys(map, violations);
        CheckStartStep(map, violations);
        CheckTransitionEndpoints(map, violations);
        CheckTransitionsFromEnd(map, violations);

        // Every later check assumes step keys are unique and every transition
        // endpoint resolves to a real step -- both already violations above.
        if (violations.Count > 0)
        {
            return violations;
        }

        var adjacency = map.Transitions
            .GroupBy(t => t.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TransitionDefinition>)g.ToList(), StringComparer.Ordinal);

        CheckDeadEnds(map, adjacency, violations);
        CheckReachability(map, adjacency, violations);
        CheckCycles(map, adjacency, violations);

        return violations;
    }

    private static void CheckDuplicateStepKeys(MapDocument map, List<MapRuleViolation> violations)
    {
        var duplicates = map.Steps
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            violations.Add(new MapRuleViolation(
                "flow.duplicate_step_key", $"duplicate step key(s): {string.Join(", ", duplicates)}"));
        }
    }

    private static void CheckStartStep(MapDocument map, List<MapRuleViolation> violations)
    {
        if (!map.StepsByKey.ContainsKey(map.StartStep))
        {
            violations.Add(new MapRuleViolation(
                "flow.unknown_start_step", $"start_step '{map.StartStep}' is not a declared step"));
        }
    }

    private static void CheckTransitionEndpoints(MapDocument map, List<MapRuleViolation> violations)
    {
        var unknown = map.Transitions
            .SelectMany(t => new[] { t.From, t.To })
            .Where(key => !map.StepsByKey.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
        {
            violations.Add(new MapRuleViolation(
                "flow.unknown_transition_target", $"transition references unknown step key(s): {string.Join(", ", unknown)}"));
        }
    }

    private static void CheckTransitionsFromEnd(MapDocument map, List<MapRuleViolation> violations)
    {
        var fromEnd = map.Transitions
            .Where(t => map.StepsByKey[t.From].Kind == StepKind.End)
            .Select(t => t.From)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fromEnd.Count > 0)
        {
            violations.Add(new MapRuleViolation(
                "flow.transition_from_end", $"end step(s) have outgoing transitions: {string.Join(", ", fromEnd)}"));
        }
    }

    private static void CheckDeadEnds(
        MapDocument map, Dictionary<string, IReadOnlyList<TransitionDefinition>> adjacency, List<MapRuleViolation> violations)
    {
        var deadEnds = map.Steps
            .Where(s => s.Kind != StepKind.End && !adjacency.ContainsKey(s.Key))
            .Select(s => s.Key)
            .ToList();

        if (deadEnds.Count > 0)
        {
            violations.Add(new MapRuleViolation(
                "flow.dead_end_step", $"non-end step(s) with no outgoing transition: {string.Join(", ", deadEnds)}"));
        }
    }

    private static void CheckReachability(
        MapDocument map, Dictionary<string, IReadOnlyList<TransitionDefinition>> adjacency, List<MapRuleViolation> violations)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { map.StartStep };
        var queue = new Queue<string>();
        queue.Enqueue(map.StartStep);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (reached.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        var unreachable = map.Steps.Where(s => !reached.Contains(s.Key)).Select(s => s.Key).ToList();
        if (unreachable.Count > 0)
        {
            violations.Add(new MapRuleViolation(
                "flow.unreachable_step", $"step(s) unreachable from start_step: {string.Join(", ", unreachable)}"));
        }

        if (!map.Steps.Any(s => s.Kind == StepKind.End && reached.Contains(s.Key)))
        {
            violations.Add(new MapRuleViolation("flow.no_reachable_end", "no end step is reachable from start_step"));
        }
    }

    private static void CheckCycles(
        MapDocument map, Dictionary<string, IReadOnlyList<TransitionDefinition>> adjacency, List<MapRuleViolation> violations)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unvisited, 1 in progress, 2 done

        foreach (var step in map.Steps)
        {
            if (state.GetValueOrDefault(step.Key) == 0 && HasCycle(step.Key, adjacency, state))
            {
                violations.Add(new MapRuleViolation("flow.cycle_detected", "the step graph contains a cycle"));
                return;
            }
        }
    }

    private static bool HasCycle(
        string key, Dictionary<string, IReadOnlyList<TransitionDefinition>> adjacency, Dictionary<string, int> state)
    {
        state[key] = 1;

        if (adjacency.TryGetValue(key, out var edges))
        {
            foreach (var edge in edges)
            {
                var next = state.GetValueOrDefault(edge.To);
                if (next == 1)
                {
                    return true;
                }

                if (next == 0 && HasCycle(edge.To, adjacency, state))
                {
                    return true;
                }
            }
        }

        state[key] = 2;
        return false;
    }
}
