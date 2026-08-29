namespace Workflow.Core.Security;

// The workflow-worker principal is a trusted internal caller, not a
// client-presented JWT, so it has no "scope" claim to read at validate time.
// This is the authoritative, server-side list `flow validate`/`flow publish`
// check a task's required_policy against, and the value Workflow.Worker
// stamps onto the context it builds for itself -- override via
// WORKFLOW_WORKER_SCOPES, never widen this default silently.
public static class WorkflowWorkerPrincipal
{
    public const string Name = "workflow-worker";

    public static readonly IReadOnlyList<string> DefaultScopes = ["workflow:execute", "workflow:read"];
}
