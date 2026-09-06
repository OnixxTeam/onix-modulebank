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

// Ticket 10 acceptance: payment-processing closes its main domain invariant --
// the operation reaches a final status only by the saved receipt's own
// outcome, never by a client body or a provider transport response, and the
// same holds whether the receipt arrived before or after wait_receipt. The
// perimeter still doesn't exist: a signal is created by calling ticket 09's
// delivery.reconcile_inbox directly, exactly as the not-yet-built reconciler
// will, and both payment-processing's complete/reject and payment-review's
// approve/reject run through the same core functions with no per-card branch.
[Collection("integration")]
public sealed class ApplyReceiptTests
{
    private readonly ApiFactory factory;

    public ApplyReceiptTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task CompletedReceipt_AfterWaitReceipt_DrivesTheOperationToCompleted_WithTheReceiptAsEvidence()
    {
        var (client, externalRequestId, operationId, processId) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, messageId, outcome: "COMPLETED"), $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(1, await ReconcileInboxAsync());
        await DrainAsync(CreateLoop()); // apply_receipt, complete_operation

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @id", ("id", operationId));
        Assert.Equal("COMPLETED", status);

        var (state, currentStepKey) = await ProcessStateAsync(processId);
        Assert.Equal("COMPLETED", state);
        Assert.Equal("done", currentStepKey);

