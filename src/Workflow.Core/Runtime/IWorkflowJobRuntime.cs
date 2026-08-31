using System.Text.Json.Nodes;

namespace Workflow.Core.Runtime;

// The worker's own claim and finish boundary (workflow.claim_job,
// workflow.finish_job). No executor consumes this yet -- ticket 08 wires a
// real worker loop on top of it -- so today it exists purely for direct-call
// and concurrent-claim/finish tests, and the flow test-finish CLI probe, to
// drive against.
public interface IWorkflowJobRuntime
{
    Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken);

    Task<JsonNode> FinishAsync(
        Guid jobId, string owner, int leaseVersion, string outcome, JsonNode result, CancellationToken cancellationToken);
}
