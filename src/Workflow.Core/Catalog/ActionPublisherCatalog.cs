using System.Text.Json.Nodes;
using Course.Core.Actions;

namespace Workflow.Core.Catalog;

// Adapts the week 1 IActionPublisher (already connected as course_publisher)
// to the catalog shape the map validator needs, rather than opening a second
// connection: `action list` and `flow validate` read the same rows, just
// project different fields out of them.
public sealed class ActionPublisherCatalog(IActionPublisher publisher) : IWorkflowActionCatalog
{
    // A map with several automatic steps calls FindAsync once per step; the
    // catalog cannot change mid-validate, so cache the one full-table read
    // instead of reissuing api.list_actions() per step.
    private Task<JsonNode>? cachedList;

    public async Task<WorkflowActionCatalogEntry?> FindAsync(
        string moduleName, string actionName, int version, CancellationToken cancellationToken)
    {
        cachedList ??= publisher.ListAsync(cancellationToken);
        var raw = await cachedList;

        foreach (var entry in raw!.AsArray())
        {
            if (entry!["module"]!.GetValue<string>() != moduleName
                || entry["action"]!.GetValue<string>() != actionName
                || entry["version"]!.GetValue<int>() != version)
            {
                continue;
            }

            return new WorkflowActionCatalogEntry(
                moduleName,
                actionName,
                version,
                entry["enabled"]!.GetValue<bool>(),
                entry["outcomes"]!.AsArray().Select(o => o!.GetValue<string>()).ToList(),
                entry["requiredPolicy"]!.AsArray().Select(p => p!.GetValue<string>()).ToList());
        }

        return null;
    }
}
