using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Course.Tests.Integration;

// Reproduces every workflow.probe mode over the network (issue 05's own
// acceptance test), and proves the domain effect is idempotent by its own
// effect key rather than by the idempotency-keys claim api.invoke also
// keeps -- a second call with the same key must leave exactly one row in
// workflow_probe.effect even where the first call's mode differs.
[Collection("integration")]
public sealed class WorkflowProbeActionTests(ApiFactory factory)
{
    private static JsonObject Payload(string mode, string value) => new JsonObject
    {
        ["mode"] = mode,
        ["value"] = value,
    };

    private static string NewKey() => $"probe-{Guid.NewGuid():N}";

    [Fact]
    public async Task Success_ReturnsDeclaredOutcomeAndStoresEffect()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");
        var key = NewKey();

        var response = await client.PostActionAsync("/api/workflow/probe", token, Payload("SUCCESS", "one"), key);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("SUCCEEDED", response.Body["outcome"]!.GetValue<string>());
        Assert.Equal(key, response.Body["result"]!["effectKey"]!.GetValue<string>());
        Assert.True(response.Body["result"]!["stored"]!.GetValue<bool>());

        var stored = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select business_value from workflow_probe.effect where effect_key = @key", ("key", key));
        Assert.Equal("one", stored);
    }

    [Fact]
    public async Task Manual_RoutesToTheDeclaredManualOutcome()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");

        var response = await client.PostActionAsync("/api/workflow/probe", token, Payload("MANUAL", "two"), NewKey());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ROUTE_MANUAL", response.Body["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task RetryableError_ReturnsAControlledEnvelopeMarkedRetryable()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");

        var response = await client.PostActionAsync("/api/workflow/probe", token, Payload("RETRYABLE_ERROR", "three"), NewKey());

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("workflow_probe.retryable", response.Body["code"]!.GetValue<string>());
        Assert.True(response.Body["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PermanentError_ReturnsAControlledEnvelopeNotMarkedRetryable()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");

        var response = await client.PostActionAsync("/api/workflow/probe", token, Payload("PERMANENT_ERROR", "four"), NewKey());

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("workflow_probe.permanent", response.Body["code"]!.GetValue<string>());
        Assert.False(response.Body["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UndeclaredOutcome_IsRejectedAsAContractViolation()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");

        var response = await client.PostActionAsync("/api/workflow/probe", token, Payload("UNDECLARED_OUTCOME", "five"), NewKey());

        Assert.Equal(500, response.StatusCode);
        Assert.Equal("action.contract_violation", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidResult_IsRejectedAsAContractViolation()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");

        var response = await client.PostActionAsync("/api/workflow/probe", token, Payload("INVALID_RESULT", "six"), NewKey());

        Assert.Equal(500, response.StatusCode);
        Assert.Equal("action.contract_violation", response.Body["code"]!.GetValue<string>());
    }

    // The trap ticket 05 calls out by name: idempotent by effect key, not by
    // attempt. Two calls, same key, different payload values -- api.invoke's
    // idempotency-keys claim replays the first envelope verbatim (same
    // payload hash requirement aside, this uses an identical payload so the
    // replay path is the one under test), and workflow_probe.effect still
    // holds exactly the first call's row.
    [Fact]
    public async Task ReplayWithSameEffectKey_StoresExactlyOneRow()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "workflow:execute");
        var key = NewKey();
        var payload = Payload("SUCCESS", "seven");

        var first = await client.PostActionAsync("/api/workflow/probe", token, payload.DeepClone(), key);
        var second = await client.PostActionAsync("/api/workflow/probe", token, payload.DeepClone(), key);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.Body.ToJsonString(), second.Body.ToJsonString());

        var rowCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow_probe.effect where effect_key = @key", ("key", key));
        Assert.Equal(1, rowCount);
    }

    // The previous test can pass even with workflow_probe.effect's ON CONFLICT
    // clause deleted entirely: api.invoke's own idempotency-keys claim matches
    // on the identical payload and replays the stored envelope without
    // probe_v1 running a second time at all. This test calls probe_v1
    // directly -- bypassing api.invoke, its claim table and its replay path
    // -- with two different payloads under the same effect key, so the
    // primary key on workflow_probe.effect is the only thing that can be
    // stopping the second insert.
    [Fact]
    public async Task Effect_IsIdempotentByItsOwnPrimaryKey_IndependentOfApiInvokesReplay()
    {
        var key = NewKey();

        await CallProbeDirectlyAsync(key, "SUCCESS", "first-direct-call");
        await CallProbeDirectlyAsync(key, "SUCCESS", "second-direct-call");

        var rowCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow_probe.effect where effect_key = @key", ("key", key));
        Assert.Equal(1, rowCount);

        var stored = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select business_value from workflow_probe.effect where effect_key = @key", ("key", key));
        Assert.Equal("first-direct-call", stored);
    }

    private Task<int> CallProbeDirectlyAsync(string effectKey, string mode, string value) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select workflow_probe.probe_v1(@context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["requestId"] = effectKey }.ToJsonString(),
            });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = Payload(mode, value).ToJsonString(),
            });

            await command.ExecuteScalarAsync();
            return 0;
        });
}
