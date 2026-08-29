using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Infrastructure.Postgres;
using Course.Infrastructure.Validation;
using Npgsql;

namespace Course.Tests.Integration;

// Exercises PostgresActionExecutor.ExecuteInTransactionAsync directly: the seam
// both week 1's HTTP path and a future workflow worker call into. The pipeline
// itself must never commit or roll back -- that stays the transaction owner's
// call -- and must carry a caller's workflow identifiers and timeout override
// through to the target, while the HTTP path keeps behaving exactly as before.
[Collection("integration")]
public sealed class ExecutorTransactionTests(ApiFactory factory)
{
    private static readonly VerifiedClaims Claims = new("candidate-client", "web", ["payment:write", "payment:read"]);

    [Fact]
    public async Task Pipeline_NeverCommitsOrRollsBackItself()
    {
        await using var dataSource = NpgsqlDataSource.Create(factory.Postgres.RuntimeConnectionString);
        var executor = CreateExecutor(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var correlationId = Guid.NewGuid();
        var outcome = await executor.ExecuteInTransactionAsync(
            connection, transaction, "testfixture", "echo_context", null, Claims, correlationId, null,
            new JsonObject(), callerContext: null, CancellationToken.None);

        Assert.True(outcome.Result.IsSuccess);
        Assert.False(outcome.TargetReturnedError);

        // Roll back deliberately: if the pipeline had committed on its own, this
        // dispatch row would survive regardless of what we do here.
        await transaction.RollbackAsync();

        var dispatchExists = await factory.Postgres.ScalarAsOwnerAsync<bool>(
            "select exists(select 1 from api.action_dispatches where correlation_id = @correlationId)",
            ("correlationId", correlationId));

        Assert.False(dispatchExists);
    }

    [Fact]
    public async Task TargetReturnedError_LeavesCommitDecisionToTheCaller()
    {
        await using var dataSource = NpgsqlDataSource.Create(factory.Postgres.RuntimeConnectionString);
        var executor = CreateExecutor(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var correlationId = Guid.NewGuid();
        var outcome = await executor.ExecuteInTransactionAsync(
            connection, transaction, "testfixture", "error", null, Claims, correlationId, null,
            new JsonObject(), callerContext: null, CancellationToken.None);

        Assert.False(outcome.Result.IsSuccess);
        Assert.True(outcome.TargetReturnedError);

        // A worker that decides to retry instead of keeping the technical dispatch
        // row rolls back here -- the pipeline must not have pre-empted that choice.
        await transaction.RollbackAsync();

        var dispatchExists = await factory.Postgres.ScalarAsOwnerAsync<bool>(
            "select exists(select 1 from api.action_dispatches where correlation_id = @correlationId)",
            ("correlationId", correlationId));

        Assert.False(dispatchExists);
    }

    [Fact]
    public async Task WorkflowIdentifiers_ReachTheTargetOnlyWhenTheCallerSetsThem()
    {
        await using var dataSource = NpgsqlDataSource.Create(factory.Postgres.RuntimeConnectionString);
        var executor = CreateExecutor(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var processId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var executionId = $"execution-{Guid.NewGuid():N}";
        var callerContext = new ActionCallerContext(processId, jobId, executionId, attemptId);

        var outcome = await executor.ExecuteInTransactionAsync(
            connection, transaction, "testfixture", "echo_context", null, Claims, Guid.NewGuid(), null,
            new JsonObject(), callerContext, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.True(outcome.Result.IsSuccess);
        var echoedContext = outcome.Result.Result!;
        Assert.Equal(processId.ToString(), echoedContext["processId"]!.GetValue<string>());
        Assert.Equal(jobId.ToString(), echoedContext["jobId"]!.GetValue<string>());
        Assert.Equal(executionId, echoedContext["executionId"]!.GetValue<string>());
        Assert.Equal(attemptId.ToString(), echoedContext["attemptId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TimeoutOverride_OutranksTheManifest()
    {
        await using var dataSource = NpgsqlDataSource.Create(factory.Postgres.RuntimeConnectionString);
        var executor = CreateExecutor(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // testfixture.echo_context's manifest declares timeout_ms 5000; only a
        // 60s override actually reaching the pipeline explains a deadline this far out.
        var callerContext = new ActionCallerContext(TimeoutOverride: TimeSpan.FromSeconds(60));
        var before = DateTimeOffset.UtcNow;

        var outcome = await executor.ExecuteInTransactionAsync(
            connection, transaction, "testfixture", "echo_context", null, Claims, Guid.NewGuid(), null,
            new JsonObject(), callerContext, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.True(outcome.Result.IsSuccess);
        var deadline = DateTimeOffset.Parse(
            outcome.Result.Result!["deadline"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(deadline > before.AddSeconds(30), "deadline should reflect the 60s override, not the manifest's 5s timeout");
    }

    [Fact]
    public async Task NetworkPath_NeverSendsWorkflowIdentifiers()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var response = await client.PostActionAsync("/api/testfixture/echo_context", token, new JsonObject());

        Assert.Equal(200, response.StatusCode);
        var echoedContext = response.Body["result"]!.AsObject();
        Assert.False(echoedContext.ContainsKey("processId"));
        Assert.False(echoedContext.ContainsKey("jobId"));
        Assert.False(echoedContext.ContainsKey("executionId"));
        Assert.False(echoedContext.ContainsKey("attemptId"));
    }

    // A fresh executor over its own JsonSchemaValidator instance: every schema
    // these tests validate against (GenericObjectSchema) declares no $id, so a
    // second validator instance never collides with the app host's registrations.
    private static PostgresActionExecutor CreateExecutor(NpgsqlDataSource dataSource) =>
        new(dataSource, new JsonSchemaValidator(), TimeProvider.System);
}
