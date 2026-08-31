using System.Text.Json.Nodes;

namespace Workflow.Core.Runtime;

// The worker's own claim boundary (workflow.claim_job). No executor consumes
// this yet -- ticket 08 wires a real worker loop on top of it -- so today it
// exists purely for direct-call and concurrent-claim tests to drive against.
public interface IWorkflowJobRuntime
{
    Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken);
}
