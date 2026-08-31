using System.Text.Json.Nodes;

namespace Workflow.Core.Runtime;

// The worker's own claim, finish and fail boundary (workflow.claim_jobs,
// workflow.finish_job, workflow.fail_job) -- exactly the role's own EXECUTE
// list besides api.invoke. Exists for direct-call and concurrent-claim/finish
// tests, the flow test-finish CLI probe, and WorkerLoop's own fail path (which
// needs a connection independent of the one its rolled-back action transaction
// used, see ticket 10: "на отдельном соединении").
public interface IWorkflowJobRuntime
{
    Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken);

    Task<JsonNode> FinishAsync(
        Guid jobId, string owner, int leaseVersion, string outcome, JsonNode result, CancellationToken cancellationToken);

    Task<JsonNode> FailAsync(
        Guid jobId, string owner, int leaseVersion, string errorCode, bool retryable, CancellationToken cancellationToken);
}
