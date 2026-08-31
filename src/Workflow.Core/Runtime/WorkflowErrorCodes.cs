namespace Workflow.Core.Runtime;

// The two error codes week 2 fixes by name (SPEC "Фиксированные error codes
// недели 2: workflow.mapping_missing, workflow.lease_stale") -- every other
// code stays a safe lowercase-dotted string nobody checks by value. lease_stale
// is minted in SQL (workflow.finish_job) and never needs a C# constant of its
// own; mapping_missing is minted here, the one place a missing mapping source
// is detected, so ticket 10's fail_job wiring reads it from one place instead
// of re-typing the literal. RequestInvalid is not one of the two fixed codes --
// it is this same worker loop's own free-form (SPEC: "остальные используют
// safe lowercase dotted identifiers") code for the other pre-invoke rejection
// this ticket must also fail non-retryably: a mapped payload the action's own
// request schema refuses.
public static class WorkflowErrorCodes
{
    public const string MappingMissing = "workflow.mapping_missing";
    public const string RequestInvalid = "workflow.request_invalid";
}
