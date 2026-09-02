using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

public sealed record MapRuleViolation(string Code, string Message);

public sealed record MapValidationResult(bool IsValid, MapDocument? Map, IReadOnlyList<MapRuleViolation> Violations)
{
    public static MapValidationResult Success(MapDocument map) => new(true, map, []);

    public static MapValidationResult Failure(IReadOnlyList<MapRuleViolation> violations) => new(false, null, violations);
}
