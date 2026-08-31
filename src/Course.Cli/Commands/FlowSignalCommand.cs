using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Cli;
using Workflow.Core.Canonicalization;
using Workflow.Core.Runtime;

namespace Course.Cli.Commands;

public static class FlowSignalCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IWorkflowProcessRuntime runtime, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow signal requires exactly one process id argument");
        }

        if (!Guid.TryParse(command.Positionals[0], out var processId))
        {
            return CliEnvelope.Fail("request.invalid", "process id must be a UUID");
        }

        if (!command.Options.TryGetValue("type", out var signalType) || signalType.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--type is required");
        }

        if (!command.Options.TryGetValue("message-id", out var messageId) || messageId.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--message-id is required");
        }

        if (!command.Options.TryGetValue("payload", out var payloadPath) || payloadPath.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--payload is required");
        }

        JsonNode body;
        try
        {
            var raw = await File.ReadAllTextAsync(payloadPath, cancellationToken);
            body = JsonNode.Parse(raw) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return CliEnvelope.Fail("request.invalid", $"cannot read payload file: {ex.Message}");
        }

        var bodyHash = JsonCanonicalizer.Sha256Hex(body);
        var result = await runtime.SignalAsync(processId, messageId, signalType, bodyHash, body, cancellationToken);

        var status = result["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "signal",
                processId = result["processId"]!.GetValue<string>(),
                messageId,
                signalType = result["signalType"]!.GetValue<string>(),
                status = result["duplicate"]!.GetValue<bool>() ? "duplicate" : "accepted",
            });
        }

        return CliEnvelope.Fail(result["code"]!.GetValue<string>(), result["message"]!.GetValue<string>());
    }
}
