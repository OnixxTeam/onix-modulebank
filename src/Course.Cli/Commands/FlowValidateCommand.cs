using Course.Core.Cli;
using Workflow.Core.Validation;

namespace Course.Cli.Commands;

public static class FlowValidateCommand
{
    // Never opens a write transaction: parses the map, runs it through the
    // schema and semantic validator, and reports -- the DB read inside
    // WorkflowMapValidator (catalog lookups) is the only I/O this command does.
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, WorkflowMapValidator validator, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow validate requires exactly one map file path argument");
        }

        var loaded = await FlowMapLoader.LoadAsync(command.Positionals[0], validator, cancellationToken);
        if (loaded.Envelope is not null)
        {
            return loaded.Envelope;
        }

        var map = loaded.Map!;
        return CliEnvelope.Ok(new
        {
            resource = "flow",
            operation = "validated",
            flowName = map.FlowName,
            flowVersion = map.Version,
        });
    }
}
