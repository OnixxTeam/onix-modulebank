using Course.Core.Cli;
using Workflow.Core.Publishing;

namespace Course.Cli.Commands;

public static class FlowListCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(IWorkflowFlowPublisher publisher, CancellationToken cancellationToken)
    {
        var flows = await publisher.ListAsync(cancellationToken);
        return CliEnvelope.Ok(new { items = flows });
    }
}
