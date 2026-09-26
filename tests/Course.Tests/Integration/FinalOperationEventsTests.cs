using System.Text;
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

// Ticket 16 acceptance: a terminal core.operations.status is never written
// without exactly one matching immutable row in core.operation_events, on all
// three terminal paths (automatic approval, manual approval/rejection,
// provider receipt), and a repeated terminal transition adds neither a second
// event nor a status change.
[Collection("integration")]
public sealed class FinalOperationEventsTests : IDisposable
{
    private readonly ApiFactory factory;

    // One data source for the whole test, not one per CreateLoop call: this
    // class drives the worker loop several times per test and an undisposed
    // data source per call is enough to exhaust postgres's connection slots
    // for the rest of the shared-container run.
    private readonly NpgsqlDataSource workerDataSource;

    public FinalOperationEventsTests(ApiFactory factory)
    {
        this.factory = factory;
        workerDataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
    }

    public void Dispose() => workerDataSource.Dispose();

    [Fact]
    public async Task AutomaticApproval_WritesExactlyOneCompletedEvent()
    {
        var (operationId, _) = await SubmitApprovalAsync("50000.00");
        await DrainAsync(CreateLoop()); // validate, check_limit -> WITHIN_LIMIT, approve

        Assert.Equal("COMPLETED", await StatusAsync(operationId));
        Assert.Equal(1, await EventCountAsync(operationId, "OPERATION_COMPLETED"));
        Assert.Equal(0, await EventCountAsync(operationId, "OPERATION_REJECTED"));
    }

    [Fact]
    public async Task ManualApproval_WritesExactlyOneCompletedEvent()
    {
        var operationId = await DecideManuallyAsync("APPROVED");

        Assert.Equal("COMPLETED", await StatusAsync(operationId));
        Assert.Equal(1, await EventCountAsync(operationId, "OPERATION_COMPLETED"));
    }

    [Fact]
    public async Task ManualRejection_WritesExactlyOneRejectedEvent()
    {
        var operationId = await DecideManuallyAsync("REJECTED");

        Assert.Equal("REJECTED", await StatusAsync(operationId));
        Assert.Equal(1, await EventCountAsync(operationId, "OPERATION_REJECTED"));
        Assert.Equal(0, await EventCountAsync(operationId, "OPERATION_COMPLETED"));
    }

    [Theory]
    [InlineData("COMPLETED", "OPERATION_COMPLETED")]
    [InlineData("REJECTED", "OPERATION_REJECTED")]
    public async Task ProviderReceipt_WritesExactlyOneMatchingFinalEvent(string receiptOutcome, string expectedEventType)
    {
        var operationId = await RunProviderPathAsync(receiptOutcome);

        Assert.Equal(receiptOutcome, await StatusAsync(operationId));
        Assert.Equal(1, await EventCountAsync(operationId, expectedEventType));
    }

