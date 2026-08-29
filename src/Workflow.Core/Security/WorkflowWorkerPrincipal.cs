namespace Workflow.Core.Security;

// The workflow-worker principal never authenticates with a JWT (week 2
// decision log #4: it is a trusted internal principal, not a client-presented
// token), so its granted scopes have no "scope" claim to read at validate
// time. This is the authoritative, server-side list `flow validate`/`flow
// publish` check a task's required_policy against, and the value
// Workflow.Worker stamps onto the context it builds for itself (ticket 08) --
// override via WORKFLOW_WORKER_SCOPES, never widen this default silently.
public static class WorkflowWorkerPrincipal
{
    public const string Name = "workflow-worker";

    public static readonly IReadOnlyList<string> DefaultScopes = ["workflow:execute", "workflow:read"];
}
