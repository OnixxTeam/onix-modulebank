using System.Text.Json;

namespace Workflow.Worker;

// SPEC "Test profile и failpoints": at a named boundary the worker prints
// exactly one structured log line then blocks until the process is killed
// outright (compose kill == SIGKILL, no signal handler, no graceful path) --
// so ReachAsync never returns in production. A worker started without
// COURSE_FAILPOINT gets NoopFailpointGate, which every existing WorkerLoop
// caller sees by default (the constructor parameter is optional) so ticket
// 08/09/10 tests need no change.
public interface IFailpointGate
{
    Task ReachAsync(string name, string instanceId, CancellationToken cancellationToken);
}

public sealed class NoopFailpointGate : IFailpointGate
{
    public static readonly NoopFailpointGate Instance = new();

    private NoopFailpointGate()
    {
    }

    public Task ReachAsync(string name, string instanceId, CancellationToken cancellationToken) => Task.CompletedTask;
}

// armedName is COURSE_FAILPOINT verbatim -- null/empty (unset) disarms both
// boundaries. Only a name equal to armedName fires; the loop calls this at
// both boundaries every time, so the other one is always a silent no-op.
public sealed class FailpointGate(string? armedName, TextWriter output) : IFailpointGate
{
    public async Task ReachAsync(string name, string instanceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(armedName) || armedName != name)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new { @event = "failpoint.reached", name, instanceId });
        await output.WriteLineAsync(line);
        await output.FlushAsync(CancellationToken.None);

        // Blocks forever -- the caller's own transaction, if any, is left
        // exactly as it was (open or already committed by the time this is
        // called). Tests pass a cancellable token to observe the block
        // without hanging the suite; production passes CancellationToken.None,
        // so this genuinely never returns there.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
