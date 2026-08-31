using Course.Core.Cli;
using Workflow.Core.Runtime;

namespace Course.Cli.Commands;

public static class FlowGetCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IWorkflowProcessRuntime runtime, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow get requires exactly one process id argument");
        }

        if (!Guid.TryParse(command.Positionals[0], out var processId))
        {
            return CliEnvelope.Fail("request.invalid", "process id must be a UUID");
        }

        var result = await runtime.GetCompactAsync(processId, cancellationToken);

        var status = result["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "process",
                processId = result["processId"]!.GetValue<string>(),
                flowName = result["flowName"]!.GetValue<string>(),
                flowVersion = result["flowVersion"]!.GetValue<int>(),
                state = result["state"]!.GetValue<string>(),
                currentStepKey = result["currentStepKey"]!.GetValue<string>(),
            });
        }

        return CliEnvelope.Fail(result["code"]!.GetValue<string>(), result["message"]!.GetValue<string>());
    }
}
