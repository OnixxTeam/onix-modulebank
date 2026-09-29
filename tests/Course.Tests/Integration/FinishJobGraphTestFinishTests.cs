using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Tests.Support;
using Npgsql;
using Workflow.Core.Canonicalization;

namespace Course.Tests.Integration;

// Ticket 07 acceptance: finish_job re-checks owner, lease version, job state
// and outcome before it changes anything; a matching finish closes the
// attempt and the step, appends history, merges the result under the step's
// own key and lets the base -- not the caller -- pick the transition,
// covering all three destination shapes (a new job, a signal/manual wait, a
// closed process). A stale owner or lease version, and an outcome with no
// declared transition, are rejected with the declared code and leave every
// row untouched. flow test-finish (IWorkflowJobRuntime.FinishAsync over the
// workflow_worker connection) is the same production boundary, exercised
// here exactly as JobsLeaseClaimTests exercises claim_jobs.
[Collection("integration")]
public sealed class FinishJobGraphTestFinishTests
{
    private readonly ApiFactory factory;

    public FinishJobGraphTestFinishTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Finish_MatchingOwnerAndLease_ClosesAttemptAndStep_AndAppendsHistory()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);

        var claimed = await ClaimAsync();
        var job = claimed["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var result = new JsonObject { ["echo"] = "value" };
        var finish = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, LastClaimOwner, job["leaseVersion"]!.GetValue<int>(), "ROUTE_SIGNAL_72125C4E", result, CancellationToken.None);

        Assert.Equal("ok", finish["status"]!.GetValue<string>());

        var attemptRow = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, result::text from workflow.workflow_attempt where attempt_id = @attemptId", connection);
            command.Parameters.AddWithValue("attemptId", Guid.Parse(job["attemptId"]!.GetValue<string>()));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), Result: reader.GetString(1));
        });
        Assert.Equal("SUCCEEDED", attemptRow.State);
        Assert.Equal("value", JsonNode.Parse(attemptRow.Result)!["echo"]!.GetValue<string>());

        var stepRow = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, outcome from workflow.step_instance where process_id = @processId and step_key = 'invoke-ea531c02'",
                connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), Outcome: reader.GetString(1));
        });
        Assert.Equal("COMPLETED", stepRow.State);
        Assert.Equal("ROUTE_SIGNAL_72125C4E", stepRow.Outcome);

        var jobState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where job_id = @jobId", ("jobId", jobId));
        Assert.Equal("SUCCEEDED", jobState);

        var eventTypes = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select event_type from workflow.workflow_event where process_id = @processId order by occurred_at",
                connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            var types = new List<string>();
            while (await reader.ReadAsync())
            {
                types.Add(reader.GetString(0));
            }

            return types;
        });
        Assert.Contains("AttemptSucceeded", eventTypes);
        Assert.Contains("StepCompleted", eventTypes);

        var processData = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select process_data::text from workflow.process_instance where process_id = @processId",
            ("processId", processId));
        var parsedProcessData = JsonNode.Parse(processData)!;
        Assert.Equal("value", parsedProcessData["invoke-ea531c02"]!["echo"]!.GetValue<string>());
        Assert.Null(parsedProcessData["echo"]);
    }

    [Fact]
    public async Task Finish_RouteSignalOutcome_MovesProcessToWaitingSignal()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;

        await FinishClaimedJobAsync(job, "ROUTE_SIGNAL_72125C4E");

        var state = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("WAITING_SIGNAL", state);
    }

    [Fact]
    public async Task Finish_RouteManualOutcome_MovesProcessToWaitingManual()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;

        await FinishClaimedJobAsync(job, "ROUTE_MANUAL_503E0EA7");

        var state = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("WAITING_MANUAL", state);
    }

    [Fact]
    public async Task Finish_TransitionToAutomaticStep_CreatesTheNextJob()
    {
        var flowName = NewFlowName();
        await PublishAndActivateAutomaticChainFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;

        await FinishClaimedJobAsync(job, "TO_NEXT");

        var nextJob = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state from workflow.workflow_job where process_id = @processId and state = 'READY'", connection);
            command.Parameters.AddWithValue("processId", processId);
            return await command.ExecuteScalarAsync();
        });
        Assert.Equal("READY", nextJob);
    }

    [Fact]
    public async Task Finish_TransitionToEndStep_ClosesTheProcess_WithTheEndStepsDeclaredOutcome()
    {
        var flowName = NewFlowName();
        await PublishAndActivateAutomaticChainFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);

        var firstJob = (await ClaimAsync())["job"]!;
        await FinishClaimedJobAsync(firstJob, "TO_NEXT");

        var secondJob = (await ClaimAsync())["job"]!;
        await FinishClaimedJobAsync(secondJob, "TO_END");

        var process = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, current_step_key from workflow.process_instance where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), CurrentStepKey: reader.GetString(1));
        });
        Assert.Equal("COMPLETED", process.State);
        Assert.Equal("close", process.CurrentStepKey);

        var closeOutcome = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select outcome from workflow.step_instance where process_id = @processId and step_key = 'close'",
            ("processId", processId));
        Assert.Equal("CLOSED_AUTO", closeOutcome);
    }

    [Fact]
    public async Task Finish_WrongOwner_IsRejectedAsStale_AndChangesNothing()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var finish = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, "someone-else", job["leaseVersion"]!.GetValue<int>(), "ROUTE_SIGNAL_72125C4E", new JsonObject(), CancellationToken.None);

        Assert.Equal("error", finish["status"]!.GetValue<string>());
        Assert.Equal("workflow.lease_stale", finish["code"]!.GetValue<string>());

        await AssertJobUntouchedAsync(jobId, "LEASED");
        await AssertProcessUnchangedAsync(processId, "RUNNING");
    }

    [Fact]
    public async Task Finish_StaleLeaseVersion_IsRejected_AndChangesNothing()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var finish = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, LastClaimOwner, job["leaseVersion"]!.GetValue<int>() + 1, "ROUTE_SIGNAL_72125C4E", new JsonObject(), CancellationToken.None);

        Assert.Equal("error", finish["status"]!.GetValue<string>());
        Assert.Equal("workflow.lease_stale", finish["code"]!.GetValue<string>());

        await AssertJobUntouchedAsync(jobId, "LEASED");
        await AssertProcessUnchangedAsync(processId, "RUNNING");
    }

    [Fact]
    public async Task Finish_AlreadyDoneJob_IsRejectedAsStale()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());
        var leaseVersion = job["leaseVersion"]!.GetValue<int>();
        var owner = LastClaimOwner;

        var first = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, owner, leaseVersion, "ROUTE_SIGNAL_72125C4E", new JsonObject(), CancellationToken.None);
        Assert.Equal("ok", first["status"]!.GetValue<string>());

        var second = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, owner, leaseVersion, "ROUTE_SIGNAL_72125C4E", new JsonObject(), CancellationToken.None);

        Assert.Equal("error", second["status"]!.GetValue<string>());
        Assert.Equal("workflow.lease_stale", second["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finish_UndeclaredOutcome_IsRejected_AndChangesNothing()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        var processId = await StartProcessAsync(flowName);
        var job = (await ClaimAsync())["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        var finish = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, LastClaimOwner, job["leaseVersion"]!.GetValue<int>(), "NO_SUCH_OUTCOME", new JsonObject(), CancellationToken.None);

        Assert.Equal("error", finish["status"]!.GetValue<string>());
        Assert.Equal("workflow.unknown_outcome", finish["code"]!.GetValue<string>());

        await AssertJobUntouchedAsync(jobId, "LEASED");
        await AssertProcessUnchangedAsync(processId, "RUNNING");
    }

    [Fact]
    public async Task WorkflowWorkerRole_CanCallFinishJob_ThroughItsOwnConnection()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        await StartProcessAsync(flowName);

        await using var connection = new NpgsqlConnection(factory.Postgres.WorkerConnectionString);
        await connection.OpenAsync();

        await using var claim = new NpgsqlCommand("select workflow.claim_jobs(@owner, 5000)", connection);
        claim.Parameters.AddWithValue("owner", NewOwner());
        var claimedRaw = (string)(await claim.ExecuteScalarAsync())!;
        var claimed = JsonNode.Parse(claimedRaw)!;
        var job = claimed["job"]!;

        await using var finish = new NpgsqlCommand(
            "select workflow.finish_job(@jobId, @owner, @leaseVersion, @outcome, @result)", connection);
        finish.Parameters.AddWithValue("jobId", Guid.Parse(job["jobId"]!.GetValue<string>()));
        finish.Parameters.AddWithValue("owner", claim.Parameters["owner"].Value!);
        finish.Parameters.AddWithValue("leaseVersion", job["leaseVersion"]!.GetValue<int>());
        finish.Parameters.AddWithValue("outcome", "ROUTE_SIGNAL_72125C4E");
        finish.Parameters.Add(new NpgsqlParameter("result", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = "{}" });

        var finishedRaw = (string)(await finish.ExecuteScalarAsync())!;
        Assert.Contains("\"status\":\"ok\"", finishedRaw.Replace(" ", ""));
    }

    // Week 4, ticket 03: `flow test-finish` used to connect as workflow_worker, and
    // the published secret-distribution rule allows that role's password in
    // postgres, worker-a and worker-b only -- so cli lost it in ticket 02 and the
    // probe lost its connection. What the probe actually needs is a call to the
    // production finish boundary, not that particular role, so it connects as
    // course_publisher with an explicit grant on that one function. Both outcomes
    // the probe exists to show are proven here through that role.
    [Fact]
    public async Task PublisherRole_CanCallFinishJob_ForTheFencingProbe()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        await StartProcessAsync(flowName);

        await using var worker = new NpgsqlConnection(factory.Postgres.WorkerConnectionString);
        await worker.OpenAsync();

        var owner = NewOwner();
        await using var claim = new NpgsqlCommand("select workflow.claim_jobs(@owner, 5000)", worker);
        claim.Parameters.AddWithValue("owner", owner);
        var claimed = JsonNode.Parse((string)(await claim.ExecuteScalarAsync())!)!;
        var job = claimed["job"]!;

        await using var publisher = new NpgsqlConnection(factory.Postgres.PublisherConnectionString);
        await publisher.OpenAsync();

        var finished = await FinishAsync(
            publisher,
            Guid.Parse(job["jobId"]!.GetValue<string>()),
            owner,
            job["leaseVersion"]!.GetValue<int>());

        Assert.Contains("\"status\":\"ok\"", finished.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublisherRole_StaleFinishIsStillRejected_ForTheFencingProbe()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await DrainClaimableJobsAsync();
        await StartProcessAsync(flowName);

        await using var worker = new NpgsqlConnection(factory.Postgres.WorkerConnectionString);
        await worker.OpenAsync();

        var owner = NewOwner();
        await using var claim = new NpgsqlCommand("select workflow.claim_jobs(@owner, 5000)", worker);
        claim.Parameters.AddWithValue("owner", owner);
        var claimed = JsonNode.Parse((string)(await claim.ExecuteScalarAsync())!)!;
        var job = claimed["job"]!;
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());

        await using var publisher = new NpgsqlConnection(factory.Postgres.PublisherConnectionString);
        await publisher.OpenAsync();

        // One lease version behind: the owner came back after losing the lease.
        var stale = await FinishAsync(publisher, jobId, owner, job["leaseVersion"]!.GetValue<int>() - 1);

        Assert.Contains("workflow.lease_stale", stale, StringComparison.Ordinal);
        await AssertJobUntouchedAsync(jobId, "LEASED");
    }

    private static async Task<string> FinishAsync(
        NpgsqlConnection connection, Guid jobId, string owner, int leaseVersion)
    {
        await using var finish = new NpgsqlCommand(
            "select workflow.finish_job(@jobId, @owner, @leaseVersion, @outcome, @result)", connection);
        finish.Parameters.AddWithValue("jobId", jobId);
        finish.Parameters.AddWithValue("owner", owner);
        finish.Parameters.AddWithValue("leaseVersion", leaseVersion);
        finish.Parameters.AddWithValue("outcome", "ROUTE_SIGNAL_72125C4E");
        finish.Parameters.Add(new NpgsqlParameter("result", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = "{}" });

        return (string)(await finish.ExecuteScalarAsync())!;
    }

    private string LastClaimOwner { get; set; } = "";

    // The container (and its workflow_job table) is shared by every test in
    // this class, one at a time (assembly-wide parallelization is off, see
    // PostgresFixture). A test earlier in the run can leave a job READY
    // without ever finishing it -- so any scenario that needs to claim
    // exactly its own just-started process's job drains the queue first, out
    // of the way on a lease long enough to outlive the test (mirrors
    // JobsLeaseClaimTests).
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

    private async Task<JsonNode> ClaimAsync()
    {
        var owner = NewOwner();
        var raw = await factory.Postgres.JobRuntime.ClaimAsync(owner, leaseMs: 30_000, CancellationToken.None);
        LastClaimOwner = owner;
        return raw;
    }

    private Task FinishClaimedJobAsync(JsonNode job, string outcome) =>
        FinishClaimedJobAsync(job, outcome, new JsonObject());

    private async Task FinishClaimedJobAsync(JsonNode job, string outcome, JsonObject result)
    {
        var jobId = Guid.Parse(job["jobId"]!.GetValue<string>());
        var leaseVersion = job["leaseVersion"]!.GetValue<int>();
        var finish = await factory.Postgres.JobRuntime.FinishAsync(
            jobId, LastClaimOwner, leaseVersion, outcome, result, CancellationToken.None);
        Assert.Equal("ok", finish["status"]!.GetValue<string>());
    }

    private async Task AssertJobUntouchedAsync(Guid jobId, string expectedState)
    {
        var state = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where job_id = @jobId", ("jobId", jobId));
        Assert.Equal(expectedState, state);
    }

    private async Task AssertProcessUnchangedAsync(Guid processId, string expectedState)
    {
        var state = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal(expectedState, state);
    }

    private static string NewFlowName() => $"probe-finish-flow-{Guid.NewGuid():N}";

    private static string NewBusinessKey() => $"business-{Guid.NewGuid():N}";

    private static string NewOwner() => $"owner-{Guid.NewGuid():N}";

    private static string FixturePath(string fileName) =>
        RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "maps", fileName);

    private static string ManifestPath(string fileName) =>
        RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "manifests", fileName);

    // Reuses the same open-check probe action/map pair JobsLeaseClaimTests
    // publishes: its automatic step already carries two declared outcomes
    // routing to a WAIT_SIGNAL and a MANUAL step, which is exactly the shape
    // this ticket's non-automatic transition scenarios need.
    private async Task PublishAndActivateProbeFlowAsync(string flowName)
    {
        await PublishProbeActionAsync();

        var document = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("3c363d117b40-v1.flow.json")))!;
        document["flow_name"] = flowName;

        var publishedFlow = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (publishedFlow["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish probe flow: {publishedFlow.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate probe flow: {activated.ToJsonString()}");
        }
    }

    private async Task PublishProbeActionAsync()
    {
        var manifestDocument = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath("3c363d117b40.action.json")))!;
        var manifest = ActionManifest.FromDocument(manifestDocument);
        var publishedAction = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
        if (publishedAction["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish probe action: {publishedAction.ToJsonString()}");
        }
    }

    // Bypasses workflow.publish_flow's C# schema/semantic validator on purpose
    // (same reasoning as ProcessManualEndAndGetTests.InsertEndOnlyFlowVersionAsync):
    // course-1 map validation is a C# concern this ticket does not own, and
    // finish_job needs a two-automatic-step-then-end chain no fixture provides,
    // to prove a job-to-job and a job-to-end transition independently. Both
    // automatic steps reuse the already-published probe action -- finish_job
    // never calls it, so its outcomes are irrelevant here.
    private async Task PublishAndActivateAutomaticChainFlowAsync(string flowName)
    {
        await PublishProbeActionAsync();

        var document = JsonNode.Parse($$"""
            {
              "flow_name": "{{flowName}}",
              "version": 1,
              "start_step": "first",
              "steps": [
                {
                  "key": "first",
                  "type": "automatic",
                  "task": {
                    "module": "module_5abe942c84",
                    "action": "action_5cd90325f0",
                    "action_version": 1,
                    "required_policy": ["workflow:execute"],
                    "timeout_ms": 1000,
                    "retry": {"max_attempts": 1, "delays_ms": []},
                    "input_mapping": {},
                    "input_constants": {
                      "marker_0b52d25a": "m", "mode_d566e409": "signal_aecd1de", "value_c6e4ac8e": "v"
                    }
                  }
                },
                {
                  "key": "second",
                  "type": "automatic",
                  "task": {
                    "module": "module_5abe942c84",
                    "action": "action_5cd90325f0",
                    "action_version": 1,
                    "required_policy": ["workflow:execute"],
                    "timeout_ms": 1000,
                    "retry": {"max_attempts": 1, "delays_ms": []},
                    "input_mapping": {},
                    "input_constants": {
                      "marker_0b52d25a": "m", "mode_d566e409": "signal_aecd1de", "value_c6e4ac8e": "v"
                    }
                  }
                },
                {"key": "close", "type": "end", "outcome": "CLOSED_AUTO"}
              ],
              "transitions": [
                {"from": "first", "outcome": "TO_NEXT", "to": "second"},
                {"from": "second", "outcome": "TO_END", "to": "close"}
              ]
            }
            """)!;

        var published = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (published["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish automatic-chain flow: {published.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate automatic-chain flow: {activated.ToJsonString()}");
        }
    }

    private async Task<Guid> StartProcessAsync(string flowName)
    {
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start probe process: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }
}
