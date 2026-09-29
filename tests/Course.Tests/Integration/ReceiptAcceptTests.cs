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

// Ticket 08 acceptance: receipt.accept runs on the same generic route as every
// other action (no dedicated controller), requires the ticket 07 signature
// boundary's own transport.signatureVerified marker rather than trusting the
// body, fixes the receipt in Inbox before any process ever applies it, and
// tells an identical repeat, a content-duplicate under a different
// idempotency command, and a genuine conflict apart -- three different
// outcomes for what looks like "the same messageId came back".
[Collection("integration")]
public sealed class ReceiptAcceptTests
{
    private readonly ApiFactory factory;

    public ReceiptAcceptTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task NewReceipt_ConfirmsOutboxAndExternalRequest_AndRecordsReceiptAndInbox()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = ReceiptBody(externalRequestId, messageId);

        var response = await PostSignedReceiptAsync(client, body, idempotencyKey: $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(200, response.StatusCode);
        var result = response.Body["result"]!;
        Assert.Equal("RECEIVED", response.Body["outcome"]!.GetValue<string>());
        Assert.Equal(messageId, result["messageId"]!.GetValue<string>());
        Assert.Equal(externalRequestId, result["externalRequestId"]!.GetValue<string>());
        Assert.Equal("RECEIVED", result["state"]!.GetValue<string>());

        var outbox = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, delivered_at from delivery.outbox where external_request_id = @id", connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), DeliveredAt: reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1));
        });
        Assert.Equal("CONFIRMED", outbox.State);
        Assert.NotNull(outbox.DeliveredAt);

        var externalRequestState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from core.external_requests where external_request_id = @id", ("id", externalRequestId));
        Assert.Equal("CONFIRMED", externalRequestState);

        var inbox = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, applied_at from autocheck.inbox where message_id = @id", connection);
            command.Parameters.AddWithValue("id", messageId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), AppliedAt: reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1));
        });
        Assert.Equal("RECEIVED", inbox.State);
        Assert.Null(inbox.AppliedAt);

        var receipt = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select outcome, signature_valid, message_version from autocheck.receipts where message_id = @id", connection);
            command.Parameters.AddWithValue("id", messageId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (Outcome: reader.GetString(0), SignatureValid: reader.GetBoolean(1), Version: reader.GetInt32(2));
        });
        Assert.Equal("COMPLETED", receipt.Outcome);
        Assert.True(receipt.SignatureValid);
        Assert.Equal(1, receipt.Version);
    }

    [Fact]
    public async Task WithoutSignature_Rejects403_AndWritesNothing()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = ReceiptBody(externalRequestId, messageId);

        var response = await client.PostRawActionAsync(
            "/api/receipt/accept", ProviderToken(), body, idempotencyKey: $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("receipt.signature_required", response.Body["code"]!.GetValue<string>());

        var inboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal(0, inboxCount);
    }

    [Fact]
    public async Task UnknownExternalRequestId_Rejects422_AndWritesNoInboxRow()
    {
        using var client = factory.CreateClient();
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = ReceiptBody($"ext-{Guid.NewGuid()}", messageId);

        var response = await PostSignedReceiptAsync(client, body, idempotencyKey: $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("receipt.external_request_not_found", response.Body["code"]!.GetValue<string>());

        var inboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal(0, inboxCount);
    }

    [Fact]
    public async Task InvalidReceiptVersion_Rejects422PayloadInvalid()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = ReceiptBody(externalRequestId, messageId, version: 2);

        var response = await PostSignedReceiptAsync(client, body, idempotencyKey: $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("payload.invalid", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task IdenticalRepeat_SameIdempotencyKeyAndBody_ReturnsOriginalResult()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = ReceiptBody(externalRequestId, messageId);
        var key = $"receipt-{Guid.NewGuid():N}";

        var first = await PostSignedReceiptAsync(client, body, key);
        var second = await PostSignedReceiptAsync(client, body, key);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.Body.ToJsonString(), second.Body.ToJsonString());

        var inboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal(1, inboxCount);
    }

    [Fact]
    public async Task SameMessageIdSameBody_DifferentIdempotencyKey_ReturnsDuplicateOutcome()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = ReceiptBody(externalRequestId, messageId);

        var first = await PostSignedReceiptAsync(client, body, $"receipt-{Guid.NewGuid():N}");
        var second = await PostSignedReceiptAsync(client, body, $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(200, first.StatusCode);
        Assert.Equal("RECEIVED", first.Body["outcome"]!.GetValue<string>());
        Assert.Equal(200, second.StatusCode);
        Assert.Equal("DUPLICATE", second.Body["outcome"]!.GetValue<string>());
        Assert.Equal(messageId, second.Body["result"]!["messageId"]!.GetValue<string>());

        var inboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal(1, inboxCount);
    }

    [Fact]
    public async Task SameMessageIdDifferentBody_Rejects409_AndLeavesOriginalRecordUnchanged()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var originalBody = ReceiptBody(externalRequestId, messageId, outcome: "COMPLETED");
        var conflictingBody = ReceiptBody(externalRequestId, messageId, outcome: "REJECTED");

        var first = await PostSignedReceiptAsync(client, originalBody, $"receipt-{Guid.NewGuid():N}");
        Assert.Equal(200, first.StatusCode);

        var second = await PostSignedReceiptAsync(client, conflictingBody, $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("idempotency.conflict", second.Body["code"]!.GetValue<string>());

        var storedOutcome = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select outcome from core.receipts where message_id = @id", ("id", messageId));
        Assert.Equal("COMPLETED", storedOutcome);

        var inboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal(1, inboxCount);
    }

    // Ticket 08's own pitfall, guarded end to end: body_hash must come from
    // the exact wire bytes (ticket 07's context.rawBodyHash), never from
    // Postgres re-rendering the parsed payload as text -- the latter would
    // normalise key order and treat these two differently-formatted-but-
    // same-fields bodies as identical content, silently deduping a case the
    // contract requires to be told apart.
    [Fact]
    public async Task SameMessageIdSameFields_DifferentByteOrdering_IsAGenuineConflictNotADuplicate()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var canonicalBody = ReceiptBody(externalRequestId, messageId);
        var reorderedBody = new JsonObject
        {
            ["version"] = 1,
            ["providerPaymentId"] = messageId,
            ["outcome"] = "COMPLETED",
            ["occurredAt"] = "2026-09-04T12:00:00Z",
            ["messageId"] = messageId,
            ["externalRequestId"] = externalRequestId,
        }.ToJsonString();
        Assert.NotEqual(canonicalBody, reorderedBody);

        var first = await PostSignedReceiptAsync(client, canonicalBody, $"receipt-{Guid.NewGuid():N}");
        Assert.Equal(200, first.StatusCode);
        Assert.Equal("RECEIVED", first.Body["outcome"]!.GetValue<string>());

        var second = await PostSignedReceiptAsync(client, reorderedBody, $"receipt-{Guid.NewGuid():N}");

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("idempotency.conflict", second.Body["code"]!.GetValue<string>());
    }

    // Ticket 17: the same trap one layer up. With a *different* idempotency
    // command the test above already reaches receipt.accept, which compares the
    // exact raw-body hash itself. With the *same* command, api.invoke's generic
    // replay used to answer first -- it hashes the normalised jsonb text, so
    // two differently-serialized signed bodies looked identical to it and the
    // second one got the first one's envelope back, HTTP 200, with the exact
    // -byte conflict never reaching the Inbox at all.
    [Fact]
    public async Task SameIdempotencyKey_DifferentlySerializedSignedBody_Rejects409_AndNeverReplays()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        var key = $"receipt-{Guid.NewGuid():N}";
        var compactBody = ReceiptBody(externalRequestId, messageId);
        var reserializedBody = $$"""
            {
              "version": 1,
              "providerPaymentId": "{{messageId}}",
              "outcome": "COMPLETED",
              "occurredAt": "2026-09-04T12:00:00Z",
              "messageId": "{{messageId}}",
              "externalRequestId": "{{externalRequestId}}"
            }
            """;
        Assert.NotEqual(compactBody, reserializedBody);

        var first = await PostSignedReceiptAsync(client, compactBody, key);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal("RECEIVED", first.Body["outcome"]!.GetValue<string>());

        var second = await PostSignedReceiptAsync(client, reserializedBody, key);

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("idempotency.conflict", second.Body["code"]!.GetValue<string>());

        var inboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal(1, inboxCount);

        var receiptCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.receipts where message_id = @id", ("id", messageId));
        Assert.Equal(1, receiptCount);
    }

    private static async Task<ActionResponse> PostSignedReceiptAsync(HttpClient client, string body, string idempotencyKey)
    {
        var header = ProviderSignature.Header(ApiFactory.ProviderHmacSecret, Encoding.UTF8.GetBytes(body));
        return await client.PostRawActionAsync("/api/receipt/accept", ProviderToken(), body, idempotencyKey, header);
    }

    private static string ReceiptBody(string externalRequestId, string messageId, string outcome = "COMPLETED", int version = 1) =>
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

    // Submits a fresh PAYMENT_EXECUTION operation and drains the worker down
    // to wait_receipt, so its external request exists in core.external_requests
    // for a receipt.accept call to address -- the same setup PrepareExternalTests
    // (ticket 06) exercises directly through the SQL function.
    private async Task<(HttpClient Client, string ExternalRequestId, Guid OperationId)> PrepareExternalRequestAsync(string amount)
    {
        var operationId = await InsertOperationAsync(amount);
        var client = factory.CreateClient();

        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);

        await DrainAsync(CreateLoop());

        return (client, $"ext-{operationId}", operationId);
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
