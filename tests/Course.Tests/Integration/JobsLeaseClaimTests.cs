using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Tests.Support;
using Npgsql;
using Workflow.Core.Canonicalization;

namespace Course.Tests.Integration;

// Ticket 06 acceptance: entering an AUTOMATIC step opens a job in READY state
// with its own execution id and a JobScheduled event; claim_job hands out one
// job per call, picks up ready/due-deferred/expired-leased rows and nothing
// else, never gives the same row to two concurrent callers, and a reclaim
// after lease expiry keeps the job id and execution id while opening a new
// attempt and marking the old one stale. No worker program exists yet -- every
// scenario here drives workflow.claim_job directly, exactly as the ticket
// describes.
[Collection("integration")]
public sealed class JobsLeaseClaimTests
{
    private static readonly string[] ExpectedOutcomes = { "ROUTE_SIGNAL_72125C4E", "ROUTE_MANUAL_503E0EA7" };
    private static readonly string[] ExpectedRequiredPolicy = { "workflow:execute" };
    private static readonly int[] ExpectedDelaysMs = { 1000, 2000 };

    private readonly ApiFactory factory;

    public JobsLeaseClaimTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task EnteringAutomaticStep_CreatesAReadyJob_WithAnExecutionId_AndAHistoryEvent()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);

        var processId = await StartProcessAsync(flowName);

        var job = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, execution_id from workflow.workflow_job where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), ExecutionId: reader.GetString(1));
        });

        Assert.Equal("READY", job.State);
        Assert.False(string.IsNullOrWhiteSpace(job.ExecutionId));

        var eventTypes = await factory.Postgres.QueryAsOwnerAsync(async connection =>
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
        Assert.Contains("JobScheduled", eventTypes);
    }

    [Fact]
    public async Task Claim_NoReadyJob_ReturnsAnEmptyJob()
    {
        await DrainClaimableJobsAsync();

        var raw = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 2000, CancellationToken.None);

        Assert.Equal("ok", raw["status"]!.GetValue<string>());
        Assert.True(IsEmpty(raw));
    }

    [Fact]
    public async Task Claim_ReadyJob_ReturnsTheFullDocument()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);

        var raw = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);

        Assert.Equal("ok", raw["status"]!.GetValue<string>());
        var job = raw["job"]!;

        Assert.False(string.IsNullOrWhiteSpace(job["jobId"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(job["executionId"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(job["attemptId"]!.GetValue<string>()));
        Assert.Equal(1, job["leaseVersion"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(job["leaseExpiresAt"]!.GetValue<string>()));

        Assert.Equal(processId.ToString(), job["process"]!["processId"]!.GetValue<string>());
        Assert.NotNull(job["process"]!["data"]);

        var task = job["task"]!;
        Assert.Equal("module_5abe942c84", task["module"]!.GetValue<string>());
        Assert.Equal("action_5cd90325f0", task["action"]!.GetValue<string>());
        Assert.Equal(1, task["actionVersion"]!.GetValue<int>());
        Assert.NotNull(task["requestSchema"]);
        Assert.NotNull(task["responseSchema"]);
        Assert.Equal(ExpectedOutcomes, task["outcomes"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Equal(ExpectedRequiredPolicy, task["requiredPolicy"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal(1600, task["timeoutMs"]!.GetValue<int>());
        Assert.Equal("marker_ad370346f0d7", task["inputConstants"]!["marker_0b52d25a"]!.GetValue<string>());
        Assert.Equal("/behavior_644ac23d", task["inputMapping"]!["/mode_d566e409"]!.GetValue<string>());
        Assert.Equal(3, task["retry"]!["maxAttempts"]!.GetValue<int>());
        Assert.Equal(ExpectedDelaysMs, task["retry"]!["delaysMs"]!.AsArray().Select(d => d!.GetValue<int>()));

        var stored = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_job where job_id = @jobId",
            ("jobId", Guid.Parse(job["jobId"]!.GetValue<string>())));
        Assert.Equal("LEASED", stored);
    }

    [Fact]
    public async Task Claim_TwoConcurrentCallers_NeverReceiveTheSameJob()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await StartProcessAsync(flowName);

        var first = factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);
        var second = factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        var claimed = results.Where(r => !IsEmpty(r)).ToList();
        Assert.Single(claimed);
    }

    [Fact]
    public async Task Claim_WhileLeaseIsAlive_TheSecondCallerGetsNothing()
    {
        await DrainClaimableJobsAsync();
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await StartProcessAsync(flowName);

        var firstClaim = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);
        Assert.NotNull(firstClaim["job"]);

        var secondClaim = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);
        Assert.True(IsEmpty(secondClaim));
    }

    [Fact]
    public async Task Claim_AfterLeaseExpires_Reclaims_SameJobAndExecutionId_NewAttempt_OldAttemptStale()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await StartProcessAsync(flowName);

        var first = await factory.Postgres.JobRuntime.ClaimAsync("owner-a", leaseMs: 100, CancellationToken.None);
        var firstJob = first["job"]!;
        await Task.Delay(400);

        var second = await factory.Postgres.JobRuntime.ClaimAsync("owner-b", leaseMs: 5000, CancellationToken.None);
        var secondJob = second["job"];
        Assert.NotNull(secondJob);

        Assert.Equal(firstJob["jobId"]!.GetValue<string>(), secondJob!["jobId"]!.GetValue<string>());
        Assert.Equal(firstJob["executionId"]!.GetValue<string>(), secondJob["executionId"]!.GetValue<string>());
        Assert.NotEqual(firstJob["attemptId"]!.GetValue<string>(), secondJob["attemptId"]!.GetValue<string>());
        Assert.Equal(2, secondJob["leaseVersion"]!.GetValue<int>());

        var attemptCount = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select attempt_count from workflow.workflow_job where job_id = @jobId",
            ("jobId", Guid.Parse(firstJob["jobId"]!.GetValue<string>())));
        Assert.Equal(2, attemptCount);

        var staleState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.workflow_attempt where attempt_id = @attemptId",
            ("attemptId", Guid.Parse(firstJob["attemptId"]!.GetValue<string>())));
        Assert.Equal("STALE", staleState);
    }

    [Fact]
    public async Task Claim_DueDeferredJob_IsPickedUp()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await StartProcessAsync(flowName);

        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "update workflow.workflow_job set state = 'DEFERRED', next_attempt_at = now() - interval '1 second'",
                connection);
            return await command.ExecuteNonQueryAsync();
        });

        var raw = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);

        Assert.NotNull(raw["job"]);
    }

    [Fact]
    public async Task WorkflowWorkerRole_CanConnectAndClaim_ButHasNoDirectTableAccess()
    {
        await using var connection = new NpgsqlConnection(factory.Postgres.WorkerConnectionString);
        await connection.OpenAsync();

        await using var claim = new NpgsqlCommand("select workflow.claim_job('direct-connection-owner', 2000)", connection);
        var raw = (string)(await claim.ExecuteScalarAsync())!;
        Assert.Contains("\"status\"", raw);

        await using var directSelect = new NpgsqlCommand("select 1 from workflow.workflow_job limit 1", connection);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => directSelect.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task AutocheckJobsAndAttemptsViews_ExposeTheDeclaredColumns_ThroughCourseRuntime()
    {
        var flowName = NewFlowName();
        await PublishAndActivateProbeFlowAsync(flowName);
        await StartProcessAsync(flowName);
        var claimed = await factory.Postgres.JobRuntime.ClaimAsync(NewOwner(), leaseMs: 5000, CancellationToken.None);
        var jobId = Guid.Parse(claimed["job"]!["jobId"]!.GetValue<string>());

        var jobRow = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select job_id, process_id, step_instance_id, execution_id, state, owner, "
                + "lease_version, lease_expires_at, attempt_count, next_attempt_at, created_at, updated_at "
                + "from autocheck.jobs where job_id = @jobId", connection);
            command.Parameters.AddWithValue("jobId", jobId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return reader.GetString(4);
        });
        Assert.Equal("LEASED", jobRow);

        var attemptRow = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select attempt_id, job_id, lease_version, attempt_number, state, outcome, error_code, started_at, finished_at "
                + "from autocheck.attempts where job_id = @jobId", connection);
            command.Parameters.AddWithValue("jobId", jobId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return reader.GetString(4);
        });
        Assert.Equal("LEASED", attemptRow);
    }

    // The container (and its workflow_job table) is shared by every test in
    // this class, one at a time (assembly-wide parallelization is off, see
    // PostgresFixture). A test earlier in the run can leave its job READY
    // without ever claiming it (that is what it means to test job creation in
    // isolation) -- so any scenario that needs an exact claimable count starts
    // by claiming everything already sitting in the queue, out of the way on
    // a lease long enough to outlive the test.
    private async Task DrainClaimableJobsAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            var raw = await factory.Postgres.JobRuntime.ClaimAsync("drain", leaseMs: 60_000, CancellationToken.None);
            if (IsEmpty(raw))
            {
                return;
            }
        }

        throw new InvalidOperationException("queue did not drain within 100 claims");
    }

    // "job": null round-trips through JsonNode as an explicit JsonValueKind.Null
    // node, not a C# null -- both count as empty here.
    private static bool IsEmpty(JsonNode claimResult) =>
        claimResult["job"] is null || claimResult["job"]!.GetValueKind() == JsonValueKind.Null;

    private static string NewFlowName() => $"probe-jobs-flow-{Guid.NewGuid():N}";

    private static string NewBusinessKey() => $"business-{Guid.NewGuid():N}";

    private static string NewOwner() => $"owner-{Guid.NewGuid():N}";

    private static string FixturePath(string fileName) =>
        RepoPaths.Combine("autocheck", "fixtures", "maps", fileName);

    private static string ManifestPath(string fileName) =>
        RepoPaths.Combine("autocheck", "fixtures", "manifests", fileName);

    // The autocheck fixture map/action pair (3c363d117b40): its first step is
    // AUTOMATIC, which is exactly the shape this ticket needs and nothing this
    // ticket owns builds one of its own (that is ticket 05's probe action).
    private async Task PublishAndActivateProbeFlowAsync(string flowName)
    {
        var manifestDocument = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath("3c363d117b40.action.json")))!;
        var manifest = ActionManifest.FromDocument(manifestDocument);
        var publishedAction = await factory.Postgres.Publisher.PublishAsync(manifest, CancellationToken.None);
        if (publishedAction["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish probe action: {publishedAction.ToJsonString()}");
        }

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
