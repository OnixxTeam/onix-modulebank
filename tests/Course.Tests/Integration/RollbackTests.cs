using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class RollbackTests(ApiFactory factory)
{
    // contract-reference matrix: "Target вернул status=error -> Полный rollback
    // -> Canary отсутствует, dispatch содержит техническую ошибку." The fixture
    // target (testfixture.error_after_insert_v1) writes a canary row and then
    // reports a controlled business failure in the same call.
    //
    // Migration 015's api.invoke rolls the domain effect back to a savepoint on
    // this branch, and PostgresActionExecutor commits the outer transaction
    // instead of discarding it -- so the canary disappears but the dispatch row
    // survives as the technical audit trail. See
    // .scratch/findings-dispatch-rollback-gap.md.
    [Fact]
    public async Task TargetStatusError_RollsBackTheWrite_ButKeepsTheDispatchRow()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var response = await client.PostActionAsync("/api/testfixture/error", token, new JsonObject());

        // business.rejected is outside ActionError's fixed HTTP matrix, but it is a
        // controlled domain rejection: it keeps its own code at 400 instead of
        // being reported as an unexpected runtime failure.
        Assert.Equal(400, response.StatusCode);
        Assert.Equal("business.rejected", response.Body["code"]!.GetValue<string>());
        Assert.Equal(
            "fixture forced rejection after a domain write", response.Body["message"]!.GetValue<string>());

        var correlationId = Guid.Parse(response.Body["meta"]!["correlationId"]!.GetValue<string>());

        var canaryExists = await factory.Postgres.ScalarAsOwnerAsync<bool>(
            "select exists(select 1 from testfixture.canary_marks where correlation_id = @correlationId)",
            ("correlationId", correlationId));

        Assert.False(canaryExists);

        var dispatchStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from api.action_dispatches where correlation_id = @correlationId",
            ("correlationId", correlationId));

        Assert.Equal("ERROR", dispatchStatus);
    }

    // contract-reference matrix: "Неизвестный outcome или invalid result ->
    // 500 action.contract_violation -> Полный rollback -> Canary отсутствует,
    // успешный idempotency result не сохранён." A replay with the same
    // Idempotency-Key must come back through the identical 500 path, not a
    // stored envelope or a 409 -- proving the claim row never survived.
    [Fact]
    public async Task UnknownOutcome_RollsBackTheWrite_AndNeverPersistsTheIdempotencyKey()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");
        var idempotencyKey = $"unknown-outcome-{Guid.NewGuid():N}";

        var first = await client.PostActionAsync("/api/testfixture/unknown_outcome", token, new JsonObject(), idempotencyKey);
        var second = await client.PostActionAsync("/api/testfixture/unknown_outcome", token, new JsonObject(), idempotencyKey);

        Assert.Equal(500, first.StatusCode);
        Assert.Equal("action.contract_violation", first.Body["code"]!.GetValue<string>());
        Assert.Equal(500, second.StatusCode);
        Assert.Equal("action.contract_violation", second.Body["code"]!.GetValue<string>());

        var canaryCount = await factory.Postgres.ScalarAsOwnerAsync<long>("select count(*) from testfixture.canary_marks");

        var idempotencyKeyStored = await factory.Postgres.ScalarAsOwnerAsync<bool>(
            """
            select exists(
              select 1 from api.idempotency_keys
              where module = 'testfixture' and action = 'unknown_outcome' and request_id = @requestId
            )
            """,
            ("requestId", idempotencyKey));

        Assert.False(idempotencyKeyStored);
        Assert.True(canaryCount == 0, "both calls wrote a canary row, but each must have rolled back on its own");
    }
}
