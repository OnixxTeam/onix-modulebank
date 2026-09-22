using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Runtime;
using Workflow.Core.Security;
using Workflow.Worker;

namespace Course.Tests.Integration;

// Ticket 05 acceptance: workflow.manual completes the manual step opened by
// ticket 04's above-threshold branch -- decision, workflow event and the next
// automatic job all commit in one transaction, the principal comes from the
// verified context (never the payload, which has no field for it), and a
// changed-body replay and a decision on an already-decided step return two
// different, non-500 conflict codes.
[Collection("integration")]
public sealed class ManualDecisionTests
{
    private readonly ApiFactory factory;

    public ManualDecisionTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Approve_MovesOperationToCompleted_AndRecordsManualDecision()
    {
        var operationId = await InsertOperationAsync("150000.00");
        using var client = factory.CreateClient();
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());

        var loop = CreateLoop();
        await DrainAsync(loop); // validate, check_limit -> WAITING_MANUAL

        var stepInstanceId = await WaitingManualStepInstanceAsync(processId);

        var decisionResponse = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "Documents verified",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");

        Assert.Equal(200, decisionResponse.StatusCode);
        var result = decisionResponse.Body["result"]!;
        Assert.Equal("APPROVED", result["decision"]!.GetValue<string>());
        Assert.Equal("MANUAL", result["source"]!.GetValue<string>());
        Assert.Equal("reviewer", result["principal"]!.GetValue<string>());

        await DrainAsync(loop); // approve_operation

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("COMPLETED", status);

