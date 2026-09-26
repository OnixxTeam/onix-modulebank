using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Delivery;
using Course.Core.Migrations;
using Course.Core.Validation;
using Workflow.Core.Canonicalization;
using Workflow.Core.Publishing;
using Workflow.Core.Validation;

namespace Course.Cli.Commands;

public static class BootstrapCommand
{
    // Ticket 13 acceptance: after a clean `docker compose up -d --build` the
    // circuit must be ready with no manual host command, including "published
    // and activated maps" -- before this, only `cli flow publish`/`flow
    // activate` run by hand (or by week 1-2's own checker script, for the
    // smoke maps) ever put a map into api.active use. These two are the given,
    // unmodifiable production flows this week's payment actions actually run
    // (docs/week3/04-week-3.md); the workflow-smoke*.flow.json fixtures stay
    // out of this list on purpose -- they exist for ad hoc `flow
    // publish`/`flow activate` during testing, not for unconditional
    // production activation on every boot.
    private static readonly string[] ProductionFlowMapFiles = ["payment-processing-v1.flow.yaml", "payment-review-v1.flow.yaml"];

    // Publish alone is idempotent and flag-safe (migrations 016/017): a repeat
    // run either matches the stored checksum and returns 'ok' before any write
    // (016's early branch), or -- for a brand-new module/action -- takes
    // enabled/is_default straight from the manifest without ever stealing an
    // existing default (017). Every built-in manifest carries enabled=true,
    // is_default=true, so a fresh install is callable from publish alone, no
    // follow-up activate needed.
    //
    // Bootstrap therefore never calls activate. It used to, unconditionally,
    // on every manifest on every run -- which was safe only until the first
    // operator command: after an `action disable` or `action activate` of a
    // different version, that unconditional activate would silently overwrite
    // the operator's own decision on the next `docker compose up` (GitHub
    // issue #2). Publish's own checksum-match branch has no such blind spot,
    // since it returns before touching enabled/is_default at all.
    public static async Task<CliEnvelope> ExecuteAsync(
        IMigrationRunner runner,
        IActionPublisher publisher,
        ISchemaValidator validator,
        JsonNode manifestSchema,
        string migrationsDirectory,
        string manifestsDirectory,
        CancellationToken cancellationToken,
        WorkflowMapValidator? flowMapValidator = null,
        IWorkflowFlowPublisher? flowPublisher = null,
        string? flowMapsDirectory = null,
        IDeliveryProfile? deliveryProfile = null,
        int? outboxLeaseMs = null)
    {
        var migrationCommand = new ParsedCommand("bootstrap", [migrationsDirectory], new Dictionary<string, string>());
        var migrationEnvelope = await MigrationApplyCommand.ExecuteAsync(migrationCommand, runner, cancellationToken);
        if (migrationEnvelope.Status != "ok")
        {
            return migrationEnvelope;
        }

        // Straight after the migrations that create the policy row, and
        // before anything can claim against it. Re-applied on every run, so a
        // stack restarted with a different COURSE_TEST_PROFILE ends up with the
        // lease that profile asks for rather than the one the last boot left.
        if (deliveryProfile is not null && outboxLeaseMs is { } leaseMs)
        {
            var leaseRaw = await deliveryProfile.SetLeaseMsAsync(leaseMs, cancellationToken);
            if (FailureEnvelope(leaseRaw) is { } leaseFailure)
            {
                return leaseFailure;
            }
        }

        var publishedActions = new List<object>();

        // Lexicographic order so a repeat run and its logs stay stable regardless
        // of how the filesystem happens to list the directory.
        var manifestPaths = Directory.EnumerateFiles(manifestsDirectory, "*.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal);

        foreach (var path in manifestPaths)
        {
            var manifestResult = await ManifestLoader.LoadAsync(path, validator, manifestSchema, cancellationToken);
            if (manifestResult.Envelope is not null)
            {
                return manifestResult.Envelope;
            }

            var manifest = manifestResult.Manifest!;

            var publishRaw = await publisher.PublishAsync(manifest, cancellationToken);
            if (FailureEnvelope(publishRaw) is { } publishFailure)
            {
                return publishFailure;
            }

            publishedActions.Add(new { key = manifest.Key, version = manifest.Version });
        }

        var activatedFlows = new List<object>();
        var keptActiveFlows = new List<object>();

        if (flowMapsDirectory is not null)
        {
            // One listing for the whole loop, taken before anything is
            // published: which flows an operator has already pinned is a
            // property of the database as bootstrap found it, not of the
            // database halfway through bootstrap's own publishing.
            var alreadyActive = await ActiveFlowVersionsAsync(flowPublisher!, cancellationToken);

            foreach (var fileName in ProductionFlowMapFiles)
            {
                var path = Path.Combine(flowMapsDirectory, fileName);
                var loaded = await FlowMapLoader.LoadAsync(path, flowMapValidator!, cancellationToken);
                if (loaded.Envelope is not null)
                {
                    return loaded.Envelope;
                }

                var hash = JsonCanonicalizer.Sha256Hex(loaded.Document!);
                var publishRaw = await flowPublisher!.PublishAsync(loaded.Document!, hash, cancellationToken);
                if (FailureEnvelope(publishRaw) is { } publishFailure)
                {
                    return publishFailure;
                }

                var flowName = publishRaw["flowName"]!.GetValue<string>();
                var flowVersion = publishRaw["flowVersion"]!.GetValue<int>();

                // First install only. This used to be unconditional, on the
                // assumption that no other version of either bundled flow would
                // ever exist -- which held right up to the first `flow publish`
                // + `flow activate` of a v2, after which every `docker compose
                // up` silently moved the active version back to the bundled v1.
                // Same operator-choice rule the action half above already
                // follows: a repeat run publishes, it does not re-decide.
                if (alreadyActive.TryGetValue(flowName, out var pinnedVersion))
                {
                    keptActiveFlows.Add(new { flowName, flowVersion = pinnedVersion });
                    continue;
                }

                var activateRaw = await flowPublisher.ActivateAsync(flowName, flowVersion, cancellationToken);
                if (FailureEnvelope(activateRaw) is { } activateFailure)
                {
                    return activateFailure;
                }

                activatedFlows.Add(new { flowName, flowVersion });
            }
        }

        return CliEnvelope.Ok(new
        {
            resource = "bootstrap",
            operation = "completed",
            migration = migrationEnvelope.Result,
            actions = publishedActions,
            flows = activatedFlows,
            keptActiveFlows,
            outboxLeaseMs,
        });
    }

    // flowName -> the version an operator (or an earlier bootstrap) already
    // activated. Absent from the map means the flow has no active version at
    // all, which is the only case bootstrap gets to decide.
    private static async Task<Dictionary<string, int>> ActiveFlowVersionsAsync(
        IWorkflowFlowPublisher flowPublisher, CancellationToken cancellationToken)
    {
        var listed = await flowPublisher.ListAsync(cancellationToken);

        return listed.AsArray()
            .Where(entry => entry!["isActive"]!.GetValue<bool>())
            .ToDictionary(
                entry => entry!["flowName"]!.GetValue<string>(),
                entry => entry!["flowVersion"]!.GetValue<int>(),
                StringComparer.Ordinal);
    }

    // Shared by every raw jsonb result this method reads (action publish, flow
    // publish, flow activate): each one is {"status": "ok", ...} or
    // {"status": "error", "code": ..., "message": ...}, so the "did this
    // stored-function call fail" check and its Fail-envelope shape only need
    // writing once.
    private static CliEnvelope? FailureEnvelope(JsonNode raw) =>
        raw["status"]!.GetValue<string>() == "ok"
            ? null
            : CliEnvelope.Fail(raw["code"]!.GetValue<string>(), raw["message"]!.GetValue<string>());
}
