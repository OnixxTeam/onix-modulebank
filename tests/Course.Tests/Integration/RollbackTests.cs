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
    // Only the canary half is asserted here: PostgresActionExecutor never calls
    // CommitAsync on this branch, so api.action_dispatches' insert (written by
    // api.invoke inside the same transaction) rolls back too and never survives
    // to be checked -- contradicting the matrix's "dispatch содержит техническую
    // ошибку" half. See .scratch/findings-dispatch-rollback-gap.md.
    [Fact]
    public async Task TargetStatusError_RollsBackTheWrite()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var response = await client.PostActionAsync("/api/testfixture/error", token, new JsonObject());

        // business.rejected is outside ActionError's fixed HTTP matrix, so it
        // collapses to internal.error/500 rather than inventing a status.
        Assert.Equal(500, response.StatusCode);
        Assert.Equal("internal.error", response.Body["code"]!.GetValue<string>());

        var correlationId = Guid.Parse(response.Body["meta"]!["correlationId"]!.GetValue<string>());

        var canaryExists = await factory.Postgres.ScalarAsOwnerAsync<bool>(
            "select exists(select 1 from testfixture.canary_marks where correlation_id = @correlationId)",
            ("correlationId", correlationId));

        Assert.False(canaryExists);
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
