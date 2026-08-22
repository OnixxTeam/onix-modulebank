using Course.Core.Actions;
using Course.Core.Cli;

namespace Course.Cli.Commands;

public static class ActionListCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(IActionPublisher publisher, CancellationToken cancellationToken)
    {
        var actions = await publisher.ListAsync(cancellationToken);
        return CliEnvelope.Ok(actions);
    }
}
