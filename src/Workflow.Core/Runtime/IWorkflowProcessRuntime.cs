using System.Text.Json.Nodes;

namespace Workflow.Core.Runtime;

// CLI-facing surface for the direct-connection process commands (flow start,
// flow get, flow signal). The network-facing full get (workflow.get action)
// goes through api.invoke like any other action and has no place here.
public interface IWorkflowProcessRuntime
{
    Task<JsonNode> StartAsync(
        string flowName, string businessKey, JsonNode startData, string startDataHash, CancellationToken cancellationToken);

    Task<JsonNode> GetCompactAsync(Guid processId, CancellationToken cancellationToken);

    Task<JsonNode> SignalAsync(
        Guid processId, string messageId, string signalType, string bodyHash, JsonNode body, CancellationToken cancellationToken);
}
