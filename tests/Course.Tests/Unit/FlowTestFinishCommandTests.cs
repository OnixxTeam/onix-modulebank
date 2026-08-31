using System.Text.Json.Nodes;
using Course.Cli;
using Course.Cli.Commands;
using Workflow.Core.Runtime;

namespace Course.Tests.Unit;

// Ticket 07's own acceptance criteria for the fencing probe -- gated by test
// profile, and a stale request giving a non-zero exit with the declared code
// -- are about the CLI command's plumbing, not workflow.finish_job itself
// (FinishJobGraphTestFinishTests already proves that boundary against real
// PostgreSQL). Both are provable without a database: FlowTestFinishCommand
// takes testProfileEnabled as a plain bool and IWorkflowJobRuntime as an
// interface, so a fake stands in for the SQL call it would otherwise make.
public sealed class FlowTestFinishCommandTests
{
    [Fact]
    public async Task ProfileDisabled_ReturnsUnknownCommand_WithoutTouchingTheRuntime()
    {
        var runtime = new FakeWorkflowJobRuntime(_ => throw new InvalidOperationException("must not be called"));
        var command = new ParsedCommand("flow test-finish", [], new Dictionary<string, string>());

        var envelope = await FlowTestFinishCommand.ExecuteAsync(command, runtime, testProfileEnabled: false, CancellationToken.None);

        Assert.Equal("error", envelope.Status);
        Assert.Equal("command.unknown", envelope.Code);
    }

    [Fact]
    public async Task ProfileEnabled_StaleFinish_ReturnsErrorStatus_WithTheDeclaredCode()
    {
        var jobId = Guid.NewGuid();
        var resultPath = WriteResultFile(new JsonObject());
        var runtime = new FakeWorkflowJobRuntime(_ => JsonNode.Parse(
            """{"status":"error","code":"workflow.lease_stale","message":"job is not leased to this owner at this lease version"}""")!);

        var command = BuildCommand(jobId, "stale-owner", "1", "SOME_OUTCOME", resultPath);

        var envelope = await FlowTestFinishCommand.ExecuteAsync(command, runtime, testProfileEnabled: true, CancellationToken.None);

        // Program.cs's exit code is `envelope.Status == "ok" ? 0 : 1` -- "error"
        // here is exactly what drives the non-zero exit the ticket asks for.
        Assert.Equal("error", envelope.Status);
        Assert.Equal("workflow.lease_stale", envelope.Code);
    }

    [Fact]
    public async Task ProfileEnabled_MatchingFinish_ReturnsOk_AndPassesArgumentsThrough()
    {
        var jobId = Guid.NewGuid();
        var resultPath = WriteResultFile(new JsonObject { ["echo"] = "value" });
        (Guid JobId, string Owner, int LeaseVersion, string Outcome, JsonNode Result)? captured = null;

        var runtime = new FakeWorkflowJobRuntime(call =>
        {
            captured = call;
            return JsonNode.Parse($$"""{"status":"ok","jobId":"{{call.JobId}}","outcome":"{{call.Outcome}}"}""")!;
        });

        var command = BuildCommand(jobId, "owner-a", "3", "DONE_OUTCOME", resultPath);

        var envelope = await FlowTestFinishCommand.ExecuteAsync(command, runtime, testProfileEnabled: true, CancellationToken.None);

        Assert.Equal("ok", envelope.Status);
        Assert.NotNull(captured);
        Assert.Equal(jobId, captured!.Value.JobId);
        Assert.Equal("owner-a", captured.Value.Owner);
        Assert.Equal(3, captured.Value.LeaseVersion);
        Assert.Equal("DONE_OUTCOME", captured.Value.Outcome);
        Assert.Equal("value", captured.Value.Result["echo"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProfileEnabled_MissingOwner_ReturnsRequestInvalid()
    {
        var runtime = new FakeWorkflowJobRuntime(_ => throw new InvalidOperationException("must not be called"));
        var command = new ParsedCommand(
            "flow test-finish",
            [Guid.NewGuid().ToString()],
            new Dictionary<string, string> { ["lease-version"] = "1", ["outcome"] = "X", ["result"] = WriteResultFile(new JsonObject()) });

        var envelope = await FlowTestFinishCommand.ExecuteAsync(command, runtime, testProfileEnabled: true, CancellationToken.None);

        Assert.Equal("error", envelope.Status);
        Assert.Equal("request.invalid", envelope.Code);
    }

    private static ParsedCommand BuildCommand(Guid jobId, string owner, string leaseVersion, string outcome, string resultPath) =>
        new(
            "flow test-finish",
            [jobId.ToString()],
            new Dictionary<string, string>
            {
                ["owner"] = owner,
                ["lease-version"] = leaseVersion,
                ["outcome"] = outcome,
                ["result"] = resultPath,
            });

    private static string WriteResultFile(JsonNode content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-test-finish-result-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content.ToJsonString());
        return path;
    }

    private sealed class FakeWorkflowJobRuntime(
        Func<(Guid JobId, string Owner, int LeaseVersion, string Outcome, JsonNode Result), JsonNode> onFinish) : IWorkflowJobRuntime
    {
        public Task<JsonNode> ClaimAsync(string owner, int leaseMs, CancellationToken cancellationToken) =>
            throw new NotSupportedException("flow test-finish never claims");

        public Task<JsonNode> FinishAsync(
            Guid jobId, string owner, int leaseVersion, string outcome, JsonNode result, CancellationToken cancellationToken) =>
            Task.FromResult(onFinish((jobId, owner, leaseVersion, outcome, result)));
    }
}
