using Course.Core.Cli;
using Workflow.Core.Canonicalization;
using Workflow.Core.Publishing;
using Workflow.Core.Validation;

namespace Course.Cli.Commands;

public static class FlowPublishCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command,
        WorkflowMapValidator validator,
        IWorkflowFlowPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow publish requires exactly one map file path argument");
        }

        var loaded = await FlowMapLoader.LoadAsync(command.Positionals[0], validator, cancellationToken);
        if (loaded.Envelope is not null)
        {
            return loaded.Envelope;
        }

        var hash = JsonCanonicalizer.Sha256Hex(loaded.Document!);
        var raw = await publisher.PublishAsync(loaded.Document!, hash, cancellationToken);

        var status = raw["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "flow",
                operation = "published",
                flowName = raw["flowName"]!.GetValue<string>(),
                flowVersion = raw["flowVersion"]!.GetValue<int>(),
            });
        }

        return CliEnvelope.Fail(raw["code"]!.GetValue<string>(), raw["message"]!.GetValue<string>());
    }
}
