using System.Text.Json.Nodes;

namespace Workflow.Core.Maps;

public enum StepKind
{
    Automatic,
    WaitSignal,
    Manual,
    End,
}

// Retry belongs to the task, not the map root: only automatic steps have one.
public sealed record RetryDefinition(int MaxAttempts, IReadOnlyList<int> DelaysMs);

public sealed record TaskDefinition(
    string Module,
    string Action,
    int ActionVersion,
    IReadOnlyList<string> RequiredPolicy,
    RetryDefinition Retry,
    IReadOnlyDictionary<string, string> InputMapping,
    JsonObject InputConstants);

// One record for all four step types rather than a class hierarchy: the schema
// already guarantees each step carries exactly the fields its `type` allows, so
// downstream code only ever reads the property that matches Kind.
public sealed record StepDefinition(
    string Key,
    StepKind Kind,
    TaskDefinition? Task = null,
    string? SignalType = null,
    string? WaitOutcome = null,
    IReadOnlyList<string>? AllowedOutcomes = null,
    string? EndOutcome = null)
{
    // The set of outcomes this step must route by transition, exactly once each --
    // automatic asks the action catalog (unknown until that lookup runs), end has
    // none (no outgoing transition is ever valid), everything else is self-declared.
    public IReadOnlyList<string>? DeclaredOutcomes(IReadOnlyList<string>? catalogOutcomes) => Kind switch
    {
        StepKind.Automatic => catalogOutcomes,
        StepKind.WaitSignal => [WaitOutcome!],
        StepKind.Manual => AllowedOutcomes,
        StepKind.End => [],
        _ => throw new InvalidOperationException($"unknown step kind: {Kind}"),
    };
}

public sealed record TransitionDefinition(string From, string Outcome, string To);

public sealed record MapDocument(
    string FlowName,
    int Version,
    string StartStep,
    IReadOnlyList<StepDefinition> Steps,
    IReadOnlyList<TransitionDefinition> Transitions)
{
    // Last-wins rather than ToDictionary's throw-on-duplicate: a map with a
    // duplicate step key must reach GraphRules.CheckDuplicateStepKeys and be
    // reported as a violation, not crash the parser before validation runs.
    public IReadOnlyDictionary<string, StepDefinition> StepsByKey { get; } = BuildLookup(Steps);

    private static Dictionary<string, StepDefinition> BuildLookup(IReadOnlyList<StepDefinition> steps)
    {
        var lookup = new Dictionary<string, StepDefinition>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            lookup[step.Key] = step;
        }

        return lookup;
    }
}

// Reads a document that has already passed JSON Schema validation, so every
// field this touches is guaranteed present and correctly typed -- the `??
// throw` guards here are unreachable in practice, not defensive input handling.
public static class MapParser
{
    public static MapDocument Parse(JsonNode document)
    {
        var flowName = document["flow_name"]!.GetValue<string>();
        var version = document["version"]!.GetValue<int>();
        var startStep = document["start_step"]!.GetValue<string>();

        var steps = document["steps"]!.AsArray().Select(ParseStep).ToList();
        var transitions = document["transitions"]!.AsArray().Select(ParseTransition).ToList();

        return new MapDocument(flowName, version, startStep, steps, transitions);
    }

    private static StepDefinition ParseStep(JsonNode? node)
    {
        var step = node!;
        var key = step["key"]!.GetValue<string>();
        var type = step["type"]!.GetValue<string>();

        return type switch
        {
            "automatic" => new StepDefinition(key, StepKind.Automatic, Task: ParseTask(step["task"]!)),
            "wait_signal" => new StepDefinition(
                key,
                StepKind.WaitSignal,
                SignalType: step["signal_type"]!.GetValue<string>(),
                WaitOutcome: step["outcome"]!.GetValue<string>()),
            "manual" => new StepDefinition(
                key,
                StepKind.Manual,
                AllowedOutcomes: step["allowed_outcomes"]!.AsArray().Select(o => o!.GetValue<string>()).ToList()),
            "end" => new StepDefinition(key, StepKind.End, EndOutcome: step["outcome"]!.GetValue<string>()),
            _ => throw new InvalidOperationException($"unknown step type: {type}"),
        };
    }

    private static TaskDefinition ParseTask(JsonNode task)
    {
        var requiredPolicy = task["required_policy"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        var retry = task["retry"]!;
        var delaysMs = retry["delays_ms"]!.AsArray().Select(d => d!.GetValue<int>()).ToList();

        var inputMapping = task["input_mapping"]!.AsObject()
            .ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);

        return new TaskDefinition(
            Module: task["module"]!.GetValue<string>(),
            Action: task["action"]!.GetValue<string>(),
            ActionVersion: task["action_version"]!.GetValue<int>(),
            RequiredPolicy: requiredPolicy,
            Retry: new RetryDefinition(retry["max_attempts"]!.GetValue<int>(), delaysMs),
            InputMapping: inputMapping,
            InputConstants: task["input_constants"]!.AsObject().DeepClone().AsObject());
    }

    private static TransitionDefinition ParseTransition(JsonNode? node)
    {
        var transition = node!;
        return new TransitionDefinition(
            transition["from"]!.GetValue<string>(),
            transition["outcome"]!.GetValue<string>(),
            transition["to"]!.GetValue<string>());
    }
}
