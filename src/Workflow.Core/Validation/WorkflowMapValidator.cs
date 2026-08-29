using System.Text.Json.Nodes;
using Course.Core.Validation;
using Workflow.Core.Catalog;
using Workflow.Core.Maps;

namespace Workflow.Core.Validation;

// The full course-1 map pipeline in the order the ticket lays the rules out:
// JSON Schema shape, then graph structure, then outcome coverage, catalog
// rules, mapping rules, retry rules. Every later phase assumes the schema
// already passed -- MapParser trusts required fields exist -- so a schema
// failure short-circuits before parsing runs at all. Every rule family after
// that runs to completion and every violation is collected: a caller reporting
// only the first one still knows every other rule ran clean for this map.
public sealed class WorkflowMapValidator(
    ISchemaValidator schemaValidator, JsonNode mapSchema, IWorkflowActionCatalog catalog, IReadOnlyList<string> workerScopes)
{
    public async Task<MapValidationResult> ValidateAsync(JsonNode document, CancellationToken cancellationToken)
    {
        var schemaResult = schemaValidator.Validate(mapSchema, document);
        if (!schemaResult.IsValid)
        {
            return MapValidationResult.Failure([
                new MapRuleViolation("flow.schema_invalid", string.Join("; ", schemaResult.Errors)),
            ]);
        }

        var map = MapParser.Parse(document);

        var violations = new List<MapRuleViolation>();
        violations.AddRange(GraphRules.Check(map));

        // Reachability/coverage checks assume the graph is well-formed; a raw
        // structural violation above already explains the failure on its own.
        if (violations.Count == 0)
        {
            violations.AddRange(OutcomeCoverageRules.Check(map));
            violations.AddRange(await CatalogRules.CheckAsync(map, catalog, workerScopes, cancellationToken));
            violations.AddRange(MappingRules.Check(map));
            violations.AddRange(RetryRules.Check(map));
        }

        return violations.Count == 0 ? MapValidationResult.Success(map) : MapValidationResult.Failure(violations);
    }
}
