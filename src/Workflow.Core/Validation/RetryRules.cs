using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

// The schema already bounds max_attempts (1..10) and each delay (0..30000ms,
// at most 9 entries); the one relationship it cannot express is that
// delays_ms must have exactly one entry per retry after the first attempt.
public static class RetryRules
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

            var retry = step.Task!.Retry;
            var expected = retry.MaxAttempts - 1;
            if (retry.DelaysMs.Count != expected)
            {
                violations.Add(new MapRuleViolation(
                    "flow.retry_invalid",
                    $"step '{step.Key}': delays_ms has {retry.DelaysMs.Count} entries, expected {expected} for max_attempts {retry.MaxAttempts}"));
            }
        }

        return violations;
    }
}
