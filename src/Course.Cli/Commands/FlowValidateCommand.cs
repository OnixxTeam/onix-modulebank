using System.Text.Json.Nodes;
using Course.Core.Cli;
using Workflow.Core.Maps;
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

        JsonNode document;
        try
        {
            document = await MapLoader.LoadAsync(command.Positionals[0], cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MapLoadException)
        {
            return CliEnvelope.Fail("map.invalid", $"cannot read map file: {ex.Message}");
        }

        var result = await validator.ValidateAsync(document, cancellationToken);
        if (!result.IsValid)
        {
            var first = result.Violations[0];
            var message = result.Violations.Count == 1
                ? first.Message
                : $"{first.Message} (and {result.Violations.Count - 1} more violation(s))";
            return CliEnvelope.Fail(first.Code, message);
        }

        var map = result.Map!;
        return CliEnvelope.Ok(new
        {
            resource = "flow",
            operation = "validated",
            flowName = map.FlowName,
            flowVersion = map.Version,
        });
    }
}
