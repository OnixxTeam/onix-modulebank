namespace Workflow.Worker;

// The loop's own tunables, injected rather than read from the environment
// inside WorkerLoop itself -- so a test builds one with whatever lease/poll
// numbers the scenario needs, and picking production vs. test defaults from
// COURSE_TEST_PROFILE stays a Program.cs concern (ticket 11 grows that part).
public sealed record IntervalProfile(int LeaseMs, int PollIntervalMs);
