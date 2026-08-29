using Workflow.Core.Catalog;
using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

// Everything a task depends on the action catalog for: existence, enabled,
// exact required_policy equality with the action's own manifest, the worker's
// scopes being sufficient to actually call it, and (since only the catalog
// knows an action's declared outcomes) automatic-step outcome coverage.
// Re-checked in SQL at publish time (contract-reference pattern from
// db/migrations/004_publish_functions.sql) because the catalog can change
// between a `flow validate` and the `flow publish` that follows it.
public static class CatalogRules
{
    public static async Task<IReadOnlyList<MapRuleViolation>> CheckAsync(
        MapDocument map, IWorkflowActionCatalog catalog, IReadOnlyList<string> workerScopes, CancellationToken cancellationToken)
    {
        var violations = new List<MapRuleViolation>();

        foreach (var step in map.Steps)
        {
            if (step.Kind != StepKind.Automatic)
            {
                continue;
            }

            var task = step.Task!;
            var entry = await catalog.FindAsync(task.Module, task.Action, task.ActionVersion, cancellationToken);

            if (entry is null)
            {
                violations.Add(new MapRuleViolation(
                    "flow.unknown_action",
                    $"step '{step.Key}': action {task.Module}.{task.Action} v{task.ActionVersion} does not exist"));
                continue;
            }

            if (!entry.Enabled)
            {
                violations.Add(new MapRuleViolation(
                    "flow.disabled_action",
                    $"step '{step.Key}': action {task.Module}.{task.Action} v{task.ActionVersion} is disabled"));
                continue;
            }

            var taskPolicy = new HashSet<string>(task.RequiredPolicy, StringComparer.Ordinal);
            var actionPolicy = new HashSet<string>(entry.RequiredPolicy, StringComparer.Ordinal);
            if (!taskPolicy.SetEquals(actionPolicy))
            {
                violations.Add(new MapRuleViolation(
                    "flow.policy_mismatch",
                    $"step '{step.Key}': required_policy {{{string.Join(", ", task.RequiredPolicy)}}} does not equal " +
                    $"action's {{{string.Join(", ", entry.RequiredPolicy)}}}"));
            }

            var scopes = new HashSet<string>(workerScopes, StringComparer.Ordinal);
            if (!taskPolicy.IsSubsetOf(scopes))
            {
                violations.Add(new MapRuleViolation(
                    "flow.policy_insufficient",
                    $"step '{step.Key}': workflow-worker scopes do not cover required_policy {{{string.Join(", ", task.RequiredPolicy)}}}"));
            }

            var outgoing = map.Transitions.Where(t => string.Equals(t.From, step.Key, StringComparison.Ordinal)).ToList();
            CoverageRules.CheckStep(step.Key, entry.Outcomes, outgoing, violations);
        }

        return violations;
    }
}
