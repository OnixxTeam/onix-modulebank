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

// Ticket 06 acceptance: submitting a PAYMENT_EXECUTION operation rides the
// real, given payment-processing-v1 card through validate_operation and
// prepare_external_request, and stops there -- the process reaches the saved
// wait_receipt step and no further, with exactly one external request and one
// durable Outbox row to show for it. No network code exists yet (that is
// ticket 11); a stopped/never-started dispatcher is the point being proven.
[Collection("integration")]
public sealed class PrepareExternalTests
{
    private readonly ApiFactory factory;

    public PrepareExternalTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Submit_PaymentExecution_ReachesWaitReceipt_WithOneExternalRequestAndOutboxRow()
    {
        var operationId = await InsertOperationAsync("1000.00");
        using var client = factory.CreateClient();

        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        Assert.Equal("payment-processing", submit.Body["result"]!["flowName"]!.GetValue<string>());
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());

        var loop = CreateLoop();
        await DrainAsync(loop); // validate_operation, prepare_external_request

        var (state, currentStepKey) = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, current_step_key from workflow.process_instance where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1));
        });
        Assert.Equal("WAITING_SIGNAL", state);
        Assert.Equal("wait_receipt", currentStepKey);

        var externalRequestId = $"ext-{operationId}";

        var externalRequest = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select operation_id, state, payload_hash from autocheck.external_requests where external_request_id = @id",
                connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (OperationId: reader.GetGuid(0), State: reader.GetString(1), PayloadHash: reader.GetString(2));
        });
        Assert.Equal(operationId, externalRequest.OperationId);
        Assert.Equal("CREATED", externalRequest.State);
        Assert.Equal(64, externalRequest.PayloadHash.Length);

        var outboxRowCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from autocheck.outbox where external_request_id = @id", ("id", externalRequestId));
        Assert.Equal(1, outboxRowCount);

        var outbox = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, attempt_count, delivered_at, amount, currency, correlation_id "
                + "from delivery.outbox where external_request_id = @id",
                connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                State: reader.GetString(0),
                AttemptCount: reader.GetInt32(1),
                DeliveredAt: reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2),
                Amount: reader.GetString(3),
                Currency: reader.GetString(4),
                CorrelationId: reader.GetGuid(5));
        });
        Assert.Equal("PENDING", outbox.State);
        Assert.Equal(0, outbox.AttemptCount);
        Assert.Null(outbox.DeliveredAt);
        // Exact contract-format string (docs/week3/external-contracts.md), not "1000":
        // a bare integer amount would make the future provider body byte-for-byte
        // different from a retried attempt's, if it were ever recomputed instead of
        // stored once.
        Assert.Equal("1000.00", outbox.Amount);
        Assert.Equal("RUB", outbox.Currency);
        Assert.NotEqual(Guid.Empty, outbox.CorrelationId);
    }

    [Fact]
    public async Task RepeatedPrepareExternal_SameOperation_CreatesExactlyOneRequestAndOutboxRow()
    {
        var operationId = await InsertOperationAsync("2000.00");

        var first = await CallPrepareExternalAsync(operationId);
        var second = await CallPrepareExternalAsync(operationId);

        Assert.Equal("ok", first["status"]!.GetValue<string>());
        Assert.Equal("ok", second["status"]!.GetValue<string>());
        Assert.Equal(
            first["result"]!["externalRequestId"]!.GetValue<string>(),
            second["result"]!["externalRequestId"]!.GetValue<string>());

        var externalRequestId = $"ext-{operationId}";
        var requestCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.external_requests where external_request_id = @id", ("id", externalRequestId));
        Assert.Equal(1, requestCount);

        var outboxCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from delivery.outbox where external_request_id = @id", ("id", externalRequestId));
        Assert.Equal(1, outboxCount);
    }

    // Calls the target function directly under course_owner, the same
    // elevation every SECURITY DEFINER target already runs as -- standing in
    // for a second job attempt without needing to force an actual worker
    // retry (the worker's own atomicity already prevents that from happening
    // in practice; this test proves the function is safe even if it did).
    private async Task<JsonNode> CallPrepareExternalAsync(Guid operationId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
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

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

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

    // Same reasoning as PaymentSubmitTests.DrainAsync: workflow.claim_job has
    // no per-process filter, and this suite shares one queue with every other
    // [Collection("integration")] test -- draining to NoJob right here (not
    // to a fixed step count) keeps that shared queue empty for whoever runs
    // next, and stops naturally at wait_receipt since that step opens no job.
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
