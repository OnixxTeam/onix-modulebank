using System.Text.Json.Nodes;

namespace Course.Core.Delivery;

// The one Outbox tunable that differs between the test and production
// profiles. Attempts and delays are the contract's retry budget and live in
// delivery.retry_policy unchanged in both profiles (migration 043); the lease
// is 2 seconds under COURSE_TEST_PROFILE=1 and conservative otherwise, so
// something outside the database has to say which, exactly as
// Workflow.Worker's IntervalProfile does for the job lease.
public interface IDeliveryProfile
{
    Task<JsonNode> SetLeaseMsAsync(int leaseMs, CancellationToken cancellationToken);
}
