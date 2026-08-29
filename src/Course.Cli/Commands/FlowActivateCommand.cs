using Course.Core.Cli;
using Workflow.Core.Publishing;

namespace Course.Cli.Commands;

public static class FlowActivateCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IWorkflowFlowPublisher publisher, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow activate requires exactly one flow name argument");
        }

        if (!ActionRouteArguments.TryParseVersion(command.Options, "version", required: true, out var version, out var versionError))
        {
            return versionError!;
        }

        var raw = await publisher.ActivateAsync(command.Positionals[0], version!.Value, cancellationToken);

        var status = raw["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "flow",
                operation = "activated",
                flowName = raw["flowName"]!.GetValue<string>(),
                flowVersion = raw["flowVersion"]!.GetValue<int>(),
            });
        }

        return CliEnvelope.Fail(raw["code"]!.GetValue<string>(), raw["message"]!.GetValue<string>());
    }
}
