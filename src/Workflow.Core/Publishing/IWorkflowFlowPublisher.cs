using System.Text.Json.Nodes;

namespace Workflow.Core.Publishing;

// Mirrors Course.Core.Actions.IActionPublisher: the SQL side already produces
// the exact result shape the CLI needs, so PublishAsync forwards the
// schema-validated document as-is rather than re-serializing a parsed
// MapDocument -- workflow.publish_flow (migration 020) decomposes it
// directly from jsonb.
public interface IWorkflowFlowPublisher
{
    Task<JsonNode> PublishAsync(JsonNode document, string canonicalHash, CancellationToken cancellationToken);

    Task<JsonNode> ListAsync(CancellationToken cancellationToken);

    Task<JsonNode> ActivateAsync(string flowName, int version, CancellationToken cancellationToken);
}
