using System.Text.Json.Nodes;
using Workflow.Core.Canonicalization;

namespace Course.Tests.Integration;

// Ticket 04 acceptance: start is idempotent by (flow_name, business_key) via
// canonical start-data hash and conflicts on changed data (surviving an
// active-version switch); a manual step reaches a persisted WAITING_MANUAL
// and never advances on its own this ticket; an end step closes the process
// with its declared outcome; the compact get and the workflow.get network
// action both surface version/state/current step, and the latter carries the
// full steps/jobs/attempts picture behind its own read policy.
[Collection("integration")]
public sealed class ProcessManualEndAndGetTests
{
    private readonly ApiFactory factory;

    public ProcessManualEndAndGetTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Start_NoActiveVersion_ReturnsControlledError()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);

        var raw = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);

        Assert.Equal("error", raw["status"]!.GetValue<string>());
        Assert.Equal("flow.no_active_version", raw["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_ManualThenEnd_ReachesWaitingManual_AndIsVisibleThroughAutocheck()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        var businessKey = NewBusinessKey();

        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, businessKey, new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);

        Assert.Equal("ok", started["status"]!.GetValue<string>());
        Assert.Equal("WAITING_MANUAL", started["state"]!.GetValue<string>());
        var processId = Guid.Parse(started["processId"]!.GetValue<string>());

        // "переживает пересоздание окружения" -- re-reading through the same
        // stable evidence seam the checker uses (not the runtime object this
        // test happens to be holding) is what that survival claim means here.
        var row = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "select state, current_step_key from autocheck.processes where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), CurrentStepKey: reader.GetString(1));
        });

        Assert.Equal("WAITING_MANUAL", row.State);
        Assert.Equal("review", row.CurrentStepKey);
    }

    [Fact]
    public async Task Start_SameBusinessKeyAndData_ReturnsThePriorProcess()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        var businessKey = NewBusinessKey();
        var data = new JsonObject { ["applicant"] = "same" };
        var hash = JsonCanonicalizer.Sha256Hex(data);

        var first = await factory.Postgres.ProcessRuntime.StartAsync(flowName, businessKey, data, hash, CancellationToken.None);
        var second = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, businessKey, new JsonObject { ["applicant"] = "same" }, hash, CancellationToken.None);

        Assert.Equal("ok", second["status"]!.GetValue<string>());
        Assert.Equal(first["processId"]!.GetValue<string>(), second["processId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_SameBusinessKeyChangedData_ReturnsConflict_EvenAfterActiveVersionChanges()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await PublishManualThenEndAsync(flowName, version: 2);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        var businessKey = NewBusinessKey();

        var original = new JsonObject { ["applicant"] = "first" };
        await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, businessKey, original, JsonCanonicalizer.Sha256Hex(original), CancellationToken.None);

        // The conflict must hold even though a different version is now active.
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 2, CancellationToken.None);

        var changed = new JsonObject { ["applicant"] = "second" };
        var conflict = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, businessKey, changed, JsonCanonicalizer.Sha256Hex(changed), CancellationToken.None);

        Assert.Equal("error", conflict["status"]!.GetValue<string>());
        Assert.Equal("process.conflict", conflict["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_NoDataOption_StoresAnEmptyObject()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        var businessKey = NewBusinessKey();

        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, businessKey, new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);
        Assert.Equal("ok", started["status"]!.GetValue<string>());

        var storedStartData = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select start_data::text from workflow.process_instance where process_id = @processId",
            ("processId", Guid.Parse(started["processId"]!.GetValue<string>())));

        Assert.Equal(JsonNode.Parse(storedStartData), JsonNode.Parse("{}"));
    }

    // The only path this ticket has to reach an END step at all: no worker,
    // no signal delivery and no manual decision exist yet, and a course-1 map
    // whose start_step is itself type `end` cannot pass GraphRules (every
    // declared step must be reachable, and an end step can never have an
    // outgoing transition -- the schema's steps-minItems:2 then forces a
    // second, permanently unreachable step). That is a real contradiction
    // between the map schema and this scenario, not something this test
    // should paper over by inventing an unreachable step; it exercises
    // enter_step's END branch directly against a hand-inserted definition,
    // the same way FlowPublishListActivateTests goes straight at SQL to
    // prove a boundary independently of the C# validator.
    [Fact]
    public async Task Start_FlowWhoseStartStepIsEnd_ClosesImmediately_WithTheDeclaredOutcome()
    {
        var flowName = NewFlowName();
        await InsertEndOnlyFlowVersionAsync(flowName, version: 1);

        var businessKey = NewBusinessKey();
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, businessKey, new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);

        Assert.Equal("ok", started["status"]!.GetValue<string>());
        Assert.Equal("COMPLETED", started["state"]!.GetValue<string>());
        var processId = Guid.Parse(started["processId"]!.GetValue<string>());

        var outcome = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select outcome from workflow.step_instance where process_id = @processId",
            ("processId", processId));
        Assert.Equal("CLOSED", outcome);

        var currentStepKey = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select current_step_key from workflow.process_instance where process_id = @processId",
            ("processId", processId));
        Assert.Equal("close", currentStepKey);
    }

    [Fact]
    public async Task GetCompact_UnknownProcessId_ReturnsControlledError()
    {
        var raw = await factory.Postgres.ProcessRuntime.GetCompactAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("error", raw["status"]!.GetValue<string>());
        Assert.Equal("process.not_found", raw["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetCompact_ReturnsVersionStateAndCurrentStep()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);

        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);
        var processId = Guid.Parse(started["processId"]!.GetValue<string>());

        var raw = await factory.Postgres.ProcessRuntime.GetCompactAsync(processId, CancellationToken.None);

        Assert.Equal("ok", raw["status"]!.GetValue<string>());
        Assert.Equal(flowName, raw["flowName"]!.GetValue<string>());
        Assert.Equal(1, raw["flowVersion"]!.GetValue<int>());
        Assert.Equal("WAITING_MANUAL", raw["state"]!.GetValue<string>());
        Assert.Equal("review", raw["currentStepKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task NetworkGet_ReturnsProcessWithStepsJobsAndAttempts()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);

        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);
        var processId = started["processId"]!.GetValue<string>();

        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-worker", "worker", "workflow:read");

        var response = await client.PostActionAsync("/api/workflow/get", token, new JsonObject { ["processId"] = processId });

        Assert.Equal(200, response.StatusCode);
        var result = response.Body["result"]!;
        Assert.Equal(processId, result["process"]!["processId"]!.GetValue<string>());
        Assert.Equal("WAITING_MANUAL", result["process"]!["state"]!.GetValue<string>());
        Assert.Single(result["steps"]!.AsArray());
        Assert.Empty(result["jobs"]!.AsArray());
        Assert.Empty(result["attempts"]!.AsArray());
    }

    [Fact]
    public async Task NetworkGet_WithoutReadPolicy_IsDenied()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);

        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);

        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-worker", "worker", "workflow:execute");

        var response = await client.PostActionAsync(
            "/api/workflow/get", token, new JsonObject { ["processId"] = started["processId"]!.GetValue<string>() });

        Assert.NotEqual(200, response.StatusCode);
        Assert.Equal("access.denied", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task NetworkGet_UnknownProcessId_ReturnsControlledError_NotAServerFault()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-worker", "worker", "workflow:read");

        var response = await client.PostActionAsync(
            "/api/workflow/get", token, new JsonObject { ["processId"] = Guid.NewGuid().ToString() });

        Assert.NotEqual(200, response.StatusCode);
        Assert.Equal("process.not_found", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task AutocheckViews_ExposeTheDeclaredColumns_ThroughCourseRuntime()
    {
        var flowName = NewFlowName();
        await PublishManualThenEndAsync(flowName, version: 1);
        await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);

        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), new JsonObject(), JsonCanonicalizer.Sha256Hex(new JsonObject()), CancellationToken.None);
        var processId = Guid.Parse(started["processId"]!.GetValue<string>());

        var processRow = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "select process_id, business_key, flow_name, flow_version, state, current_step_key, created_at, updated_at "
                + "from autocheck.processes where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return reader.GetString(4);
        });
        Assert.Equal("WAITING_MANUAL", processRow);

        var eventTypes = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "select event_type from autocheck.workflow_events where process_id = @processId order by occurred_at", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            var types = new List<string>();
            while (await reader.ReadAsync())
            {
                types.Add(reader.GetString(0));
            }

            return types;
        });
        Assert.Equal(new List<string> { "ProcessStarted", "StepEntered" }, eventTypes);
    }

    private static string NewFlowName() => $"probe-manual-flow-{Guid.NewGuid():N}";

    private static string NewBusinessKey() => $"business-{Guid.NewGuid():N}";

    // Two steps -- manual then end -- is the smallest map course-1's schema
    // (steps minItems:2) allows; no automatic step means no probe action
    // dependency at all.
    private static JsonNode BuildManualThenEndDocument(string flowName, int version) => JsonNode.Parse($$"""
        {
          "contract_version": "course-1",
          "flow_name": "{{flowName}}",
          "version": {{version}},
          "start_step": "review",
          "steps": [
            {"key": "review", "type": "manual", "allowed_outcomes": ["APPROVED"]},
            {"key": "close", "type": "end", "outcome": "CLOSED"}
          ],
          "transitions": [
            {"from": "review", "outcome": "APPROVED", "to": "close"}
          ]
        }
        """)!;

    private Task<JsonNode> PublishManualThenEndAsync(string flowName, int version)
    {
        var document = BuildManualThenEndDocument(flowName, version);
        return factory.Postgres.FlowPublisher.PublishAsync(document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
    }

    // Bypasses workflow.publish_flow entirely -- see the scenario's own
    // comment above for why a schema-valid, start-step-is-end map cannot
    // exist to publish in the first place.
    private Task<int> InsertEndOnlyFlowVersionAsync(string flowName, int version) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var insertFlow = new Npgsql.NpgsqlCommand(
                "insert into workflow.flow_definition (flow_name) values (@flowName)", connection);
            insertFlow.Parameters.AddWithValue("flowName", flowName);
            await insertFlow.ExecuteNonQueryAsync();

            await using var insertVersion = new Npgsql.NpgsqlCommand(
                "insert into workflow.flow_version (flow_name, version, start_step, canonical_hash, is_active) "
                + "values (@flowName, @version, 'close', 'test-fixture-hash', true)", connection);
            insertVersion.Parameters.AddWithValue("flowName", flowName);
            insertVersion.Parameters.AddWithValue("version", version);
            await insertVersion.ExecuteNonQueryAsync();

            await using var insertStep = new Npgsql.NpgsqlCommand(
                "insert into workflow.step_definition (flow_name, version, step_key, step_type, end_outcome) "
                + "values (@flowName, @version, 'close', 'END', 'CLOSED')", connection);
            insertStep.Parameters.AddWithValue("flowName", flowName);
            insertStep.Parameters.AddWithValue("version", version);
            await insertStep.ExecuteNonQueryAsync();

            return 0;
        });
}
