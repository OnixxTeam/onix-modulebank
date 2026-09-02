using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Cli;
using Workflow.Core.Runtime;

namespace Course.Cli.Commands;

// The fencing probe (ticket 07): simulates a finish from an arbitrary owner
// and lease version without racing a real lease expiry, by calling the exact
// production finish boundary (workflow.finish_job) a real worker calls.
// Gated by testProfileEnabled so it stays invisible outside COURSE_TEST_PROFILE=1
// -- reported the same way an unknown command would be, not as a disabled one,
// so its existence isn't leaked to a caller outside the test profile.
public static class FlowTestFinishCommand
{
    public static async Task<CliEnvelope> ExecuteAsync(
        ParsedCommand command, IWorkflowJobRuntime runtime, bool testProfileEnabled, CancellationToken cancellationToken)
    {
        if (!testProfileEnabled)
        {
            return CliEnvelope.Fail("command.unknown", "unknown command: flow test-finish");
        }

        if (command.Positionals.Count != 1)
        {
            return CliEnvelope.Fail("request.invalid", "flow test-finish requires exactly one job id argument");
        }

        if (!Guid.TryParse(command.Positionals[0], out var jobId))
        {
            return CliEnvelope.Fail("request.invalid", "job id must be a UUID");
        }

        if (!command.Options.TryGetValue("owner", out var owner) || owner.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--owner is required");
        }

        if (!ActionRouteArguments.TryParseVersion(command.Options, "lease-version", required: true, out var leaseVersion, out var leaseVersionError))
        {
            return leaseVersionError!;
        }

        if (!command.Options.TryGetValue("outcome", out var outcome) || outcome.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--outcome is required");
        }

        if (!command.Options.TryGetValue("result", out var resultPath) || resultPath.Length == 0)
        {
            return CliEnvelope.Fail("request.invalid", "--result is required");
        }

        JsonNode result;
        try
        {
            var raw = await File.ReadAllTextAsync(resultPath, cancellationToken);
            result = JsonNode.Parse(raw) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return CliEnvelope.Fail("request.invalid", $"cannot read result file: {ex.Message}");
        }

        var envelope = await runtime.FinishAsync(jobId, owner, leaseVersion!.Value, outcome, result, cancellationToken);

        var status = envelope["status"]!.GetValue<string>();
        if (status == "ok")
        {
            return CliEnvelope.Ok(new
            {
                resource = "job",
                operation = "finished",
                jobId = envelope["jobId"]!.GetValue<string>(),
                outcome = envelope["outcome"]!.GetValue<string>(),
            });
        }

        return CliEnvelope.Fail(envelope["code"]!.GetValue<string>(), envelope["message"]!.GetValue<string>());
    }
}