        var decision = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select source, principal, outcome, reason_hash, rule_version from autocheck.decisions where process_id = @processId",
                connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                Source: reader.GetString(0),
                Principal: reader.GetString(1),
                Outcome: reader.GetString(2),
                ReasonHash: reader.IsDBNull(3) ? null : reader.GetString(3),
                RuleVersion: reader.IsDBNull(4) ? null : reader.GetString(4));
        });

        Assert.Equal("MANUAL", decision.Source);
        Assert.Equal("reviewer", decision.Principal);
        Assert.Equal("APPROVED", decision.Outcome);
        Assert.NotNull(decision.ReasonHash);
        Assert.Null(decision.RuleVersion);
    }

    [Fact]
    public async Task Reject_MovesOperationToRejected()
    {
        var operationId = await InsertOperationAsync("150000.00");
        using var client = factory.CreateClient();
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());

        var loop = CreateLoop();
        await DrainAsync(loop); // validate, check_limit -> WAITING_MANUAL

        var stepInstanceId = await WaitingManualStepInstanceAsync(processId);

        var decisionResponse = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "REJECTED",
                ["reason"] = "Insufficient documentation",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");

        Assert.Equal(200, decisionResponse.StatusCode);

        await DrainAsync(loop); // reject_operation

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("REJECTED", status);
    }

    [Fact]
    public async Task Decide_SameKeySameBody_ReturnsTheOriginalResult()
    {
        var (processId, stepInstanceId) = await SubmitToWaitingManualAsync();
        using var client = factory.CreateClient();
        var key = $"manual-{Guid.NewGuid():N}";
        var payload = new JsonObject
        {
            ["processId"] = processId.ToString(),
            ["stepInstanceId"] = stepInstanceId.ToString(),
            ["decision"] = "APPROVED",
            ["reason"] = "Documents verified",
        };

        var first = await client.PostActionAsync("/api/workflow/manual", ReviewerToken(), payload, idempotencyKey: key);
        var second = await client.PostActionAsync("/api/workflow/manual", ReviewerToken(), payload.DeepClone(), idempotencyKey: key);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.Body!.ToJsonString(), second.Body!.ToJsonString());

        await DrainAsync(CreateLoop()); // approve_operation
    }

    [Fact]
    public async Task Decide_SameKeyChangedReason_ReturnsIdempotencyConflict()
    {
        var (processId, stepInstanceId) = await SubmitToWaitingManualAsync();
        using var client = factory.CreateClient();
        var key = $"manual-{Guid.NewGuid():N}";

        var first = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "Documents verified",
            },
            idempotencyKey: key);
        Assert.Equal(200, first.StatusCode);

        var second = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "A different reason entirely",
            },
            idempotencyKey: key);

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("idempotency.conflict", second.Body["code"]!.GetValue<string>());

        await DrainAsync(CreateLoop()); // approve_operation, from the first accepted call
    }

    [Fact]
    public async Task Decide_AlreadyDecidedStep_ReturnsDecisionConflict()
    {
        var (processId, stepInstanceId) = await SubmitToWaitingManualAsync();
        using var client = factory.CreateClient();

        var first = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "Documents verified",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");
        Assert.Equal(200, first.StatusCode);

        var second = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "REJECTED",
                ["reason"] = "Changed my mind",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("workflow.decision_conflict", second.Body["code"]!.GetValue<string>());

        await DrainAsync(CreateLoop()); // approve_operation, from the first accepted call
    }

    [Fact]
    public async Task Decide_ConcurrentCompetingDecisions_ExactlyOneAccepted_OtherGetsDecisionConflict()
    {
        var (processId, stepInstanceId) = await SubmitToWaitingManualAsync();
        using var client = factory.CreateClient();

        var payloadA = new JsonObject
        {
            ["processId"] = processId.ToString(),
            ["stepInstanceId"] = stepInstanceId.ToString(),
            ["decision"] = "APPROVED",
            ["reason"] = "Reviewer A approves",
        };
        var payloadB = new JsonObject
        {
            ["processId"] = processId.ToString(),
            ["stepInstanceId"] = stepInstanceId.ToString(),
            ["decision"] = "REJECTED",
            ["reason"] = "Reviewer B rejects",
        };

        var responses = await Task.WhenAll(
            client.PostActionAsync("/api/workflow/manual", ReviewerToken(), payloadA, idempotencyKey: $"manual-a-{Guid.NewGuid():N}"),
            client.PostActionAsync("/api/workflow/manual", ReviewerToken(), payloadB, idempotencyKey: $"manual-b-{Guid.NewGuid():N}"));

        var succeeded = responses.Where(r => r.StatusCode == 200).ToList();
        var conflicted = responses.Where(r => r.StatusCode == 409).ToList();

        Assert.Single(succeeded);
        Assert.Single(conflicted);
        Assert.Equal("workflow.decision_conflict", conflicted[0].Body["code"]!.GetValue<string>());

        var decisionCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.decisions where process_id = @processId", ("processId", processId));
        Assert.Equal(1, decisionCount);

        await DrainAsync(CreateLoop()); // approve_operation or reject_operation, whichever decision won
    }

    [Fact]
    public async Task Decide_UnknownStepInstance_ReturnsControlledNotFound()
    {
        var (processId, _) = await SubmitToWaitingManualAsync();
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = Guid.NewGuid().ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "Documents verified",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("step.not_found", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Decide_WithoutManualPolicy_IsDenied()
    {
        var (processId, stepInstanceId) = await SubmitToWaitingManualAsync();
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/workflow/manual", ClientToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "Documents verified",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("access.denied", response.Body["code"]!.GetValue<string>());
    }

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

    private static string ReviewerToken() => ApiFactory.CreateToken("reviewer", "backoffice", "workflow:manual payment:read");

    private async Task<(Guid ProcessId, Guid StepInstanceId)> SubmitToWaitingManualAsync()
    {
        var operationId = await InsertOperationAsync("150000.00");
        using var client = factory.CreateClient();
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());

        await DrainAsync(CreateLoop()); // validate, check_limit -> WAITING_MANUAL

        var stepInstanceId = await WaitingManualStepInstanceAsync(processId);
        return (processId, stepInstanceId);
    }

    private Task<Guid> WaitingManualStepInstanceAsync(Guid processId) =>
        factory.Postgres.ScalarAsOwnerAsync<Guid>(
            "select step_instance_id from workflow.step_instance "
            + "where process_id = @processId and step_key = 'wait_manual_decision' and state = 'WAITING'",
            ("processId", processId));

    private async Task<Guid> InsertOperationAsync(string amount) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.operations (request_id, operation_kind, amount, currency, status) "
                + "values (@requestId, 'PAYMENT_APPROVAL', @amount, 'RUB', 'CREATED') returning operation_id",
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

    // See PaymentSubmitTests' own copy of this helper: workflow.claim_job
    // claims the globally oldest READY job with no process filter, and every
    // test in this shared collection publishes processes onto the one real
    // "payment-review" flow, so draining to NoJob here is what keeps a job
    // this test creates from being claimed by some later test's own loop.
    private static async Task DrainAsync(WorkerLoop loop, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var outcome = (await loop.RunOnceAsync(CancellationToken.None)).Outcome;
            if (outcome == WorkerStepOutcome.NoJob)
            {
                return;
            }

            Assert.Equal(WorkerStepOutcome.Succeeded, outcome);
        }

        throw new TimeoutException("worker loop did not drain to an empty queue within the expected number of steps");
    }
}
