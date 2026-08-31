using System.Text.Json.Nodes;

namespace Workflow.Core.Runtime;

// CLI-facing surface for the two direct-connection process commands (flow
// start, flow get). The network-facing full get (workflow.get action) goes
// through api.invoke like any other action and has no place here.
public interface IWorkflowProcessRuntime
{
    Task<JsonNode> StartAsync(
        string flowName, string businessKey, JsonNode startData, string startDataHash, CancellationToken cancellationToken);

    Task<JsonNode> GetCompactAsync(Guid processId, CancellationToken cancellationToken);
}
