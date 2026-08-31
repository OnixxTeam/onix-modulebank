namespace Workflow.Core.Runtime;

// The two error codes week 2 fixes by name (SPEC "Фиксированные error codes
// недели 2: workflow.mapping_missing, workflow.lease_stale") -- every other
// code stays a safe lowercase-dotted string nobody checks by value. lease_stale
// is minted in SQL (workflow.finish_job) and never needs a C# constant of its
// own; mapping_missing is minted here, the one place a missing mapping source
// is detected, so a future caller (ticket 10's fail_job) reads it from one
// place instead of re-typing the literal.
public static class WorkflowErrorCodes
{
    public const string MappingMissing = "workflow.mapping_missing";
}
