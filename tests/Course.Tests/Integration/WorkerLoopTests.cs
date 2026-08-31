using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Workflow.Core.Canonicalization;
using Workflow.Core.Runtime;
using Workflow.Core.Security;
using Workflow.Worker;

namespace Course.Tests.Integration;

// Ticket 08 acceptance: the worker loop claims a job (its own short,
// separately-committed transaction), maps process data into a payload,
// validates it, calls the target through the shared executor's foreign-
// transaction path and finishes in the same transaction it invoked in --
// committing only on success, rolling back whole on any rejection so no
// dispatch row survives a failed attempt. Every scenario here drives the
// already-activated workflow.probe action (ticket 05), never a name of its
// own -- the loop carries no branch on map, step or action identity.
[Collection("integration")]
public sealed class WorkerLoopTests
{
    private readonly ApiFactory factory;

    public WorkerLoopTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task RunOnce_SuccessfulProbeCall_CommitsEffectAndFinish_AndAdvancesToWaitingSignal()
    {
        var flowName = NewFlowName();
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "SUCCESS", value: "hello-world");
        var processId = await StartProcessAsync(flowName);

        var loop = CreateLoop(NewOwner());

        var result = await loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.Succeeded, result.Outcome);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("WAITING_SIGNAL", processState);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("DONE", jobState);

        var dispatch = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select principal, request_id, status from autocheck.action_dispatches "
                + "where module = 'workflow' and action = 'probe' and principal = @principal order by occurred_at desc limit 1",
                connection);
            command.Parameters.AddWithValue("principal", WorkflowWorkerPrincipal.Name);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (Principal: reader.GetString(0), RequestId: reader.GetString(1), Status: reader.GetString(2));
        });
        Assert.Equal(WorkflowWorkerPrincipal.Name, dispatch.Principal);
        Assert.Equal("OK", dispatch.Status);

        var executionId = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select execution_id from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal(executionId, dispatch.RequestId);

        var effectCount = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select count(*) from workflow_probe.effect where effect_key = @executionId", connection);
            command.Parameters.AddWithValue("executionId", executionId);
            return (long)(await command.ExecuteScalarAsync())!;
        });
        Assert.Equal(1, effectCount);
    }

    [Fact]
    public async Task RunOnce_MappingDrawsFromAPriorStepsResult_AndReachesTheTarget()
    {
        var flowName = NewFlowName();
        await PublishAndActivateMappingChainFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);
        var loop = CreateLoop(NewOwner());

        var seedResult = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, seedResult.Outcome);

        var probeResult = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, probeResult.Outcome);

        var processData = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select process_data::text from workflow.process_instance where process_id = @processId", ("processId", processId));
        var parsed = JsonNode.Parse(processData)!;
        Assert.Equal("seed-value", parsed["seed"]!["value"]!.GetValue<string>());
        Assert.Equal("seed-value", parsed["probe"]!["value"]!.GetValue<string>());

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("WAITING_SIGNAL", processState);
    }

    [Fact]
    public async Task RunOnce_NoReadyJob_ReturnsNoJob()
    {
        await DrainClaimableJobsAsync();

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.NoJob, result.Outcome);
    }

    [Fact]
    public async Task RunOnce_MappingSourceMissing_DoesNotCallTheAction_AndLeavesTheJobLeased()
    {
        var flowName = NewFlowName();
        await PublishAndActivateMissingMappingFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.MappingMissing, result.Outcome);
        Assert.Equal(WorkflowErrorCodes.MappingMissing, result.ErrorCode);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("LEASED", jobState);

        var dispatchCount = await CountDispatchesForProcessAsync(processId);
        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task RunOnce_ContractViolation_RollsBackTheWholeTransaction_NoDispatchRow_JobStaysLeased()
    {
        var flowName = NewFlowName();
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "UNDECLARED_OUTCOME", value: "hello-world");
        var processId = await StartProcessAsync(flowName);

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.ActionRejected, result.Outcome);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("LEASED", jobState);

        var dispatchCount = await CountDispatchesForProcessAsync(processId);
        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task RunOnce_TwoLoopInstances_AgainstOneReadyJob_ExactlyOneSucceeds_WithExactlyOneEffect()
    {
        var flowName = NewFlowName();
        var value = $"racing-{Guid.NewGuid():N}";
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "SUCCESS", value: value);
        await StartProcessAsync(flowName);

        var first = CreateLoop(NewOwner());
        var second = CreateLoop(NewOwner());

        var results = await Task.WhenAll(first.RunOnceAsync(CancellationToken.None), second.RunOnceAsync(CancellationToken.None));
        var outcomes = results.Select(result => result.Outcome).ToList();

        Assert.Single(outcomes, WorkerStepOutcome.Succeeded);
        Assert.Single(outcomes, WorkerStepOutcome.NoJob);

        var effectCount = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select count(*) from workflow_probe.effect where business_value = @value", connection);
            command.Parameters.AddWithValue("value", value);
            return (long)(await command.ExecuteScalarAsync())!;
        });
        Assert.Equal(1, effectCount);
    }

    // Reuses the app host's own ISchemaValidator/IActionExecutor singletons
    // (WebApplicationFactory.Services) instead of new-ing a JsonSchemaValidator
    // per loop: a second instance compiling the same catalog schema text a
    // WorkflowProbeActionTests HTTP call already registered would collide in
    // Json.Schema.Net's process-wide SchemaRegistry (see ApiFactory's own
    // comment on IntegrationSuite). ExecuteInTransactionAsync never touches the
    // executor's own NpgsqlDataSource -- connection and transaction are always
    // the caller's -- so reusing the course_runtime-backed singleton here is
    // safe even though this loop talks to Postgres as workflow_worker.
    private WorkerLoop CreateLoop(string owner)
    {
        var dataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            owner, dataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    // Scoped to this one process's job (via its execution id, the dispatch's own
    // request_id), never a bare principal/module/action count: the container is
    // shared across every test in this class, so an unscoped count would also
    // see every dispatch an earlier test already committed.
    private async Task<long> CountDispatchesForProcessAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select count(*) from api.action_dispatches d "
                + "join workflow.workflow_job j on j.execution_id = d.request_id "
                + "where j.process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            return (long)(await command.ExecuteScalarAsync())!;
        });

    private async Task DrainClaimableJobsAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            var raw = await factory.Postgres.JobRuntime.ClaimAsync("drain", leaseMs: 60_000, CancellationToken.None);
            if (raw["job"] is null || raw["job"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            {
                return;
            }
        }

        throw new InvalidOperationException("queue did not drain within 100 claims");
    }

    private static string NewFlowName() => $"probe-worker-flow-{Guid.NewGuid():N}";

    private static string NewBusinessKey() => $"business-{Guid.NewGuid():N}";

    private static string NewOwner() => $"owner-{Guid.NewGuid():N}";

    private static string ProbeTask(string mode, string value, string inputMapping = "{}") => $$"""
        {
          "module": "workflow",
          "action": "probe",
          "action_version": 1,
          "required_policy": ["workflow:execute"],
          "timeout_ms": 5000,
          "retry": {"max_attempts": 1, "delays_ms": []},
          "input_mapping": {{inputMapping}},
          "input_constants": {"mode": "{{mode}}", "value": "{{value}}"}
        }
        """;

    // A single automatic step whose payload comes entirely from input_constants
    // -- no process-data dependency, so it works on the very first step of a
    // process (process_data starts empty; only a completed step's own result
    // populates it).
    private async Task PublishAndActivateConstantsOnlyFlowAsync(string flowName, string mode, string value)
    {
        var document = JsonNode.Parse($$"""
            {
              "flow_name": "{{flowName}}",
              "version": 1,
              "start_step": "probe",
              "steps": [
                {"key": "probe", "type": "automatic", "task": {{ProbeTask(mode, value)}}},
                {"key": "wait", "type": "wait_signal", "signal_type": "test.signal", "outcome": "SIGNALED"},
                {"key": "manual", "type": "manual", "allowed_outcomes": ["APPROVED"]},
                {"key": "end", "type": "end", "outcome": "DONE"}
              ],
              "transitions": [
                {"from": "probe", "outcome": "SUCCEEDED", "to": "wait"},
                {"from": "probe", "outcome": "ROUTE_MANUAL", "to": "manual"},
                {"from": "wait", "outcome": "SIGNALED", "to": "end"},
                {"from": "manual", "outcome": "APPROVED", "to": "end"}
              ]
            }
            """)!;

        await PublishAndActivateAsync(flowName, document);
    }

    // input_mapping's one entry always misses on the very first step: process
    // data starts as '{}' (SPEC: only a completed step's result populates it),
    // so any source pointer here is unresolvable without a second fixture.
    private async Task PublishAndActivateMissingMappingFlowAsync(string flowName)
    {
        var document = JsonNode.Parse($$"""
            {
              "flow_name": "{{flowName}}",
              "version": 1,
              "start_step": "probe",
              "steps": [
                {"key": "probe", "type": "automatic", "task": {{ProbeTask("SUCCESS", "unused", """{"/mode": "/does/not/exist"}""")}}},
                {"key": "end", "type": "end", "outcome": "DONE"}
              ],
              "transitions": [
                {"from": "probe", "outcome": "SUCCEEDED", "to": "end"},
                {"from": "probe", "outcome": "ROUTE_MANUAL", "to": "end"}
              ]
            }
            """)!;

        await PublishAndActivateAsync(flowName, document);
    }

    // Two automatic steps: "seed" runs on constants alone and its result lands
    // in process_data under its own step key (finish_job's own merge, ticket
    // 07); "probe" then maps its /value from "/seed/value", proving the loop
    // resolves a real RFC 6901 pointer against live process data, not just a
    // literal the map happened to also carry.
    private async Task PublishAndActivateMappingChainFlowAsync(string flowName)
    {
        var document = JsonNode.Parse($$"""
            {
              "flow_name": "{{flowName}}",
              "version": 1,
              "start_step": "seed",
              "steps": [
                {"key": "seed", "type": "automatic", "task": {{ProbeTask("SUCCESS", "seed-value")}}},
                {
                  "key": "probe",
                  "type": "automatic",
                  "task": {
                    "module": "workflow",
                    "action": "probe",
                    "action_version": 1,
                    "required_policy": ["workflow:execute"],
                    "timeout_ms": 5000,
                    "retry": {"max_attempts": 1, "delays_ms": []},
                    "input_mapping": {"/value": "/seed/value"},
                    "input_constants": {"mode": "SUCCESS"}
                  }
                },
                {"key": "wait", "type": "wait_signal", "signal_type": "test.signal", "outcome": "SIGNALED"},
                {"key": "end", "type": "end", "outcome": "DONE"}
              ],
              "transitions": [
                {"from": "seed", "outcome": "SUCCEEDED", "to": "probe"},
                {"from": "probe", "outcome": "SUCCEEDED", "to": "wait"},
                {"from": "wait", "outcome": "SIGNALED", "to": "end"}
              ]
            }
            """)!;

        await PublishAndActivateAsync(flowName, document);
    }

    private async Task PublishAndActivateAsync(string flowName, JsonNode document)
    {
        var published = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (published["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish worker test flow {flowName}: {published.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate worker test flow {flowName}: {activated.ToJsonString()}");
        }
    }

    private async Task<Guid> StartProcessAsync(string flowName)
    {
        var startData = new JsonObject();
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), startData, JsonCanonicalizer.Sha256Hex(startData), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start worker test process: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }
}
