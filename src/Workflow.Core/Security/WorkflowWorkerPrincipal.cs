namespace Workflow.Core.Security;

// The workflow-worker principal is a trusted internal caller, not a
// client-presented JWT, so it has no "scope" claim to read at validate time.
// This is the authoritative, server-side list `flow validate`/`flow publish`
// check a task's required_policy against, and the value Workflow.Worker
// stamps onto the context it builds for itself -- override via
// WORKFLOW_WORKER_SCOPES, never widen this default silently.
//
// payment:internal was added deliberately (week 3, ticket 03 fix): the
// course-supplied, unmodifiable db/maps/payment-processing-v1.flow.yaml
// pins every automatic step's task to required_policy: [payment:internal],
// so every payment.* domain action (validate/check_limit/approve/reject,
// and processing's own prepare_external/apply_receipt/complete from later
// tickets) declares that same policy in its manifest -- CatalogRules
// requires an exact set match between a task and its target manifest.
public static class WorkflowWorkerPrincipal
{
    public const string Name = "workflow-worker";

    public static readonly IReadOnlyList<string> DefaultScopes = ["workflow:execute", "workflow:read", "payment:internal"];
}
