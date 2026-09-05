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

// Ticket 04 acceptance: payment.submit takes only an operation id, pins a
// process on the real activated payment-review card (PostgresFixture
// publishes it once for the whole run), and the below-threshold amount rides
// the automatic branch all the way to a final operation status without any
// manual step. Idempotency's three barriers (same key, new key against the
// same operation, and a genuine race clearing both at once) are each their
// own scenario below.
[Collection("integration")]
public sealed class PaymentSubmitTests
{
    private readonly ApiFactory factory;

    public PaymentSubmitTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Submit_BelowThreshold_PinsPaymentReview_AndAutomaticBranchReachesCompleted()
    {
        var operationId = await InsertOperationAsync("50000.00");
        using var client = factory.CreateClient();
        var token = ClientToken();

        var response = await client.PostActionAsync(
            "/api/payment/submit", token, new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");

        Assert.Equal(200, response.StatusCode);
        var result = response.Body["result"]!;
        Assert.Equal(operationId.ToString(), result["operationId"]!.GetValue<string>());
        Assert.Equal("payment-review", result["flowName"]!.GetValue<string>());
        Assert.Equal(1, result["flowVersion"]!.GetValue<int>());
        Assert.Equal("PROCESSING", result["status"]!.GetValue<string>());
        var processId = Guid.Parse(result["processId"]!.GetValue<string>());

        var operationStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("PROCESSING", operationStatus);

        var pinnedProcessId = await factory.Postgres.ScalarAsOwnerAsync<Guid>(
            "select process_id from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal(processId, pinnedProcessId);

        await DrainAsync(CreateLoop());

        var finalStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("COMPLETED", finalStatus);

        var eventTypes = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select event_type from core.operation_events where operation_id = @operationId order by occurred_at", connection);
            command.Parameters.AddWithValue("operationId", operationId);
            await using var reader = await command.ExecuteReaderAsync();
            var types = new List<string>();
            while (await reader.ReadAsync())
            {
                types.Add(reader.GetString(0));
            }

            return types;
        });
        Assert.Contains("OPERATION_SUBMITTED", eventTypes);

        var eventsResponse = await client.PostActionAsync(
            "/api/operation/events", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() });
        Assert.Equal(200, eventsResponse.StatusCode);
        var events = eventsResponse.Body["result"]!["events"]!.AsArray();
        Assert.Contains(events, e => e!["eventType"]!.GetValue<string>() == "OPERATION_SUBMITTED");
    }

    [Fact]
    public async Task Submit_UnknownOperationId_ReturnsControlledNotFound()
    {
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = Guid.NewGuid().ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("operation.not_found", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Submit_SameKeySameBody_ReturnsTheOriginalResult()
    {
        var operationId = await InsertOperationAsync("50000.00");
        using var client = factory.CreateClient();
        var key = $"submit-{Guid.NewGuid():N}";
        var payload = new JsonObject { ["operationId"] = operationId.ToString() };

        var first = await client.PostActionAsync("/api/payment/submit", ClientToken(), payload, idempotencyKey: key);
        var second = await client.PostActionAsync("/api/payment/submit", ClientToken(), payload, idempotencyKey: key);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.Body!.ToJsonString(), second.Body!.ToJsonString());

        await DrainAsync(CreateLoop());
    }

    [Fact]
    public async Task Submit_SameKeyChangedBody_ReturnsIdempotencyConflict()
    {
        var operationA = await InsertOperationAsync("50000.00");
        var operationB = await InsertOperationAsync("60000.00");
        using var client = factory.CreateClient();
        var key = $"submit-{Guid.NewGuid():N}";

        var first = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationA.ToString() }, idempotencyKey: key);
        Assert.Equal(200, first.StatusCode);

        var second = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationB.ToString() }, idempotencyKey: key);

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("idempotency.conflict", second.Body["code"]!.GetValue<string>());

        // Only operationA ever reached the target (operationB's call was
        // rejected before invoking it), so only its own job needs draining.
        await DrainAsync(CreateLoop());
    }

    [Fact]
    public async Task Submit_NewKeySameOperation_ReturnsTheSamePinnedProcess()
    {
        var operationId = await InsertOperationAsync("50000.00");
        using var client = factory.CreateClient();
        var payload = new JsonObject { ["operationId"] = operationId.ToString() };

        var first = await client.PostActionAsync("/api/payment/submit", ClientToken(), payload, idempotencyKey: $"submit-{Guid.NewGuid():N}");
        var second = await client.PostActionAsync("/api/payment/submit", ClientToken(), payload, idempotencyKey: $"submit-{Guid.NewGuid():N}");

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.Body["result"]!["processId"]!.GetValue<string>(), second.Body["result"]!["processId"]!.GetValue<string>());

        // The second submit pinned no new process (it read the first's
        // pinned one back), so only one job was ever created either way.
        await DrainAsync(CreateLoop());
    }

    [Fact]
    public async Task Submit_AfterOperationFinishes_StillReportsProcessingStatus()
    {
        var operationId = await InsertOperationAsync("50000.00");
        using var client = factory.CreateClient();

        var first = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, first.StatusCode);

        await DrainAsync(CreateLoop());

        var finalStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from core.operations where operation_id = @operationId", ("operationId", operationId));
        Assert.Equal("COMPLETED", finalStatus);

        var replay = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");

        Assert.Equal(200, replay.StatusCode);
        Assert.Equal("PROCESSING", replay.Body["result"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Submit_TwentyConcurrentRequests_CreateExactlyOneProcess()
    {
        var operationId = await InsertOperationAsync("50000.00");
        using var client = factory.CreateClient();
        var payload = new JsonObject { ["operationId"] = operationId.ToString() };

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            client.PostActionAsync("/api/payment/submit", ClientToken(), payload.DeepClone(), idempotencyKey: $"submit-concurrent-{i}-{Guid.NewGuid():N}")));

        Assert.All(responses, r => Assert.Equal(200, r.StatusCode));

        var processIds = responses.Select(r => r.Body["result"]!["processId"]!.GetValue<string>()).Distinct().ToList();
        Assert.Single(processIds);

        var processCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow.process_instance where business_key = @businessKey",
            ("businessKey", operationId.ToString()));
        Assert.Equal(1, processCount);

        await DrainAsync(CreateLoop());
    }

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

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

    // workflow.claim_job (migration 022) claims the globally oldest READY job
    // with no process filter, and every test in this [Collection("integration")]
    // shares one Postgres container and the one real "payment-review" flow
    // (PostgresFixture activates it once for the whole run) -- so a job this
    // test's own scenario does not care to finish would otherwise sit in the
    // queue and get claimed by a *later* test's own RunOnceAsync call instead
    // of that test's own step. Draining every job this test created down to
    // NoJob, right here, is what keeps the shared queue empty at the start of
    // every other test -- the invariant every scenario above and in
    // ManualDecisionTests relies on to count its own steps precisely.
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
