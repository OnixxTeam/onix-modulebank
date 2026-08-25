namespace Course.Cli;

public sealed record ParsedCommand(string Name, IReadOnlyList<string> Positionals, IReadOnlyDictionary<string, string> Options);

public sealed class ArgumentParserException(string message) : Exception(message);

// Hand-rolled: the stdout contract allows exactly one JSON document, and off-the-shelf
// argument-parsing libraries print usage/parse errors straight to stdout, which would
// corrupt it.
public static class ArgumentParser
{
    private static readonly HashSet<string> CommandGroups = new(StringComparer.Ordinal) { "migration", "action" };

    public static ParsedCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentParserException("missing command");
        }

        var index = 0;
        var nameTokens = new List<string> { args[index++] };

        if (CommandGroups.Contains(nameTokens[0])
            && index < args.Length
            && !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            nameTokens.Add(args[index++]);
        }

        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        while (index < args.Length)
        {
            var token = args[index];

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var key = token[2..];
                if (key.Length == 0)
                {
                    throw new ArgumentParserException($"empty flag name at position {index}");
                }

                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentParserException($"flag --{key} requires a value");
                }

                options[key] = args[index + 1];
                index += 2;
            }
            else
            {
                positionals.Add(token);
                index += 1;
            }
        }

        return new ParsedCommand(string.Join(' ', nameTokens), positionals, options);
    }
}
