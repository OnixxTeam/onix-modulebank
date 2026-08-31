using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Cli;
using Workflow.Core.Canonicalization;
using Workflow.Core.Runtime;

namespace Course.Cli.Commands;

public static class FlowStartCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IWorkflowProcessRuntime runtime, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow start requires exactly one flow name argument");
        }

        if (!command.Options.TryGetValue("business-key", out var businessKey) || businessKey.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--business-key is required");
        }

        JsonNode startData;
        if (command.Options.TryGetValue("data", out var dataPath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(dataPath, cancellationToken);
                startData = JsonNode.Parse(raw) ?? new JsonObject();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return CliEnvelope.Fail("request.invalid", $"cannot read data file: {ex.Message}");
            }
        }
        else
        {
            startData = new JsonObject();
        }

        var startDataHash = JsonCanonicalizer.Sha256Hex(startData);
        var result = await runtime.StartAsync(command.Positionals[0], businessKey, startData, startDataHash, cancellationToken);

        var status = result["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "process",
                operation = "started",
                processId = result["processId"]!.GetValue<string>(),
                flowName = result["flowName"]!.GetValue<string>(),
                flowVersion = result["flowVersion"]!.GetValue<int>(),
                state = result["state"]!.GetValue<string>(),
            });
        }

        return CliEnvelope.Fail(result["code"]!.GetValue<string>(), result["message"]!.GetValue<string>());
    }
}
