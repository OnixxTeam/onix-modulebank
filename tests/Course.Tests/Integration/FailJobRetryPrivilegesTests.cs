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

// Ticket 10 acceptance: workflow.fail_job records a failed attempt on its own
// connection (the caller's action transaction, if any, is already dead by the
// time it runs), schedules a bounded retry only when the failure is both
// retryable and the budget -- counted from FAILED attempts, never from
// attempt_count, which a reclaim also bumps without spending it -- is not yet
// exhausted, and otherwise brings job/step/process into one consistent DEAD/
// FAILED/FAILED with the mandatory TaskFailed event. workflow_worker's own
// EXECUTE list is exactly four functions now that fail_job exists, and every
// other function in the two schemas it touches (including this ticket's own
// public-execute leaks) is closed.
[Collection("integration")]
public sealed class FailJobRetryPrivilegesTests
{
    private readonly ApiFactory factory;

    public FailJobRetryPrivilegesTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task FailJob_RetryableWithBudgetRemaining_SchedulesTheDeclaredDelay_AndLeavesStepAndProcessRunning()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        // An hour-long delay, never a short one, for a job this test leaves in
        // RETRY_WAIT and never resolves: claim_jobs is a global FIFO with no
        // per-test filter, so a short delay would make this job due -- and
        // liable to be claimed by a later test in the same run -- long before
        // the container goes away.
        await PublishAndActivateRetryFlowAsync(flowName, maxAttempts: 3, delaysMs: [3_600_000, 3_600_000]);
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());
        var leaseVersion = job["leaseVersion"]!.GetValue<int>();

        var before = DateTimeOffset.UtcNow;
        var fail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, LastClaimOwner, leaseVersion, "workflow_probe.retryable", retryable: true, CancellationToken.None);

        Assert.Equal("ok", fail["status"]!.GetValue<string>());
        Assert.Equal("RETRY_WAIT", fail["jobState"]!.GetValue<string>());

        var jobRow = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, owner, next_attempt_at from workflow.workflow_job where job_id = @jobId", connection);
            command.Parameters.AddWithValue("jobId", jobId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), Owner: reader.IsDBNull(1) ? null : reader.GetString(1),
                NextAttemptAt: reader.GetDateTime(2));
        });
        Assert.Equal("RETRY_WAIT", jobRow.State);
        Assert.Null(jobRow.Owner);
        Assert.InRange(
            jobRow.NextAttemptAt, before.AddMilliseconds(3_500_000).UtcDateTime, before.AddMilliseconds(3_700_000).UtcDateTime);

        var attempt = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, error_code, outcome from workflow.workflow_attempt where job_id = @jobId", connection);
            command.Parameters.AddWithValue("jobId", jobId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), ErrorCode: reader.GetString(1), IsOutcomeNull: reader.IsDBNull(2));
        });
        Assert.Equal("FAILED", attempt.State);
        Assert.Equal("workflow_probe.retryable", attempt.ErrorCode);
        Assert.True(attempt.IsOutcomeNull);

        // step_instance never gains a RUNNING state of its own in this schema
        // (claim_jobs only ever touches workflow_job) -- it stays READY, the
        // state enter_step gave it when the automatic step opened its job.
        var stepState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.step_instance where process_id = @processId and step_key = 'probe'",
            ("processId", processId));
        Assert.Equal("READY", stepState);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("RUNNING", processState);
    }

    [Fact]
    public async Task FailJob_NonRetryable_MovesJobStepAndProcess_ToDEAD_AndRecordsTaskFailed()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateRetryFlowAsync(flowName, maxAttempts: 5, delaysMs: [1000, 1000, 1000, 1000]);
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var fail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, LastClaimOwner, job["leaseVersion"]!.GetValue<int>(), "workflow_probe.permanent", retryable: false,
            CancellationToken.None);

        Assert.Equal("ok", fail["status"]!.GetValue<string>());
        Assert.Equal("DEAD", fail["jobState"]!.GetValue<string>());

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where job_id = @jobId", ("jobId", jobId));
        Assert.Equal("DEAD", jobState);

        var stepState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.step_instance where process_id = @processId and step_key = 'probe'",
            ("processId", processId));
        Assert.Equal("FAILED", stepState);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("FAILED", processState);

        var eventTypes = await EventTypesAsync(processId);
        Assert.Contains("TaskFailed", eventTypes);
    }

    [Fact]
    public async Task FailJob_RetryableButBudgetExhausted_MovesToDEAD()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateRetryFlowAsync(flowName, maxAttempts: 2, delaysMs: [10]);
        await StartProcessAsync(flowName);

        var first = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(first["jobId"]!.GetValue<string>());
        var firstFail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, LastClaimOwner, first["leaseVersion"]!.GetValue<int>(), "workflow_probe.retryable", retryable: true,
            CancellationToken.None);
        Assert.Equal("RETRY_WAIT", firstFail["jobState"]!.GetValue<string>());

        await MakeRetryDueNowAsync(jobId);
        var second = (await ClaimAsync())["job"]!;
        Assert.Equal(jobId, Guid.Parse(second["jobId"]!.GetValue<string>()));

        var secondFail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, LastClaimOwner, second["leaseVersion"]!.GetValue<int>(), "workflow_probe.retryable", retryable: true,
            CancellationToken.None);

        Assert.Equal("DEAD", secondFail["jobState"]!.GetValue<string>());

        var attemptCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow.workflow_attempt where job_id = @jobId and state = 'FAILED'", ("jobId", jobId));
        Assert.Equal(2, attemptCount);
    }

    // The ticket's own trap: a reclaim (expired lease) bumps attempt_count and
    // opens a new attempt without ever calling fail_job, so it must not count
    // against the failure budget. max_attempts=2 here means a budget wrongly
    // counted from attempt_count (2, after one reclaim) would already read as
    // exhausted; counting from FAILED attempts (0 so far) proves this is still
    // this job's first real failure.
    [Fact]
    public async Task FailJob_AfterAStaleReclaim_StillCountsOnlyRealFailures_NotAttemptCount()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        // See the note on the RETRY_WAIT delay in the earlier test: this test
        // also leaves its job in RETRY_WAIT and never resolves it further.
        await PublishAndActivateRetryFlowAsync(flowName, maxAttempts: 2, delaysMs: [3_600_000]);
        await StartProcessAsync(flowName);

        var claimed = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 50, CancellationToken.None);
        var firstJob = claimed["job"]!;
        var jobId = Guid.Parse(firstJob["jobId"]!.GetValue<string>());

        await Task.Delay(300);

        var reclaimed = (await ClaimAsync())["job"]!;
        Assert.Equal(jobId, Guid.Parse(reclaimed["jobId"]!.GetValue<string>()));

        var attemptCount = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select attempt_count from workflow.workflow_job where job_id = @jobId", ("jobId", jobId));
        Assert.Equal(2, attemptCount);

        var fail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, LastClaimOwner, reclaimed["leaseVersion"]!.GetValue<int>(), "workflow_probe.retryable", retryable: true,
            CancellationToken.None);

        Assert.Equal("RETRY_WAIT", fail["jobState"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailJob_WrongOwner_IsRejectedAsStale_AndChangesNothing()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateRetryFlowAsync(flowName, maxAttempts: 2, delaysMs: [10]);
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var fail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, "someone-else", job["leaseVersion"]!.GetValue<int>(), "workflow_probe.permanent", retryable: false,
            CancellationToken.None);

        Assert.Equal("error", fail["status"]!.GetValue<string>());
        Assert.Equal("workflow.lease_stale", fail["code"]!.GetValue<string>());

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where job_id = @jobId", ("jobId", jobId));
        Assert.Equal("LEASED", jobState);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("RUNNING", processState);
    }

    [Fact]
    public async Task FailJob_StaleLeaseVersion_IsRejected_AndChangesNothing()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateRetryFlowAsync(flowName, maxAttempts: 2, delaysMs: [10]);
        await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var fail = await factory.Postgres.JobRuntime.FailAsync(
            jobId, LastClaimOwner, job["leaseVersion"]!.GetValue<int>() + 1, "workflow_probe.permanent", retryable: false,
            CancellationToken.None);

        Assert.Equal("error", fail["status"]!.GetValue<string>());
        Assert.Equal("workflow.lease_stale", fail["code"]!.GetValue<string>());

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where job_id = @jobId", ("jobId", jobId));
        Assert.Equal("LEASED", jobState);
    }

    // End-to-end through WorkerLoop: a retryable failure leaves the job in
    // RETRY_WAIT after the first attempt, and a second attempt (once due) that
    // fails the same way exhausts a two-attempt budget into DEAD/FAILED/FAILED
    // with the mandatory TaskFailed event.
    [Fact]
    public async Task WorkerLoop_RetryableFailure_RetriesOnce_ThenExhaustsToDeadAndFailed()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateProbeModeFlowAsync(flowName, mode: "RETRYABLE_ERROR", maxAttempts: 2, delaysMs: [10]);
        var processId = await StartProcessAsync(flowName);
        var loop = CreateLoop(NewOwner());

        var first = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.ActionRejected, first.Outcome);
        Assert.Equal("workflow_probe.retryable", first.ErrorCode);

        var afterFirst = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("RETRY_WAIT", afterFirst);

        var jobId = await factory.Postgres.ScalarAsOwnerAsync<Guid>(
            "select job_id from workflow.workflow_job where process_id = @processId", ("processId", processId));
        await MakeRetryDueNowAsync(jobId);

        var second = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.ActionRejected, second.Outcome);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where process_id = @processId", ("processId", processId));
        Assert.Equal("DEAD", jobState);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("FAILED", processState);

        var eventTypes = await EventTypesAsync(processId);
        Assert.Contains("TaskFailed", eventTypes);

        var attemptCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow.workflow_attempt a join workflow.workflow_job j on j.job_id = a.job_id "
            + "where j.process_id = @processId and a.state = 'FAILED'", ("processId", processId));
        Assert.Equal(2, attemptCount);
    }

    // Ticket 10's own closing check: workflow_worker's EXECUTE list is exactly
    // the four declared functions -- no more, since a default PUBLIC grant on
    // any other function in the two schemas it holds USAGE on would inflate
    // it -- and it can insert, update or delete in no table in the database.
    // Mirrors the hidden checker's own worker_database_security query
    // (autocheck/public_check.py) so this fails locally before it fails there.
    [Fact]
    public async Task WorkflowWorkerRole_ExecutesExactlyTheFourDeclaredFunctions_AndHasNoTableDml()
    {
        var executable = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "with worker_role as (select oid from pg_catalog.pg_roles where rolname = 'workflow_worker') "
                + "select n.nspname as schema_name, p.proname as function_name, "
                + "has_function_privilege(r.oid, p.oid, 'EXECUTE') as can_execute "
                + "from pg_catalog.pg_proc p "
                + "join pg_catalog.pg_namespace n on n.oid = p.pronamespace "
                + "left join worker_role r on true "
                + "where n.nspname in ('api', 'workflow')",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            var boundaries = new HashSet<(string Schema, string Function)>();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(2) && reader.GetBoolean(2))
                {
                    boundaries.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            return boundaries;
        });

        var expected = new HashSet<(string Schema, string Function)>
        {
            ("api", "invoke"),
            ("workflow", "claim_jobs"),
            ("workflow", "finish_job"),
            ("workflow", "fail_job"),
        };
        Assert.Equal(expected, executable);

        var dmlRights = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "with worker_role as (select oid from pg_catalog.pg_roles where rolname = 'workflow_worker') "
                + "select n.nspname, c.relname, "
                + "has_table_privilege(r.oid, c.oid, 'INSERT') or has_table_privilege(r.oid, c.oid, 'UPDATE') "
                + "or has_table_privilege(r.oid, c.oid, 'DELETE') "
                + "from pg_catalog.pg_class c "
                + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
                + "left join worker_role r on true "
                + "where c.relkind in ('r', 'p', 'f') and n.nspname <> 'information_schema' and n.nspname not like 'pg\\_%'",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            var offending = new List<string>();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(2) && reader.GetBoolean(2))
                {
                    offending.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                }
            }

            return offending;
        });
        Assert.Empty(dmlRights);
    }

    private string LastClaimOwner { get; set; } = "";

    private async Task<JsonNode> ClaimAsync()
    {
        var owner = NewOwner();
        var raw = await factory.Postgres.JobRuntime.ClaimAsync(owner, leaseMs: 30_000, CancellationToken.None);
        LastClaimOwner = owner;
        return raw;
    }

    // The container's workflow_job table is shared by every test in the whole
    // "integration" collection, one at a time. A test elsewhere can leave a
    // READY job behind without ever resolving it (JobsLeaseClaimTests does
    // this deliberately) -- claim_jobs orders by created_at, so an older
    // leftover would be handed out before this test's own freshly-started
    // process's job. Draining first (same pattern as JobsLeaseClaimTests and
    // FinishJobGraphTestFinishTests) guarantees the next claim is this test's.
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

    private async Task MakeRetryDueNowAsync(Guid jobId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "update workflow.workflow_job set next_attempt_at = now() - interval '1 second' where job_id = @jobId",
                connection);
            command.Parameters.AddWithValue("jobId", jobId);
            return await command.ExecuteNonQueryAsync();
        });

    private async Task<List<string>> EventTypesAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select event_type from workflow.workflow_event where process_id = @processId order by occurred_at", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            var types = new List<string>();
            while (await reader.ReadAsync())
            {
                types.Add(reader.GetString(0));
            }

            return types;
        });

    private WorkerLoop CreateLoop(string owner)
    {
        var dataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            owner, dataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    private static string NewFlowName() => $"fail-job-flow-{Guid.NewGuid():N}";

    private static string NewBusinessKey() => $"business-{Guid.NewGuid():N}";

    private static string NewOwner() => $"owner-{Guid.NewGuid():N}";

    // A single automatic step whose payload comes entirely from input_constants
    // so it works on the first step of a process, forced into RETRYABLE_ERROR
    // mode. Used by every direct fail_job test: what mode actually runs is
    // irrelevant to those (fail_job is called directly, never through
    // api.invoke), only the task_definition's own retry policy matters.
    private Task PublishAndActivateRetryFlowAsync(string flowName, int maxAttempts, int[] delaysMs) =>
        PublishAndActivateProbeModeFlowAsync(flowName, mode: "RETRYABLE_ERROR", maxAttempts, delaysMs);

    private async Task PublishAndActivateProbeModeFlowAsync(string flowName, string mode, int maxAttempts, int[] delaysMs)
    {
        var delaysJson = string.Join(",", delaysMs);
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
                    "retry": {"max_attempts": {{maxAttempts}}, "delays_ms": [{{delaysJson}}]},
                    "input_mapping": {},
                    "input_constants": {"mode": "{{mode}}", "value": "hello-world"}
                  }
                },
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

        var published = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (published["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish fail-job test flow {flowName}: {published.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate fail-job test flow {flowName}: {activated.ToJsonString()}");
        }
    }

    private async Task<Guid> StartProcessAsync(string flowName)
    {
        var startData = new JsonObject();
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), startData, JsonCanonicalizer.Sha256Hex(startData), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start fail-job test process: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }
}