    // The history is the record of what actually decided the operation, so the
    // final event's payload_hash is bound to the persisted receipt/decision --
    // never to the action payload, which carries nothing but the operation id.
    [Fact]
    public async Task FinalEvent_IsBoundToPersistedEvidence_NotToTheActionPayload()
    {
        var operationId = await RunProviderPathAsync("COMPLETED");

        var payloadHash = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select payload_hash from core.operation_events "
            + "where operation_id = @id and event_type = 'OPERATION_COMPLETED'",
            ("id", operationId));
        var bareActionPayloadHash = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select encode(sha256(@payload::jsonb::text::bytea), 'hex')",
            ("payload", new JsonObject { ["operationId"] = operationId.ToString() }.ToJsonString()));

        Assert.Matches("^[0-9a-f]{64}$", payloadHash);
        Assert.NotEqual(bareActionPayloadHash, payloadHash);
    }

    [Fact]
    public async Task RepeatedTerminalTransition_AddsNoSecondEvent_AndLeavesStatusUnchanged()
    {
        var operationId = await RunProviderPathAsync("REJECTED");

        // The same terminal action again, and the opposite one too: neither may
        // rewrite an already-final status or grow the history.
        var repeatedReject = await CallTargetDirectlyAsync("payment", "reject_v1", operationId);
        var opposingApprove = await CallTargetDirectlyAsync("payment", "approve_v1", operationId);

        Assert.Equal("ok", repeatedReject["status"]!.GetValue<string>());
        Assert.Equal("ok", opposingApprove["status"]!.GetValue<string>());
        Assert.Equal("REJECTED", await StatusAsync(operationId));
        Assert.Equal(1, await EventCountAsync(operationId, "OPERATION_REJECTED"));
        Assert.Equal(0, await EventCountAsync(operationId, "OPERATION_COMPLETED"));
    }

    // The status update and the event insert happen in the caller's own
    // transaction, so a technical failure after the call undoes both or
    // neither -- a rolled-back attempt must leave no half-written history
    // behind.
    [Fact]
    public async Task RolledBackTerminalCall_LeavesNeitherTheStatusNorTheHistoryChanged()
    {
        var (operationId, _) = await SubmitApprovalAsync("150000.00");
        await DrainAsync(CreateLoop()); // validate, check_limit -> WAITING_MANUAL

        var statusInsideTransaction = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();

            await using var call = new NpgsqlCommand("select payment.reject_v1(@context, @payload)", connection, transaction);
            call.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb) { Value = "{}" });
            call.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["operationId"] = operationId.ToString() }.ToJsonString(),
            });
            var envelope = JsonNode.Parse((string)(await call.ExecuteScalarAsync())!)!;
            Assert.Equal("ok", envelope["status"]!.GetValue<string>());

            await using var read = new NpgsqlCommand(
                "select status from core.operations where operation_id = @id", connection, transaction);
            read.Parameters.AddWithValue("id", operationId);
            var status = (string)(await read.ExecuteScalarAsync())!;

            await transaction.RollbackAsync();
            return status;
        });

        // The uncommitted transaction really did reach the terminal write --
        // otherwise this test would pass for the wrong reason.
        Assert.Equal("REJECTED", statusInsideTransaction);

        Assert.Equal("PROCESSING", await StatusAsync(operationId));
        Assert.Equal(0, await EventCountAsync(operationId, "OPERATION_REJECTED"));
        Assert.Equal(0, await EventCountAsync(operationId, "OPERATION_COMPLETED"));
    }

    [Fact]
    public async Task UnknownOperation_WritesNoEventAtAll()
    {
        var unknown = Guid.NewGuid();

        var result = await CallTargetDirectlyAsync("payment", "approve_v1", unknown);

        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal("operation.not_found", result["code"]!.GetValue<string>());
        Assert.Equal(0, await EventCountAsync(unknown, "OPERATION_COMPLETED"));
    }

    private async Task<Guid> DecideManuallyAsync(string decision)
    {
        var (operationId, processId) = await SubmitApprovalAsync("150000.00");
        using var client = factory.CreateClient();
        await DrainAsync(CreateLoop()); // validate, check_limit -> WAITING_MANUAL

        var stepInstanceId = await factory.Postgres.ScalarAsOwnerAsync<Guid>(
            "select step_instance_id from workflow.step_instance "
            + "where process_id = @processId and step_key = 'wait_manual_decision' and state = 'WAITING'",
            ("processId", processId));

        var decided = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = decision,
                ["reason"] = "Documents verified",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");
        Assert.Equal(200, decided.StatusCode);

        await DrainAsync(CreateLoop()); // approve_operation / reject_operation
        return operationId;
    }

    private async Task<Guid> RunProviderPathAsync(string receiptOutcome)
    {
        var operationId = await InsertOperationAsync("1000.00", "PAYMENT_EXECUTION");
        using var client = factory.CreateClient();
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        await DrainAsync(CreateLoop()); // validate_operation, prepare_external_request -> wait_receipt

        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = new JsonObject
        {
            ["externalRequestId"] = $"ext-{operationId}",
            ["messageId"] = messageId,
            ["occurredAt"] = "2026-09-04T12:00:00Z",
            ["outcome"] = receiptOutcome,
            ["providerPaymentId"] = messageId,
            ["version"] = 1,
        }.ToJsonString();
        var header = ProviderSignature.Header(ApiFactory.ProviderHmacSecret, Encoding.UTF8.GetBytes(body));
        var accepted = await client.PostRawActionAsync(
            "/api/receipt/accept", ProviderToken(), body, $"receipt-{Guid.NewGuid():N}", header);
        Assert.Equal(200, accepted.StatusCode);

        // At least this test's own message: reconcile_inbox is global, so a
        // message some other test in this shared collection left RECEIVED is
        // counted here too.
        Assert.True(await factory.Postgres.ScalarAsOwnerAsync<int>("select delivery.reconcile_inbox(50)") >= 1);
        await DrainAsync(CreateLoop()); // apply_receipt, complete_operation / reject_operation
        return operationId;
    }

    private async Task<(Guid OperationId, Guid ProcessId)> SubmitApprovalAsync(string amount)
    {
        var operationId = await InsertOperationAsync(amount, "PAYMENT_APPROVAL");
        using var client = factory.CreateClient();
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        return (operationId, Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>()));
    }

    private Task<string> StatusAsync(Guid operationId) =>
        factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @id", ("id", operationId));

    private async Task<int> EventCountAsync(Guid operationId, string eventType) =>
        (int)await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.operation_events where operation_id = @id and event_type = @eventType",
            ("id", operationId), ("eventType", eventType));

    private async Task<JsonNode> CallTargetDirectlyAsync(string schema, string function, Guid operationId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand($"select {schema}.{function}(@context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["principal"] = "test-direct-call" }.ToJsonString(),
            });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["operationId"] = operationId.ToString() }.ToJsonString(),
            });
            return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
        });

    private async Task<Guid> InsertOperationAsync(string amount, string kind) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.operations (request_id, operation_kind, amount, currency, status) "
                + "values (@requestId, @kind, @amount, 'RUB', 'CREATED') returning operation_id",
                connection);
            command.Parameters.AddWithValue("requestId", $"req-{Guid.NewGuid():N}");
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric)
            {
                Value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            });
            return (Guid)(await command.ExecuteScalarAsync())!;
        });

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

    private static string ReviewerToken() => ApiFactory.CreateToken("reviewer", "backoffice", "workflow:manual payment:read");

    private static string ProviderToken() => ApiFactory.CreateToken("receipt-provider", "receipt-adapter", "receipt:write");

    private WorkerLoop CreateLoop()
    {
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            $"owner-{Guid.NewGuid():N}", workerDataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    // See ManualDecisionTests' own copy: every test in this collection shares
    // the one real payment-review/payment-processing flow, so draining to
    // NoJob is what keeps this test's jobs out of a later test's loop.
    private static async Task DrainAsync(WorkerLoop loop, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var step = await loop.RunOnceAsync(CancellationToken.None);
            if (step.Outcome == WorkerStepOutcome.NoJob)
            {
                return;
            }

            Assert.True(step.Outcome == WorkerStepOutcome.Succeeded, $"outcome={step.Outcome} errorCode={step.ErrorCode}");
        }

        throw new TimeoutException("worker loop did not drain to an empty queue within the expected number of steps");
    }
}
