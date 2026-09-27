using System.Text;
using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Core.Diagnostics;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Course.Tests.Integration;

// Ticket 13 (week 4): the two failpoints `api` owns, on the generic action seam,
// plus the gate's own rules.
//
// The seam is deliberately nameless. The gate is reached on every successful
// action, so what is under test is that the pre-commit stop leaves nothing behind
// and the post-commit stop leaves everything behind -- which is the whole content
// of the published commit-boundary invariants, independent of which action was
// stopped.
[Collection("integration")]
public sealed class FailpointSeamTests : IDisposable
{
    private readonly ApiFactory factory;
    private readonly NpgsqlDataSource runtimeDataSource;

    public FailpointSeamTests(ApiFactory factory)
    {
        this.factory = factory;
        runtimeDataSource = NpgsqlDataSource.Create(factory.Postgres.RuntimeConnectionString);
    }

    public void Dispose() => runtimeDataSource.Dispose();

    [Fact]
    public void TheSixPublishedNamesAreTheOnesTheGateKnows()
    {
        Assert.Equal(
            [
                "after_job_claim", "after_action_before_finish", "after_outbox_claim",
                "after_provider_response", "after_inbox_saved", "after_manual_decision",
            ],
            Failpoints.Names);
    }

    [Fact]
    public async Task AnUnknownNameDisarmsEveryBoundary()
    {
        var output = new StringWriter();
        var gate = new FailpointGate("after_everything_explodes", output);

        // Would block forever if it were armed; the token is only insurance.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var name in Failpoints.Names)
        {
            await gate.ReachAsync(name, "api", cancellation.Token);
        }

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task OnlyTheOneArmedNameFires()
    {
        var output = new StringWriter();
        var gate = new FailpointGate(Failpoints.AfterInboxSaved, output);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        foreach (var name in Failpoints.Names.Where(name => name != Failpoints.AfterInboxSaved))
        {
            await gate.ReachAsync(name, "api", cancellation.Token);
        }

        Assert.Equal(string.Empty, output.ToString());

        var reached = gate.ReachAsync(Failpoints.AfterInboxSaved, "api", cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await Task.Delay(-1, cancellation.Token);
            await reached;
        });

        var ack = JsonNode.Parse(output.ToString().Trim())!;
        Assert.Equal("failpoint.reached", ack["event"]!.GetValue<string>());
        Assert.Equal("after_inbox_saved", ack["name"]!.GetValue<string>());
        Assert.Equal("api", ack["instanceId"]!.GetValue<string>());
        Assert.Equal(
            AckFields,
            ack.AsObject().Select(property => property.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    // "Production profile ignores failpoints" is not an armed gate that declines
    // to fire: without COURSE_TEST_PROFILE=1 no gate is built at all.
    [Theory]
    [InlineData(null, "after_inbox_saved")]
    [InlineData("", "after_inbox_saved")]
    [InlineData("0", "after_inbox_saved")]
    [InlineData("true", "after_inbox_saved")]
    public void OutsideTheTestProfileTheGateIsTheNoopOne(string? profile, string armed)
    {
        using var scope = new EnvironmentScope(("COURSE_TEST_PROFILE", profile), ("COURSE_FAILPOINT", armed));

        Assert.Same(NoopFailpointGate.Instance, Failpoints.FromEnvironment(new StringWriter()));
    }

    [Fact]
    public void InsideTheTestProfileTheGateIsTheRealOne()
    {
        using var scope = new EnvironmentScope(("COURSE_TEST_PROFILE", "1"), ("COURSE_FAILPOINT", "after_manual_decision"));

        Assert.IsType<FailpointGate>(Failpoints.FromEnvironment(new StringWriter()));
    }

    // The pre-commit boundary. Stopping here must lose the effect, the dispatch
    // row and the successful idempotency result together, and the command must be
    // replayable afterwards with the same key -- without anyone deleting that key
    // by hand.
    [Fact]
    public async Task StoppingBeforeCommitRollsTheWholeCommandBackAndLeavesItReplayable()
    {
        var value = $"failpoint-api-{Guid.NewGuid():N}";
        var key = $"failpoint-key-{Guid.NewGuid():N}";
        var payload = new JsonObject { ["mode"] = "SUCCESS", ["value"] = value };

        var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var executor = new PostgresActionExecutor(
            runtimeDataSource,
            factory.Services.GetRequiredService<ISchemaValidator>(),
            TimeProvider.System,
            new FailpointGate(Failpoints.AfterManualDecision, output),
            "api");

        var blocked = executor.ExecuteAsync(
            "workflow", "probe", null, Claims(), Guid.NewGuid(), key, payload.DeepClone(), cancellation.Token);

        await WaitForAckAsync(output);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        Assert.Equal(0, await ProbeEffectCountAsync(value));
        Assert.Equal(0, await DispatchCountAsync(key));

        // The key row may exist as a claim, but it must carry no successful
        // envelope -- otherwise the replay below would return a result for work
        // that never happened.
        Assert.Null(await StoredEnvelopeOutcomeAsync(key));

        using var client = factory.CreateClient();
        var replay = await client.PostActionAsync(
            "/api/workflow/probe", TokenText(), payload.DeepClone(), idempotencyKey: key);

        Assert.Equal(200, replay.StatusCode);
        Assert.Equal(1, await ProbeEffectCountAsync(value));
    }

    // The post-commit boundary. Stopping here loses the HTTP response and keeps
    // everything the action wrote, readable through the read-only projections.
    [Fact]
    public async Task StoppingAfterCommitKeepsTheRecordAndOnlyLosesTheResponse()
    {
        var value = $"failpoint-api-{Guid.NewGuid():N}";
        var key = $"failpoint-key-{Guid.NewGuid():N}";
        var payload = new JsonObject { ["mode"] = "SUCCESS", ["value"] = value };

        var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var executor = new PostgresActionExecutor(
            runtimeDataSource,
            factory.Services.GetRequiredService<ISchemaValidator>(),
            TimeProvider.System,
            new FailpointGate(Failpoints.AfterInboxSaved, output),
            "api");

        var blocked = executor.ExecuteAsync(
            "workflow", "probe", null, Claims(), Guid.NewGuid(), key, payload.DeepClone(), cancellation.Token);

        await WaitForAckAsync(output);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        Assert.Equal(1, await ProbeEffectCountAsync(value));
        Assert.Equal(1, await DispatchCountAsync(key));
        Assert.Equal("SUCCEEDED", await StoredEnvelopeOutcomeAsync(key));

        // And the repeat with the same key adds no second effect: it is served
        // from the stored envelope the commit left behind.
        using var client = factory.CreateClient();
        var replay = await client.PostActionAsync(
            "/api/workflow/probe", TokenText(), payload.DeepClone(), idempotencyKey: key);

        Assert.Equal(200, replay.StatusCode);
        Assert.Equal(1, await ProbeEffectCountAsync(value));
    }

    private static readonly string[] AckFields = ["event", "instanceId", "name"];

    private static async Task WaitForAckAsync(StringWriter output)
    {
        for (var i = 0; i < 200; i++)
        {
            if (output.ToString().Contains("failpoint.reached", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the failpoint never acknowledged");
    }

    private static VerifiedClaims Claims() =>
        new("candidate-client", "web", ["workflow:execute", "workflow:read", "payment:internal"]);

    private static string TokenText() =>
        ApiFactory.CreateToken("candidate-client", "web", "workflow:execute workflow:read payment:internal");

    private async Task<int> ProbeEffectCountAsync(string value) =>
        (int)await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow_probe.effect where business_value = @value", ("value", value));

    private async Task<int> DispatchCountAsync(string requestId) =>
        (int)await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from api.action_dispatches where request_id = @requestId", ("requestId", requestId));

    private Task<string?> StoredEnvelopeOutcomeAsync(string requestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select envelope ->> 'outcome' from api.idempotency_keys where request_id = @requestId", connection);
            command.Parameters.AddWithValue("requestId", requestId);
            var value = await command.ExecuteScalarAsync();
            return value is DBNull or null ? null : (string)value;
        });

    // Restores whatever the variables held before, so the two profile tests do not
    // leak a test profile into the rest of the run.
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly List<(string Name, string? Value)> previous = [];

        public EnvironmentScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                previous.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
