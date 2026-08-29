namespace Workflow.Core.Catalog;

public sealed record WorkflowActionCatalogEntry(
    string Module,
    string Action,
    int Version,
    bool Enabled,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> RequiredPolicy);

// The catalog read `flow validate`/`flow publish` need: existence, enabled,
// declared outcomes and required policy for one action version. The worker
// never implements this -- claim_jobs hands it the pinned action's contract
// directly, so no second catalog read exists there.
public interface IWorkflowActionCatalog
{
    Task<WorkflowActionCatalogEntry?> FindAsync(string moduleName, string actionName, int version, CancellationToken cancellationToken);
}
