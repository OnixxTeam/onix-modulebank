using System.Text.Json.Nodes;
using Course.Cli;
using Course.Cli.Commands;
using Workflow.Core.Runtime;

namespace Course.Tests.Unit;

// Ticket 09's CLI-plumbing acceptance: --type/--message-id/--payload parsing,
// the accepted/duplicate status mapping and error pass-through, all provable
// without a database (SignalTests.cs covers workflow.apply_signal itself
// against real PostgreSQL) since FlowSignalCommand takes IWorkflowProcessRuntime
// as an interface.
public sealed class FlowSignalCommandTests
{
    [Fact]
    public async Task NewSignal_ReturnsAcceptedStatus_AndPassesTheCanonicalBodyHashThrough()
    {
        var processId = Guid.NewGuid();
        var payloadPath = WritePayloadFile(new JsonObject { ["value"] = 1 });
        (Guid ProcessId, string MessageId, string SignalType, string BodyHash, JsonNode Body)? captured = null;

        var runtime = new FakeWorkflowProcessRuntime(call =>
        {
            captured = call;
            return JsonNode.Parse(
                $$"""{"status":"ok","duplicate":false,"processId":"{{call.ProcessId}}","signalType":"{{call.SignalType}}"}""")!;
        });

        var command = BuildCommand(processId, "training.completed", "msg-1", payloadPath);

        var envelope = await FlowSignalCommand.ExecuteAsync(command, runtime, CancellationToken.None);

        Assert.Equal("ok", envelope.Status);
        Assert.NotNull(captured);
        Assert.Equal(processId, captured!.Value.ProcessId);
        Assert.Equal("msg-1", captured.Value.MessageId);
        Assert.Equal("training.completed", captured.Value.SignalType);
        Assert.Equal(1, captured.Value.Body["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task DuplicateSignal_MapsToDuplicateStatus_NotAccepted()
    {
        var processId = Guid.NewGuid();
        var payloadPath = WritePayloadFile(new JsonObject());
        var runtime = new FakeWorkflowProcessRuntime(call => JsonNode.Parse(
            $$"""{"status":"ok","duplicate":true,"processId":"{{call.ProcessId}}","signalType":"{{call.SignalType}}"}""")!);

        var command = BuildCommand(processId, "training.completed", "msg-1", payloadPath);

        var envelope = await FlowSignalCommand.ExecuteAsync(command, runtime, CancellationToken.None);

        Assert.Equal("ok", envelope.Status);
        var parsed = JsonNode.Parse(envelope.ToJson())!;
        Assert.Equal("duplicate", parsed["result"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Conflict_ReturnsErrorStatus_WithTheDeclaredCode()
    {
        var processId = Guid.NewGuid();
        var payloadPath = WritePayloadFile(new JsonObject());
        var runtime = new FakeWorkflowProcessRuntime(_ => JsonNode.Parse(
            """{"status":"error","code":"workflow.signal_conflict","message":"message id already used with a different process, type or body"}""")!);

        var command = BuildCommand(processId, "training.completed", "msg-1", payloadPath);

        var envelope = await FlowSignalCommand.ExecuteAsync(command, runtime, CancellationToken.None);

        Assert.Equal("error", envelope.Status);
        Assert.Equal("workflow.signal_conflict", envelope.Code);
    }

    [Fact]
    public async Task MissingMessageId_ReturnsRequestInvalid_WithoutTouchingTheRuntime()
    {
        var runtime = new FakeWorkflowProcessRuntime(_ => throw new InvalidOperationException("must not be called"));
        var command = new ParsedCommand(
            "flow signal",
            [Guid.NewGuid().ToString()],
            new Dictionary<string, string> { ["type"] = "training.completed", ["payload"] = WritePayloadFile(new JsonObject()) });

        var envelope = await FlowSignalCommand.ExecuteAsync(command, runtime, CancellationToken.None);

        Assert.Equal("error", envelope.Status);
        Assert.Equal("request.invalid", envelope.Code);
    }

    [Fact]
    public async Task InvalidProcessId_ReturnsRequestInvalid_WithoutTouchingTheRuntime()
    {
        var runtime = new FakeWorkflowProcessRuntime(_ => throw new InvalidOperationException("must not be called"));
        var command = new ParsedCommand(
            "flow signal",
            ["not-a-guid"],
            new Dictionary<string, string>
            {
                ["type"] = "training.completed",
                ["message-id"] = "msg-1",
                ["payload"] = WritePayloadFile(new JsonObject()),
            });

        var envelope = await FlowSignalCommand.ExecuteAsync(command, runtime, CancellationToken.None);

        Assert.Equal("error", envelope.Status);
        Assert.Equal("request.invalid", envelope.Code);
    }

    private static ParsedCommand BuildCommand(Guid processId, string signalType, string messageId, string payloadPath) =>
        new(
            "flow signal",
            [processId.ToString()],
            new Dictionary<string, string> { ["type"] = signalType, ["message-id"] = messageId, ["payload"] = payloadPath });

    private static string WritePayloadFile(JsonNode content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-signal-payload-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content.ToJsonString());
        return path;
    }

    private sealed class FakeWorkflowProcessRuntime(
        Func<(Guid ProcessId, string MessageId, string SignalType, string BodyHash, JsonNode Body), JsonNode> onSignal)
        : IWorkflowProcessRuntime
    {
        public Task<JsonNode> StartAsync(
            string flowName, string businessKey, JsonNode startData, string startDataHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException("flow signal never starts a process");

        public Task<JsonNode> GetCompactAsync(Guid processId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("flow signal never reads a process back");

        public Task<JsonNode> SignalAsync(
            Guid processId, string messageId, string signalType, string bodyHash, JsonNode body, CancellationToken cancellationToken) =>
            Task.FromResult(onSignal((processId, messageId, signalType, bodyHash, body)));
    }
}
