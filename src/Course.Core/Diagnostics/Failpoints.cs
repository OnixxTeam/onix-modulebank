using System.Text.Json;

namespace Course.Core.Diagnostics;

// The six published failpoint names, and the one place that decides whether a
// process arms any of them at all.
//
// Week 4 moved this out of Workflow.Worker: four of the six boundaries are in
// `api` and the dispatcher, and all of them are *generic* seams -- a job claim,
// an action's own pre-commit moment, an Outbox claim, a provider response. None
// of them names a domain action, because no part of C# is allowed to.
public static class Failpoints
{
    public const string AfterJobClaim = "after_job_claim";
    public const string AfterActionBeforeFinish = "after_action_before_finish";
    public const string AfterOutboxClaim = "after_outbox_claim";
    public const string AfterProviderResponse = "after_provider_response";
    public const string AfterInboxSaved = "after_inbox_saved";
    public const string AfterManualDecision = "after_manual_decision";

    public static readonly IReadOnlyList<string> Names =
    [
        AfterJobClaim, AfterActionBeforeFinish, AfterOutboxClaim,
        AfterProviderResponse, AfterInboxSaved, AfterManualDecision,
    ];

    // Outside the closed test profile there is no gate at all, which is what
    // "production profile ignores failpoints" means: not an armed gate that
    // declines to fire, but no armed name reaching the process in the first
    // place. There is deliberately no endpoint that can arm one either -- the
    // only channel is this pair of variables, set before the process starts.
    public static IFailpointGate FromEnvironment(TextWriter output) =>
        Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE") == "1"
            ? new FailpointGate(Environment.GetEnvironmentVariable("COURSE_FAILPOINT"), output)
            : NoopFailpointGate.Instance;
}

// At a named boundary the component prints exactly one structured line then
// blocks until the process is killed outright (compose kill == SIGKILL, no
// signal handler, no graceful path) -- so ReachAsync never returns in
// production.
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

// armedName is COURSE_FAILPOINT verbatim. Null, empty or a name outside the six
// published ones disarms every boundary: an unknown name is a plain no-op rather
// than a startup failure, because the verifying run sets this variable through a
// Compose override and a typo there must not take the component down. Callers
// invoke this at every boundary they own on every pass, so the boundaries that
// are not the armed one are always silent.
public sealed class FailpointGate(string? armedName, TextWriter output) : IFailpointGate
{
    private readonly string? armed =
        armedName is not null && Failpoints.Names.Contains(armedName, StringComparer.Ordinal) ? armedName : null;

    public async Task ReachAsync(string name, string instanceId, CancellationToken cancellationToken)
    {
        if (armed is null || armed != name)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new { @event = "failpoint.reached", name, instanceId });
        await output.WriteLineAsync(line);
        await output.FlushAsync(CancellationToken.None);

        // Blocks forever -- the caller's own transaction, if any, is left exactly
        // as it was (open, or already committed by the time this is called).
        // Tests pass a cancellable token to observe the block without hanging the
        // suite; production passes CancellationToken.None, so this genuinely
        // never returns there.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
