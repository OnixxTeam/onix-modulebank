using Course.Core.Actions;
using Course.Core.Cli;

namespace Course.Cli.Commands;

public static class ActionDisableCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IActionPublisher publisher, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "action disable requires exactly one module.action argument");
        }

        if (!ActionRouteArguments.TryParseRouteKey(command.Positionals[0], out var module, out var action, out var keyError))
        {
            return keyError!;
        }

        if (!ActionRouteArguments.TryParseVersion(command.Options, "version", required: true, out var version, out var versionError))
        {
            return versionError!;
        }

        if (!ActionRouteArguments.TryParseVersion(
                command.Options, "replacement-version", required: false, out var replacementVersion, out var replacementError))
        {
            return replacementError!;
        }

        var raw = await publisher.DisableAsync(module, action, version!.Value, replacementVersion, cancellationToken);

        var status = raw["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "action",
                operation = "disabled",
                key = raw["key"]!.GetValue<string>(),
                version = raw["version"]!.GetValue<int>(),
            });
        }

        return CliEnvelope.Fail(raw["code"]!.GetValue<string>(), raw["message"]!.GetValue<string>());
    }
}