        var receiptAppliedAt = await factory.Postgres.ScalarAsOwnerAsync<DateTime?>(
            "select applied_at from core.receipts where message_id = @id", ("id", messageId));
        Assert.NotNull(receiptAppliedAt);
    }

    [Fact]
    public async Task RejectedReceipt_AfterWaitReceipt_DrivesTheOperationToRejected()
    {
        var (client, externalRequestId, operationId, processId) = await PrepareExternalRequestAsync("1100.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, messageId, outcome: "REJECTED"), $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(1, await ReconcileInboxAsync());
        await DrainAsync(CreateLoop());

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @id", ("id", operationId));
        Assert.Equal("REJECTED", status);

        var (state, currentStepKey) = await ProcessStateAsync(processId);
        Assert.Equal("COMPLETED", state); // process itself finishes normally; the operation carries REJECTED
        Assert.Equal("done", currentStepKey);
    }

    // The literal early-receipt case: the receipt is saved and reconciled into
    // a signal before the operation is ever submitted to a process at all
    // (SPEC ticket 10, "Квитанция, зафиксированная до входа в шаг ожидания,
    // применяется при входе в него"). apply_pending_signal (migration 026)
    // buffers it as ACCEPTED and applies it the moment enter_step marks
    // wait_receipt WAITING -- proving the same final status comes out whether
    // the receipt beat the process there or not.
    [Fact]
    public async Task CompletedReceipt_ReconciledBeforeTheOperationIsEvenSubmitted_StillAppliesWhenTheProcessReachesWait()
    {
        var operationId = await InsertOperationAsync("1200.00");
        var externalRequestId = await CallPrepareExternalDirectlyAsync(operationId);
        using var client = factory.CreateClient();
        var messageId = $"provider-{Guid.NewGuid():N}";
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, messageId, outcome: "COMPLETED"), $"receipt-{Guid.NewGuid():N}");

        // Unresolvable yet: the operation has no pinned process (ticket 09's
        // own boundary), so the message is left RECEIVED, not lost.
        Assert.Equal(0, await ReconcileInboxAsync());

        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());
        await DrainAsync(CreateLoop()); // validate_operation, prepare_external_request (already-exists branch) -> wait_receipt

        Assert.Equal(1, await ReconcileInboxAsync());
        await DrainAsync(CreateLoop()); // apply_receipt, complete_operation

        var status = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @id", ("id", operationId));
        Assert.Equal("COMPLETED", status);

        var (state, _) = await ProcessStateAsync(processId);
        Assert.Equal("COMPLETED", state);
    }

    // Ticket 10's own trap: apply_receipt never takes an outcome as an
    // argument (SPEC "Действие не принимает исход, статус или сумму от
    // вызывающего") -- it reads the saved row itself. Calling it directly with
    // a payload that carries nothing but operationId, against a REJECTED
    // receipt, still reports REJECTED: there is no channel for a caller to
    // override it.
    [Fact]
    public async Task ApplyReceipt_IgnoresAnythingBeyondOperationId_AndAlwaysReportsTheSavedOutcome()
    {
        var (_, _, operationId, _) = await PrepareExternalRequestAsyncWithReceiptApplied("1300.00", "REJECTED");

        var result = await CallApplyReceiptDirectlyAsync(operationId);

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal("REJECTED", result["outcome"]!.GetValue<string>());
    }

    private async Task<(HttpClient Client, string ExternalRequestId, Guid OperationId, Guid ProcessId)> PrepareExternalRequestAsyncWithReceiptApplied(
        string amount, string outcome)
    {
        var (client, externalRequestId, operationId, processId) = await PrepareExternalRequestAsync(amount);
        await PostSignedReceiptAsync(
            client, ReceiptBody(externalRequestId, $"provider-{Guid.NewGuid():N}", outcome: outcome), $"receipt-{Guid.NewGuid():N}");
        Assert.Equal(1, await ReconcileInboxAsync());
        await DrainAsync(CreateLoop());
        return (client, externalRequestId, operationId, processId);
    }

    private async Task<JsonNode> CallApplyReceiptDirectlyAsync(Guid operationId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand("select payment.apply_receipt_v1(@context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb) { Value = "{}" });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["operationId"] = operationId.ToString() }.ToJsonString(),
            });
            var raw = (string)(await command.ExecuteScalarAsync())!;
            return JsonNode.Parse(raw)!;
        });

    private async Task<int> ReconcileInboxAsync() =>
        await factory.Postgres.ScalarAsOwnerAsync<int>("select delivery.reconcile_inbox(50)");

    private async Task<string> CallPrepareExternalDirectlyAsync(Guid operationId)
    {
        var result = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand("select payment.prepare_external_v1(@context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["correlationId"] = Guid.NewGuid().ToString() }.ToJsonString(),
            });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["operationId"] = operationId.ToString() }.ToJsonString(),
            });
            var raw = (string)(await command.ExecuteScalarAsync())!;
            return JsonNode.Parse(raw)!;
        });

        return result["result"]!["externalRequestId"]!.GetValue<string>();
    }

    private async Task<(string State, string CurrentStepKey)> ProcessStateAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, current_step_key from workflow.process_instance where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1));
        });

    private static async Task<ActionResponse> PostSignedReceiptAsync(HttpClient client, string body, string idempotencyKey)
    {
        var header = ProviderSignature.Header(ApiFactory.ProviderHmacSecret, Encoding.UTF8.GetBytes(body));
        return await client.PostRawActionAsync("/api/receipt/accept", ProviderToken(), body, idempotencyKey, header);
    }

    private static string ReceiptBody(string externalRequestId, string messageId, string outcome, int version = 1) =>
        new JsonObject
        {
            ["externalRequestId"] = externalRequestId,
            ["messageId"] = messageId,
            ["occurredAt"] = "2026-09-04T12:00:00Z",
            ["outcome"] = outcome,
            ["providerPaymentId"] = messageId,
            ["version"] = version,
        }.ToJsonString();

    private static string ProviderToken() => ApiFactory.CreateToken("receipt-provider", "receipt-adapter", "receipt:write");

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

    private async Task<(HttpClient Client, string ExternalRequestId, Guid OperationId, Guid ProcessId)> PrepareExternalRequestAsync(string amount)
    {
        var operationId = await InsertOperationAsync(amount);
        var client = factory.CreateClient();

        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());

        await DrainAsync(CreateLoop());

        return (client, $"ext-{operationId}", operationId, processId);
    }

    private async Task<Guid> InsertOperationAsync(string amount) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.operations (request_id, operation_kind, amount, currency, status) "
                + "values (@requestId, 'PAYMENT_EXECUTION', @amount, 'RUB', 'CREATED') returning operation_id",
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
