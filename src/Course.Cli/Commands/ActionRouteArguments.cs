using Course.Core.Cli;

namespace Course.Cli.Commands;

// Shared by action activate and action disable: both take a "module.action"
// positional and one or more --version-shaped options, and both must reject
// a malformed value before ever opening a database connection.
internal static class ActionRouteArguments
{
    public static bool TryParseRouteKey(string raw, out string module, out string action, out CliEnvelope? error)
    {
        var separator = raw.IndexOf('.');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            module = "";
            action = "";
            error = CliEnvelope.Fail("request.invalid", "route key must be in the form module.action");
            return false;
        }

        module = raw[..separator];
        action = raw[(separator + 1)..];
        error = null;
        return true;
    }

    public static bool TryParseVersion(
        IReadOnlyDictionary<string, string> options, string optionName, bool required, out int? version, out CliEnvelope? error)
    {
        if (!options.TryGetValue(optionName, out var raw))
        {
            version = null;
            error = required ? CliEnvelope.Fail("request.invalid", $"--{optionName} is required") : null;
            return !required;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 1)
        {
            version = null;
            error = CliEnvelope.Fail("request.invalid", $"--{optionName} must be an integer >= 1");
            return false;
        }

        version = parsed;
        error = null;
        return true;
    }
}
