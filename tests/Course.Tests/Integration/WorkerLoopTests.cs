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
        Assert.Equal("SUCCEEDED", jobState);

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
    public async Task RunOnce_MappingDrawsFromFlowStartData_OnTheVeryFirstStep_Succeeds()
    {
        var flowName = NewFlowName();
        await PublishAndActivateFirstStepMappingFlowAsync(flowName);
        var startData = new JsonObject { ["startValue"] = "from-flow-start" };
        var processId = await StartProcessAsync(flowName, startData);

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.Succeeded, result.Outcome);

        var processData = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select process_data::text from workflow.process_instance where process_id = @processId", ("processId", processId));
        var parsed = JsonNode.Parse(processData)!;
        Assert.Equal("from-flow-start", parsed["startValue"]!.GetValue<string>());
        Assert.Equal("from-flow-start", parsed["probe"]!["value"]!.GetValue<string>());
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

    // Ticket 01 acceptance: a claim call that throws (cold-start race against
    // bootstrap's migrations) must not crash RunOnceAsync -- it comes back as
    // ClaimFailed, and once the underlying source starts working again the
    // very next call reaches and processes the waiting job.
    [Fact]
    public async Task RunOnce_ClaimThrows_ReturnsClaimFailed_ThenSucceedsOnceClaimingRecovers()
    {
        var flowName = NewFlowName();
        var value = $"claim-retry-{Guid.NewGuid():N}";
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "SUCCESS", value: value);
        var processId = await StartProcessAsync(flowName);

        var owner = NewOwner();
        var flakyRuntime = new FlakyClaimJobRuntime(factory.Postgres.JobRuntime, failuresBeforeSuccess: 2);
        var loop = CreateLoop(owner, flakyRuntime);

        var first = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.ClaimFailed, first.Outcome);

        var second = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.ClaimFailed, second.Outcome);

        var third = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, third.Outcome);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("SUCCEEDED", jobState);
    }

    [Fact]
    public async Task RunOnce_NoReadyJob_ReturnsNoJob()
    {
        await DrainClaimableJobsAsync();

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.NoJob, result.Outcome);
    }

    // Ticket 10: mapping_missing is non-retryable, and every fixture task in
    // this file declares max_attempts = 1 -- so the one failed attempt this
    // produces already exhausts the budget and the job goes straight to DEAD,
    // never back to LEASED for a later retry.
    [Fact]
    public async Task RunOnce_MappingSourceMissing_DoesNotCallTheAction_AndFailsTheJobImmediately()
    {
        var flowName = NewFlowName();
        await PublishAndActivateMissingMappingFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.MappingMissing, result.Outcome);
        Assert.Equal(WorkflowErrorCodes.MappingMissing, result.ErrorCode);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("DEAD", jobState);

        var attempt = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select a.state, a.error_code from workflow.workflow_attempt a "
                + "join workflow.workflow_job j on j.job_id = a.job_id where j.process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), ErrorCode: reader.GetString(1));
        });
        Assert.Equal("FAILED", attempt.State);
        Assert.Equal(WorkflowErrorCodes.MappingMissing, attempt.ErrorCode);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("FAILED", processState);

        var dispatchCount = await CountDispatchesForProcessAsync(processId);
        Assert.Equal(0, dispatchCount);
    }

    // UNDECLARED_OUTCOME is a pipeline-level contract violation (the target's
    // outcome is not one of the manifest's declared outcomes), non-retryable
    // the same way -- this fixture's task also has max_attempts = 1, so this
    // single attempt exhausts the budget immediately.
    [Fact]
    public async Task RunOnce_ContractViolation_RollsBackTheWholeTransaction_NoDispatchRow_JobFailsImmediately()
    {
        var flowName = NewFlowName();
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "UNDECLARED_OUTCOME", value: "hello-world");
        var processId = await StartProcessAsync(flowName);

        var result = await CreateLoop(NewOwner()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkerStepOutcome.ActionRejected, result.Outcome);
        Assert.Equal("action.contract_violation", result.ErrorCode);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("DEAD", jobState);

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
        Assert.Equal(1, await EffectCountAsync(value));
    }

    // Ticket 11 acceptance: the after_job_claim boundary fires strictly after
    // the claim (lease + attempt already committed by ClaimAsync) and before
    // the target is ever invoked -- so cancelling the block here must leave
    // zero dispatches/effects and the lease still held by this owner, exactly
    // what a real SIGKILL at this boundary leaves for a second worker to
    // reclaim once the (test-profile) lease expires.
    [Fact]
    public async Task RunOnce_FailpointAfterJobClaim_BlocksBeforeInvokingTheAction_WithZeroDispatchesOnCancel()
    {
        var flowName = NewFlowName();
        var value = $"failpoint-claim-{Guid.NewGuid():N}";
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "SUCCESS", value: value);
        var processId = await StartProcessAsync(flowName);

        var owner = NewOwner();
        var output = new StringWriter();
        var loop = CreateLoop(owner, new FailpointGate("after_job_claim", output));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunOnceAsync(cts.Token));

        AssertSingleAck(output.ToString(), "after_job_claim", owner);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("LEASED", jobState);

        Assert.Equal(0, await CountDispatchesForProcessAsync(processId));
        Assert.Equal(0, await EffectCountAsync(value));
    }

    // Ticket 11 acceptance: the after_action_before_finish boundary fires
    // after the effect and response validation already happened inside the
    // still-open action transaction, strictly before finish_job/commit.
    // Cancelling here unwinds the `await using` transaction/connection
    // (an explicit rollback, standing in for the server-side rollback a real
    // SIGKILL triggers) -- the effect and dispatch this attempt produced must
    // not survive it.
    [Fact]
    public async Task RunOnce_FailpointAfterActionBeforeFinish_BlocksWithOpenTransaction_RollsBackOnCancel_NoEffectSurvives()
    {
        var flowName = NewFlowName();
        var value = $"failpoint-finish-{Guid.NewGuid():N}";
        await PublishAndActivateConstantsOnlyFlowAsync(flowName, mode: "SUCCESS", value: value);
        var processId = await StartProcessAsync(flowName);

        var owner = NewOwner();
        var output = new StringWriter();
        var loop = CreateLoop(owner, new FailpointGate("after_action_before_finish", output));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.RunOnceAsync(cts.Token));

        AssertSingleAck(output.ToString(), "after_action_before_finish", owner);

        Assert.Equal(0, await EffectCountAsync(value));
        Assert.Equal(0, await CountDispatchesForProcessAsync(processId));
    }

    // Scoped by business_value the way CountDispatchesForProcessAsync is
    // scoped by process id -- the effect table, like action_dispatches, is
    // shared across every test in this class.
    private Task<long> EffectCountAsync(string value) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select count(*) from workflow_probe.effect where business_value = @value", connection);
            command.Parameters.AddWithValue("value", value);
            return (long)(await command.ExecuteScalarAsync())!;
        });

    // Exactly one structured line, matching SPEC's own shape
    // {"event":"failpoint.reached","name":"...","instanceId":"..."} -- a
    // second line (fired on a poll iteration rather than a successful claim)
    // is the exact regression `parse_failpoint_acks` would flag.
    private static void AssertSingleAck(string output, string expectedName, string expectedInstanceId)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(lines);
        var ack = JsonNode.Parse(line)!;
        Assert.Equal("failpoint.reached", ack["event"]!.GetValue<string>());
        Assert.Equal(expectedName, ack["name"]!.GetValue<string>());
        Assert.Equal(expectedInstanceId, ack["instanceId"]!.GetValue<string>());
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
    private WorkerLoop CreateLoop(string owner) => CreateLoop(owner, failpoints: null);

    private WorkerLoop CreateLoop(string owner, IFailpointGate? failpoints) =>
        CreateLoop(owner, factory.Postgres.JobRuntime, failpoints);

    private WorkerLoop CreateLoop(string owner, IWorkflowJobRuntime jobRuntime) => CreateLoop(owner, jobRuntime, failpoints: null);

    private WorkerLoop CreateLoop(string owner, IWorkflowJobRuntime jobRuntime, IFailpointGate? failpoints)
    {
        var dataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            owner, dataSource, jobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes, failpoints);
    }

    // Delegates every call to the real runtime except the first N ClaimAsync
    // calls, which throw instead -- standing in for workflow.claim_jobs being
    // undefined or ungranted while bootstrap's migrations haven't landed yet
    // (ticket 01). Any Exception works for this test: WorkerLoop's catch around
    // the claim is not typed to PostgresException specifically.
    private sealed class FlakyClaimJobRuntime(IWorkflowJobRuntime inner, int failuresBeforeSuccess) : IWorkflowJobRuntime
    {
        private int remainingFailures = failuresBeforeSuccess;

        public Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref remainingFailures) >= 0)
            {
                throw new InvalidOperationException("simulated claim failure: workflow.claim_jobs not ready yet");
            }

            return inner.ClaimAsync(owner, leaseMs, cancellationToken);
        }

        public Task<JsonNode> FinishAsync(
            Guid jobId, string owner, int leaseVersion, string outcome, JsonNode result, CancellationToken cancellationToken) =>
            inner.FinishAsync(jobId, owner, leaseVersion, outcome, result, cancellationToken);

        public Task<JsonNode> FailAsync(
            Guid jobId, string owner, int leaseVersion, string errorCode, bool retryable, CancellationToken cancellationToken) =>
            inner.FailAsync(jobId, owner, leaseVersion, errorCode, retryable, cancellationToken);
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
    // -- no process-data dependency at all, unlike the first-step-mapping
    // fixture below which does depend on flow start's own data.
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

    // process_data is seeded from flow start's own data (migration 028), but
    // StartProcessAsync below always starts with an empty object, and this
    // fixture's source pointer doesn't match any key a completed step could
    // have produced either -- so it stays genuinely unresolvable.
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

    // The very first step maps its payload straight from flow start's own
    // data (migration 028: process_data is seeded from start_data), proving
    // a caller can parameterize the first step via `flow start --data`
    // without a constants-only detour.
    private async Task PublishAndActivateFirstStepMappingFlowAsync(string flowName)
    {
        var document = JsonNode.Parse($$"""
            {
              "flow_name": "{{flowName}}",
              "version": 1,
              "start_step": "probe",
              "steps": [
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
                    "input_mapping": {"/value": "/startValue"},
                    "input_constants": {"mode": "SUCCESS"}
                  }
                },
                {"key": "wait", "type": "wait_signal", "signal_type": "test.signal", "outcome": "SIGNALED"},
                {"key": "end", "type": "end", "outcome": "DONE"}
              ],
              "transitions": [
                {"from": "probe", "outcome": "SUCCEEDED", "to": "wait"},
                {"from": "wait", "outcome": "SIGNALED", "to": "end"}
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

    private Task<Guid> StartProcessAsync(string flowName) => StartProcessAsync(flowName, new JsonObject());

    private async Task<Guid> StartProcessAsync(string flowName, JsonObject startData)
    {
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), startData, JsonCanonicalizer.Sha256Hex(startData), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start worker test process: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }
}
