using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Canonicalization;
using Workflow.Core.Runtime;
using Workflow.Core.Security;
using Workflow.Worker;

namespace Course.Tests.Integration;

// Ticket 03 acceptance: payment.validate, payment.check_limit, payment.approve
// and payment.reject are published, activated, worker-invokable and audited.
// No map exists yet (ticket 04), so each test wires a throwaway automatic-step
// flow -- same pattern WorkerLoopTests uses for workflow.probe -- rather than
// calling the target functions over plain HTTP: the LIMIT_RULE decision genuinely
// needs a real process/step instance, which only a claimed job's context carries.
[Collection("integration")]
public sealed class PaymentReviewActionsTests
{
    private readonly ApiFactory factory;

    public PaymentReviewActionsTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("50000.00")]
    [InlineData("100000.00")] // exact boundary: inclusive WITHIN_LIMIT
    public async Task AtOrBelowThreshold_AutoApproves_RecordsLimitRuleDecision_AndCompletesOperation(string amount)
    {
        var operationId = await InsertOperationAsync(amount);
        var flowName = NewFlowName();
        await PublishAndActivateAsync(flowName, ValidateCheckLimitApproveFlow(flowName));
        var processId = await StartProcessAsync(flowName, operationId);
        var loop = CreateLoop();

        var validate = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, validate.Outcome);

        var checkLimit = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, checkLimit.Outcome);

        var approve = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, approve.Outcome);

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("COMPLETED", status);

        var decision = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select source, principal, outcome, rule_version, reason_hash from autocheck.decisions where process_id = @processId",
                connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                Source: reader.GetString(0),
                Principal: reader.GetString(1),
                Outcome: reader.GetString(2),
                RuleVersion: reader.IsDBNull(3) ? null : reader.GetString(3),
                ReasonHash: reader.IsDBNull(4) ? null : reader.GetString(4));
        });

        Assert.Equal("LIMIT_RULE", decision.Source);
        Assert.Equal(WorkflowWorkerPrincipal.Name, decision.Principal);
        Assert.Equal("APPROVED", decision.Outcome);
        Assert.Equal("course-limit-v1", decision.RuleVersion);
        Assert.Null(decision.ReasonHash);
    }

    [Fact]
    public async Task AboveThreshold_RoutesToReviewRequired_AndRecordsNoDecision()
    {
        var operationId = await InsertOperationAsync("100000.01");
        var flowName = NewFlowName();
        await PublishAndActivateAsync(flowName, ValidateCheckLimitToManualFlow(flowName));
        var processId = await StartProcessAsync(flowName, operationId);
        var loop = CreateLoop();

        var validate = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, validate.Outcome);

        var checkLimit = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, checkLimit.Outcome);

        var processState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from workflow.process_instance where process_id = @processId", ("processId", processId));
        Assert.Equal("WAITING_MANUAL", processState);

        var decisionCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.decisions where process_id = @processId", ("processId", processId));
        Assert.Equal(0, decisionCount);

        // Operation status is untouched by check_limit itself -- only approve/
        // reject ever move it off PROCESSING.
        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("PROCESSING", status);
    }

    [Fact]
    public async Task Reject_MovesOperationToRejectedStatus()
    {
        var operationId = await InsertOperationAsync("30000.00");
        var flowName = NewFlowName();
        await PublishAndActivateAsync(flowName, RejectOnlyFlow(flowName));
        await StartProcessAsync(flowName, operationId);
        var loop = CreateLoop();

        var reject = await loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(WorkerStepOutcome.Succeeded, reject.Outcome);

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("REJECTED", status);
    }

    [Fact]
    public async Task Validate_UnknownOperationId_ReturnsControlledNotFound()
    {
        var result = await CallTargetDirectlyAsync("payment", "validate_v1", new JsonObject { ["operationId"] = Guid.NewGuid().ToString() });

        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal("operation.not_found", result["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckLimit_PayloadRejectsClientSuppliedRuleFields()
    {
        var operationId = await InsertOperationAsync("50000.00");
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("workflow-worker-test", "internal", "payment:internal");

        var response = await client.PostActionAsync(
            "/api/payment/check_limit",
            token,
            new JsonObject { ["operationId"] = operationId.ToString(), ["ruleVersion"] = "client-supplied-v9" },
            idempotencyKey: $"check-limit-{Guid.NewGuid():N}");

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("payload.invalid", response.Body["code"]!.GetValue<string>());
    }

    private async Task<JsonNode> CallTargetDirectlyAsync(string schema, string function, JsonObject payload) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand($"select {schema}.{function}(@context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["requestId"] = Guid.NewGuid().ToString(), ["principal"] = "test-direct-call" }.ToJsonString(),
            });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.ToJsonString() });

            return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
        });

    private async Task<Guid> InsertOperationAsync(string amount) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.operations (request_id, operation_kind, amount, currency, status) "
                + "values (@requestId, 'PAYMENT_APPROVAL', @amount, 'RUB', 'PROCESSING') returning operation_id",
                connection);
            command.Parameters.AddWithValue("requestId", $"req-{Guid.NewGuid():N}");
            command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric)
            {
                Value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            });
            return (Guid)(await command.ExecuteScalarAsync())!;
        });

    private WorkerLoop CreateLoop()
    {
        var dataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            $"owner-{Guid.NewGuid():N}", dataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    private static string NewFlowName() => $"payment-review-actions-{Guid.NewGuid():N}";

    private static string ValidateTask(string inputMapping) => $$"""
        {
          "module": "payment",
          "action": "validate",
          "action_version": 1,
          "required_policy": ["payment:internal"],
          "timeout_ms": 5000,
          "retry": {"max_attempts": 1, "delays_ms": []},
          "input_mapping": {{inputMapping}},
          "input_constants": {}
        }
        """;

    private static string CheckLimitTask() => """
        {
          "module": "payment",
          "action": "check_limit",
          "action_version": 1,
          "required_policy": ["payment:internal"],
          "timeout_ms": 5000,
          "retry": {"max_attempts": 1, "delays_ms": []},
          "input_mapping": {"/operationId": "/operationId"},
          "input_constants": {}
        }
        """;

    private static string ApproveTask() => """
        {
          "module": "payment",
          "action": "approve",
          "action_version": 1,
          "required_policy": ["payment:internal"],
          "timeout_ms": 5000,
          "retry": {"max_attempts": 1, "delays_ms": []},
          "input_mapping": {"/operationId": "/operationId"},
          "input_constants": {}
        }
        """;

    private static string RejectTask(string inputMapping) => $$"""
        {
          "module": "payment",
          "action": "reject",
          "action_version": 1,
          "required_policy": ["payment:internal"],
          "timeout_ms": 5000,
          "retry": {"max_attempts": 1, "delays_ms": []},
          "input_mapping": {{inputMapping}},
          "input_constants": {}
        }
        """;

    // validate -> check_limit -> [WITHIN_LIMIT] approve -> end
    private static JsonNode ValidateCheckLimitApproveFlow(string flowName) => JsonNode.Parse($$"""
        {
          "flow_name": "{{flowName}}",
          "version": 1,
          "start_step": "validate",
          "steps": [
            {"key": "validate", "type": "automatic", "task": {{ValidateTask("""{"/operationId": "/operationId"}""")}}},
            {"key": "check_limit", "type": "automatic", "task": {{CheckLimitTask()}}},
            {"key": "approve", "type": "automatic", "task": {{ApproveTask()}}},
            {"key": "end", "type": "end", "outcome": "DONE"}
          ],
          "transitions": [
            {"from": "validate", "outcome": "VALID", "to": "check_limit"},
            {"from": "check_limit", "outcome": "WITHIN_LIMIT", "to": "approve"},
            {"from": "check_limit", "outcome": "REVIEW_REQUIRED", "to": "end"},
            {"from": "approve", "outcome": "APPROVED", "to": "end"}
          ]
        }
        """)!;

    // validate -> check_limit -> [REVIEW_REQUIRED] manual (left unresolved --
    // ticket 05 is what finishes a manual step); [WITHIN_LIMIT] short-circuits
    // straight to end since this fixture never needs that branch to complete.
    private static JsonNode ValidateCheckLimitToManualFlow(string flowName) => JsonNode.Parse($$"""
        {
          "flow_name": "{{flowName}}",
          "version": 1,
          "start_step": "validate",
          "steps": [
            {"key": "validate", "type": "automatic", "task": {{ValidateTask("""{"/operationId": "/operationId"}""")}}},
            {"key": "check_limit", "type": "automatic", "task": {{CheckLimitTask()}}},
            {"key": "manual", "type": "manual", "allowed_outcomes": ["APPROVED"]},
            {"key": "end", "type": "end", "outcome": "DONE"}
          ],
          "transitions": [
            {"from": "validate", "outcome": "VALID", "to": "check_limit"},
            {"from": "check_limit", "outcome": "WITHIN_LIMIT", "to": "end"},
            {"from": "check_limit", "outcome": "REVIEW_REQUIRED", "to": "manual"},
            {"from": "manual", "outcome": "APPROVED", "to": "end"}
          ]
        }
        """)!;

    // A single automatic step, so Reject_MovesOperationToRejectedStatus can
    // exercise payment.reject without depending on check_limit's routing.
    private static JsonNode RejectOnlyFlow(string flowName) => JsonNode.Parse($$"""
        {
          "flow_name": "{{flowName}}",
          "version": 1,
          "start_step": "reject",
          "steps": [
            {"key": "reject", "type": "automatic", "task": {{RejectTask("""{"/operationId": "/operationId"}""")}}},
            {"key": "end", "type": "end", "outcome": "DONE"}
          ],
          "transitions": [
            {"from": "reject", "outcome": "REJECTED", "to": "end"}
          ]
        }
        """)!;

    private async Task PublishAndActivateAsync(string flowName, JsonNode document)
    {
        var published = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (published["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish {flowName}: {published.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate {flowName}: {activated.ToJsonString()}");
        }
    }

    private async Task<Guid> StartProcessAsync(string flowName, Guid operationId)
    {
        var startData = new JsonObject { ["operationId"] = operationId.ToString() };
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, $"business-{Guid.NewGuid():N}", startData, JsonCanonicalizer.Sha256Hex(startData), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start process for {flowName}: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }
}
