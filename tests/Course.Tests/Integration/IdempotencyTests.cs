using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class IdempotencyTests(ApiFactory factory)
{
    private static readonly JsonNode ExecutionPayload = new JsonObject
    {
        ["operationKind"] = "PAYMENT_EXECUTION",
        ["amount"] = "10.00",
        ["currency"] = "RUB",
    };

    // contract-reference: "Идентичный повтор возвращает ту же operation" -- one
    // domain effect and one OPERATION_CREATED event no matter how many times the
    // same key+payload is replayed.
    [Fact]
    public async Task ReplayWithSameKeyAndPayload_ReturnsSameOperation_WithOneEvent()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");
        var idempotencyKey = $"idem-replay-{Guid.NewGuid():N}";

        var first = await client.PostActionAsync("/api/payment/request", token, ExecutionPayload.DeepClone(), idempotencyKey);
        var second = await client.PostActionAsync("/api/payment/request", token, ExecutionPayload.DeepClone(), idempotencyKey);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);

        // contract-reference matrix: "Повтор с тем же key и payload -> Исходный
        // успешный envelope". The whole envelope, meta included -- a replay hands
        // back what the first call returned rather than a lookalike stamped with a
        // fresh correlationId.
        Assert.Equal(first.Body.ToJsonString(), second.Body.ToJsonString());

        var operationId = Guid.Parse(first.Body["result"]!["operationId"]!.GetValue<string>());

        var eventCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.operation_events where operation_id = @operationId",
            ("operationId", operationId));

        Assert.Equal(1, eventCount);
    }

    // The stored envelope is keyed on a hash of the payload, so a replay that
    // differs only in property order or whitespace is still the same request and
    // replays rather than executing again.
    [Fact]
    public async Task ReplayWithReorderedPayload_ReplaysTheOriginalEnvelope()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");
        var idempotencyKey = $"idem-canonical-{Guid.NewGuid():N}";

        var first = await client.PostRawActionAsync(
            "/api/payment/request", token,
            """{"operationKind":"PAYMENT_EXECUTION","amount":"10.00","currency":"RUB"}""", idempotencyKey);
        var second = await client.PostRawActionAsync(
            "/api/payment/request", token,
            """{ "currency" : "RUB", "amount" : "10.00", "operationKind" : "PAYMENT_EXECUTION" }""", idempotencyKey);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.Body.ToJsonString(), second.Body.ToJsonString());

        var operationId = Guid.Parse(first.Body["result"]!["operationId"]!.GetValue<string>());

        var eventCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.operation_events where operation_id = @operationId",
            ("operationId", operationId));

        Assert.Equal(1, eventCount);
    }

    // contract-reference: "Тот же key с другим payload возвращает 409
    // idempotency.conflict" -- and the first request's effect stays the only one.
    [Fact]
    public async Task ReplayWithSameKeyDifferentPayload_Returns409_LeavesStateUnchanged()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");
        var idempotencyKey = $"idem-conflict-{Guid.NewGuid():N}";

        var first = await client.PostActionAsync("/api/payment/request", token, ExecutionPayload.DeepClone(), idempotencyKey);
        var second = await client.PostActionAsync(
            "/api/payment/request",
            token,
            new JsonObject { ["operationKind"] = "PAYMENT_EXECUTION", ["amount"] = "99.00", ["currency"] = "RUB" },
            idempotencyKey);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(409, second.StatusCode);
        Assert.Equal("idempotency.conflict", second.Body["code"]!.GetValue<string>());

        var operationCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from core.operations where request_id = @requestId",
            ("requestId", idempotencyKey));

        Assert.Equal(1, operationCount);
    }
}
