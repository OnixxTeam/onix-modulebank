namespace Workflow.Worker;

// The loop's own tunables, injected rather than read from the environment
// inside WorkerLoop itself -- so a test builds one with whatever lease/poll
// numbers the scenario needs. Picking production vs. test defaults from
// COURSE_TEST_PROFILE, and letting WORKFLOW_WORKER_LEASE_MS/_POLL_MS override
// either one individually, is a Program.cs concern (ticket 11).
public sealed record IntervalProfile(int LeaseMs, int PollIntervalMs);
