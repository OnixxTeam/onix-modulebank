using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class PolicyTests(ApiFactory factory)
{
    // contract-reference matrix: "Недостаточная policy -> 403 access.denied ->
    // Target не вызывается -> Отказ подтверждён на HTTP-границе и в
    // api.invoke." Both boundaries are checked independently: the HTTP path
    // (PostgresActionExecutor's own manifest.RequiredPolicy check) and a direct
    // SQL call into api.invoke with a context that carries no scopes at all,
    // bypassing the HTTP layer entirely -- the "denied-client" persona from
    // contract-reference has an empty scope claim for exactly this case.
    [Fact]
    public async Task InsufficientPolicy_IsRefusedAtBothBoundaries()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("denied-client", "test", "");

        var httpResponse = await client.PostActionAsync(
            "/api/payment/request",
            token,
            new JsonObject { ["operationKind"] = "PAYMENT_EXECUTION", ["amount"] = "10.00", ["currency"] = "RUB" },
            idempotencyKey: $"policy-denied-{Guid.NewGuid():N}"); // payment.request requires this header before its policy check even runs

        Assert.Equal(403, httpResponse.StatusCode);
        Assert.Equal("access.denied", httpResponse.Body["code"]!.GetValue<string>());

        var directInvokeCode = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select api.invoke('payment', 'request', null, @context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb)
            {
                Value = """{"principal":"denied-client","consumer":"test","scopes":[],"correlationId":"22222222-2222-2222-2222-222222222222","requestId":null}""",
            });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = """{"operationKind":"PAYMENT_EXECUTION","amount":"10.00","currency":"RUB"}""",
            });

            var raw = (string)(await command.ExecuteScalarAsync())!;
            return JsonNode.Parse(raw)!["code"]!.GetValue<string>();
        });

        Assert.Equal("access.denied", directInvokeCode);
    }

    // contract-reference matrix: "Неизвестная или выключенная версия -> 404
    // action.not_found -> Target не вызывается -> В dispatch нет успешного
    // предметного вызова." operation.get has exactly one published version, so
    // disabling it (no replacement needed -- nothing else is enabled) leaves
    // the route entirely unresolvable.
    [Fact]
    public async Task DisabledVersion_Returns404_NeverReachesTarget()
    {
        var dispatchCountBefore = await CountOperationGetDispatchesAsync();

        var disableResult = await factory.Postgres.Publisher.DisableAsync("operation", "get", 1, null, CancellationToken.None);
        Assert.Equal("ok", disableResult["status"]!.GetValue<string>());

        try
        {
            using var client = factory.CreateClient();
            var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

            var response = await client.PostActionAsync(
                "/api/operation/get", token, new JsonObject { ["operationId"] = Guid.NewGuid().ToString() });

            Assert.Equal(404, response.StatusCode);
            Assert.Equal("action.not_found", response.Body["code"]!.GetValue<string>());

            var dispatchCountAfter = await CountOperationGetDispatchesAsync();
            Assert.Equal(dispatchCountBefore, dispatchCountAfter);
        }
        finally
        {
            // Re-enable so a later test run against the same container (or a
            // future test added to this class) still finds operation.get usable.
            await factory.Postgres.Publisher.ActivateAsync("operation", "get", 1, CancellationToken.None);
        }
    }

    private Task<long> CountOperationGetDispatchesAsync() =>
        factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from api.action_dispatches where module = 'operation' and action = 'get'");
}
