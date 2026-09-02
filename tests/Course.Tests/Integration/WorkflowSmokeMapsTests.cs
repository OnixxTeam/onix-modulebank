using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Course.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Workflow.Core.Canonicalization;
using Workflow.Core.Maps;
using Workflow.Core.Security;
using Workflow.Worker;

namespace Course.Tests.Integration;

// Ticket 12 acceptance: workflow-smoke v1 walks automatic -> wait_signal ->
// end and covers both of workflow.probe's declared outcomes with transitions
// (SUCCEEDED -> wait, ROUTE_MANUAL -> review); v2 keeps the same flow_name
// but changes what happens after the automatic step's SUCCEEDED outcome in a
// way visible from outside (no wait_signal at all -- v2 completes in the same
// worker pass that runs the automatic step). A process started on v1 before
// v2 is activated stays pinned to v1 and finishes on it; a process started
// after uses v2 observably. workflow-smoke-manual is the ticket's own
// "отдельная карта" that actually reaches WAITING_MANUAL at runtime (v1/v2's
// own ROUTE_MANUAL transition is structurally present but never taken, since
// workflow.probe's mode is a fixed input_constant baked into the map --
// process_data starts as '{}' on every process, so the very first step can
// never branch on start data). All three files are read from db/maps -- the
// exact files src/Course.Cli/Dockerfile bakes into the image.
[Collection("integration")]
public sealed class WorkflowSmokeMapsTests
{
    private readonly ApiFactory factory;

    public WorkflowSmokeMapsTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task WorkflowSmoke_V1ThenV2_PinsOldProcess_RoutesNewProcess()
    {
        await PublishAndActivateAsync("workflow-smoke.v1.flow.json");

        var v1Process = await StartProcessAsync("workflow-smoke");
        Assert.Equal(WorkerStepOutcome.Succeeded, (await CreateLoop().RunOnceAsync(CancellationToken.None)).Outcome);

        var afterAutomatic = await GetProcessAsync(v1Process);
        Assert.Equal("WAITING_SIGNAL", afterAutomatic.State);
        Assert.Equal(1, afterAutomatic.FlowVersion);

        await PublishAndActivateAsync("workflow-smoke.v2.flow.json");

        // Activating v2 must not move a process already running on v1.
        var stillV1 = await GetProcessAsync(v1Process);
        Assert.Equal(1, stillV1.FlowVersion);
        Assert.Equal("WAITING_SIGNAL", stillV1.State);

        await SignalAsync(v1Process, "workflow-smoke.signal", new JsonObject());
        var v1Final = await GetProcessAsync(v1Process);
        Assert.Equal("COMPLETED", v1Final.State);
        Assert.Equal(1, v1Final.FlowVersion);
        Assert.Equal("DONE_V1", await EndOutcomeAsync(v1Process));

        // Started after v2's activation: v2's SUCCEEDED path skips wait_signal
        // entirely, so one worker pass completes it outright -- an externally
        // observable difference from v1's own path above, not just a version
        // number bump.
        var v2Process = await StartProcessAsync("workflow-smoke");
        Assert.Equal(WorkerStepOutcome.Succeeded, (await CreateLoop().RunOnceAsync(CancellationToken.None)).Outcome);

        var v2Final = await GetProcessAsync(v2Process);
        Assert.Equal(2, v2Final.FlowVersion);
        Assert.Equal("COMPLETED", v2Final.State);
        Assert.Equal("DONE_V2", await EndOutcomeAsync(v2Process));
    }

    [Fact]
    public async Task WorkflowSmokeManual_ReachesWaitingManual_AndSurvivesANewWorkerInstance()
    {
        await PublishAndActivateAsync("workflow-smoke-manual.v1.flow.json");

        var manualProcess = await StartProcessAsync("workflow-smoke-manual");
        Assert.Equal(WorkerStepOutcome.Succeeded, (await CreateLoop().RunOnceAsync(CancellationToken.None)).Outcome);

        var waitingManual = await GetProcessAsync(manualProcess);
        Assert.Equal("WAITING_MANUAL", waitingManual.State);
        Assert.Equal("review", waitingManual.CurrentStepKey);

        // A brand-new WorkerLoop instance (its own data source, its own
        // owner) stands in for a recreated worker container: WAITING_MANUAL
        // has no job to claim, so it must find nothing and leave the process
        // exactly where it was.
        var recreatedWorkerResult = await CreateLoop().RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.NoJob, recreatedWorkerResult.Outcome);

        var afterRecreate = await GetProcessAsync(manualProcess);
        Assert.Equal("WAITING_MANUAL", afterRecreate.State);
        Assert.Equal("review", afterRecreate.CurrentStepKey);
    }

    private async Task PublishAndActivateAsync(string fileName)
    {
        var document = await MapLoader.LoadAsync(RepoPaths.Combine("db", "maps", fileName), CancellationToken.None);
        var flowName = document["flow_name"]!.GetValue<string>();
        var version = document["version"]!.GetValue<int>();

        var published = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (published["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish {fileName}: {published.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, version, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate {fileName}: {activated.ToJsonString()}");
        }
    }

    private async Task<Guid> StartProcessAsync(string flowName)
    {
        var startData = new JsonObject();
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), startData, JsonCanonicalizer.Sha256Hex(startData), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start {flowName} process: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }

    private Task<JsonNode> SignalAsync(Guid processId, string signalType, JsonNode body) =>
        factory.Postgres.ProcessRuntime.SignalAsync(
            processId, NewMessageId(), signalType, JsonCanonicalizer.Sha256Hex(body), body, CancellationToken.None);

    // Mirrors WorkerLoopTests/SignalTests.CreateLoop: reuses the app host's
    // own ISchemaValidator/IActionExecutor singletons so a second
    // JsonSchemaValidator never tries to re-register a catalog schema an
    // earlier HTTP call already compiled (Json.Schema.Net's process-wide
    // SchemaRegistry throws on a second registration of the same $id). A
    // fresh NpgsqlDataSource/owner every call -- never reused across the two
    // "worker recreate" assertions -- since a real recreate gets a new worker
    // process with a new connection pool, not the same instance polling again.
    private WorkerLoop CreateLoop()
    {
        var dataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            $"owner-{Guid.NewGuid():N}", dataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    private async Task<(string State, int FlowVersion, string CurrentStepKey)> GetProcessAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, flow_version, current_step_key from workflow.process_instance where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), FlowVersion: reader.GetInt32(1), CurrentStepKey: reader.GetString(2));
        });

    private async Task<string> EndOutcomeAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select outcome from workflow.step_instance where process_id = @processId and step_key = 'end'", connection);
            command.Parameters.AddWithValue("processId", processId);
            return (string)(await command.ExecuteScalarAsync())!;
        });

    private static string NewBusinessKey() => $"workflow-smoke-{Guid.NewGuid():N}";

    private static string NewMessageId() => $"message-{Guid.NewGuid():N}";
}
