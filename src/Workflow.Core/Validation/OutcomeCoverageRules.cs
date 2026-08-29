using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

// Outcome coverage for the two step types that declare their own outcome set
// without a catalog lookup: wait_signal (exactly its one `outcome`) and manual
// (exactly its `allowed_outcomes`). Automatic steps declare no outcome set of
// their own -- the action they call does -- so their coverage check lives in
// CatalogRules, next to the catalog lookup that resolves it.
public static class OutcomeCoverageRules
{
    public static IReadOnlyList<MapRuleViolation> Check(MapDocument map)
    {
        var violations = new List<MapRuleViolation>();

        foreach (var step in map.Steps)
        {
            if (step.Kind is StepKind.Automatic or StepKind.End)
            {
                continue;
            }

            var declared = step.DeclaredOutcomes(catalogOutcomes: null)!;
            var outgoing = map.Transitions.Where(t => string.Equals(t.From, step.Key, StringComparison.Ordinal)).ToList();
            CoverageRules.CheckStep(step.Key, declared, outgoing, violations);
        }

        return violations;
    }
}

// Shared by OutcomeCoverageRules (wait_signal/manual) and CatalogRules
// (automatic): exactly one transition per declared outcome, no more, no fewer.
internal static class CoverageRules
{
    public static void CheckStep(
        string stepKey, IReadOnlyList<string> declaredOutcomes, IReadOnlyList<TransitionDefinition> outgoing, List<MapRuleViolation> violations)
    {
        var counts = outgoing.GroupBy(t => t.Outcome, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var declaredSet = new HashSet<string>(declaredOutcomes, StringComparer.Ordinal);

        var missing = declaredSet.Where(o => !counts.ContainsKey(o)).ToList();
        var unexpected = counts.Keys.Where(o => !declaredSet.Contains(o)).ToList();
        var duplicated = counts.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToList();

        if (missing.Count == 0 && unexpected.Count == 0 && duplicated.Count == 0)
        {
            return;
        }

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"missing transition for outcome(s) {string.Join(", ", missing)}");
        }

        if (unexpected.Count > 0)
        {
            parts.Add($"transition for undeclared outcome(s) {string.Join(", ", unexpected)}");
        }

        if (duplicated.Count > 0)
        {
            parts.Add($"more than one transition for outcome(s) {string.Join(", ", duplicated)}");
        }

        violations.Add(new MapRuleViolation(
            "flow.outcome_coverage_mismatch", $"step '{stepKey}': {string.Join("; ", parts)}"));
    }
}
