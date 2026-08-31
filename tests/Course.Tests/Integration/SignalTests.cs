using System.Text.Json.Nodes;
using Npgsql;
using Workflow.Core.Canonicalization;

namespace Course.Tests.Integration;

// Ticket 09 acceptance: message_id is globally unique, a matching repeat is a
// duplicate, any discrepancy is a conflict that changes nothing, an
// undeclared signal type is rejected, a signal for an already-waiting process
// applies and advances in the same transaction, an early signal is stored
// ACCEPTED and picked up atomically the moment the process enters the
// matching wait_signal step (never by a separate pass), and a signal to a
// completed process is stored without moving it.
[Collection("integration")]
public sealed class SignalTests
{
    private readonly ApiFactory factory;

    public SignalTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Signal_ProcessAlreadyWaiting_AppliesImmediately_AndAdvancesTheProcess()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);

        var body = new JsonObject { ["value"] = "hello" };
        var result = await SignalAsync(processId, "type-a", NewMessageId(), body);

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.False(result["duplicate"]!.GetValue<bool>());

        var process = await GetProcessAsync(processId);
        Assert.Equal("WAITING_SIGNAL", process.State);
        Assert.Equal("second-wait", process.CurrentStepKey);

        var signalStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from workflow.workflow_signal where process_id = @processId and signal_type = 'type-a'",
            ("processId", processId));
        Assert.Equal("APPLIED", signalStatus);

        var eventTypes = await GetEventTypesAsync(processId);
        Assert.Contains("SignalAccepted", eventTypes);
        Assert.Contains("SignalApplied", eventTypes);
    }

    [Fact]
    public async Task Signal_EarlyArrival_IsStoredAccepted_AndAppliedAtomicallyOnEntry_CascadingToTheEnd()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);

        // type-b's step ("second-wait") has not been entered yet -- the process
        // is still on "first-wait" (type-a).
        var earlyResult = await SignalAsync(processId, "type-b", NewMessageId(), new JsonObject());
        Assert.Equal("ok", earlyResult["status"]!.GetValue<string>());

        var afterEarly = await GetProcessAsync(processId);
        Assert.Equal("first-wait", afterEarly.CurrentStepKey);

        var earlySignalStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from workflow.workflow_signal where process_id = @processId and signal_type = 'type-b'",
            ("processId", processId));
        Assert.Equal("ACCEPTED", earlySignalStatus);

        // The on-time type-a signal completes first-wait, enters second-wait,
        // and second-wait's own entry must pick up type-b's already-ACCEPTED
        // signal in the same statement -- never a separate pass.
        await SignalAsync(processId, "type-a", NewMessageId(), new JsonObject());

        var final = await GetProcessAsync(processId);
        Assert.Equal("COMPLETED", final.State);
        Assert.Equal("end", final.CurrentStepKey);

        var typeBStatus = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select status from workflow.workflow_signal where process_id = @processId and signal_type = 'type-b'",
            ("processId", processId));
        Assert.Equal("APPLIED", typeBStatus);
    }

    [Fact]
    public async Task Signal_ExactRepeat_ReturnsDuplicate_AndChangesNothing()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);
        var messageId = NewMessageId();
        var body = new JsonObject { ["value"] = "same" };

        var first = await SignalAsync(processId, "type-a", messageId, body);
        Assert.False(first["duplicate"]!.GetValue<bool>());

        var second = await SignalAsync(processId, "type-a", messageId, body);

        Assert.Equal("ok", second["status"]!.GetValue<string>());
        Assert.True(second["duplicate"]!.GetValue<bool>());

        var signalCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow.workflow_signal where message_id = @messageId", ("messageId", messageId));
        Assert.Equal(1, signalCount);
    }

    [Fact]
    public async Task Signal_SameMessageId_DifferentBody_ReturnsConflict_AndChangesNothing()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);
        var messageId = NewMessageId();

        var first = await SignalAsync(processId, "type-a", messageId, new JsonObject { ["value"] = "one" });
        Assert.Equal("ok", first["status"]!.GetValue<string>());

        var second = await SignalAsync(processId, "type-a", messageId, new JsonObject { ["value"] = "two" });

        Assert.Equal("error", second["status"]!.GetValue<string>());
        Assert.Equal("workflow.signal_conflict", second["code"]!.GetValue<string>());

        var storedHash = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select body_hash from workflow.workflow_signal where message_id = @messageId", ("messageId", messageId));
        Assert.Equal(JsonCanonicalizer.Sha256Hex(new JsonObject { ["value"] = "one" }), storedHash);
    }

    [Fact]
    public async Task Signal_SameMessageId_DifferentProcess_ReturnsConflict()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var firstProcessId = await StartProcessAsync(flowName);
        var secondProcessId = await StartProcessAsync(flowName);
        var messageId = NewMessageId();

        var first = await SignalAsync(firstProcessId, "type-a", messageId, new JsonObject());
        Assert.Equal("ok", first["status"]!.GetValue<string>());

        var second = await SignalAsync(secondProcessId, "type-a", messageId, new JsonObject());

        Assert.Equal("error", second["status"]!.GetValue<string>());
        Assert.Equal("workflow.signal_conflict", second["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Signal_UndeclaredType_IsRejected_AndStoresNoRow()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);
        var messageId = NewMessageId();

        var result = await SignalAsync(processId, "type-nonexistent", messageId, new JsonObject());

        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal("workflow.unknown_signal_type", result["code"]!.GetValue<string>());

        var signalCount = await factory.Postgres.ScalarAsOwnerAsync<long>(
            "select count(*) from workflow.workflow_signal where message_id = @messageId", ("messageId", messageId));
        Assert.Equal(0, signalCount);
    }

    [Fact]
    public async Task Signal_ToACompletedProcess_IsStored_ButDoesNotMoveIt()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);
        await SignalAsync(processId, "type-a", NewMessageId(), new JsonObject());
        await SignalAsync(processId, "type-b", NewMessageId(), new JsonObject());

        var completed = await GetProcessAsync(processId);
        Assert.Equal("COMPLETED", completed.State);

        var result = await SignalAsync(processId, "type-a", NewMessageId(), new JsonObject());

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.False(result["duplicate"]!.GetValue<bool>());

        var stillCompleted = await GetProcessAsync(processId);
        Assert.Equal("COMPLETED", stillCompleted.State);
        Assert.Equal("end", stillCompleted.CurrentStepKey);
    }

    [Fact]
    public async Task AutocheckSignalsView_ExposesTheDeclaredColumns_ThroughCourseRuntime()
    {
        var flowName = NewFlowName();
        await PublishAndActivateTwoWaitFlowAsync(flowName);
        var processId = await StartProcessAsync(flowName);
        var messageId = NewMessageId();
        await SignalAsync(processId, "type-a", messageId, new JsonObject());

        var row = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select message_id, process_id, signal_type, body_hash, status, received_at "
                + "from autocheck.signals where message_id = @messageId", connection);
            command.Parameters.AddWithValue("messageId", messageId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (MessageId: reader.GetString(0), SignalType: reader.GetString(2), Status: reader.GetString(4));
        });
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal("type-a", row.SignalType);
        Assert.Equal("APPLIED", row.Status);
    }

    private Task<JsonNode> SignalAsync(Guid processId, string signalType, string messageId, JsonNode body) =>
        factory.Postgres.ProcessRuntime.SignalAsync(
            processId, messageId, signalType, JsonCanonicalizer.Sha256Hex(body), body, CancellationToken.None);

    private async Task<(string State, string CurrentStepKey)> GetProcessAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, current_step_key from workflow.process_instance where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (State: reader.GetString(0), CurrentStepKey: reader.GetString(1));
        });

    private async Task<List<string>> GetEventTypesAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select event_type from workflow.workflow_event where process_id = @processId order by occurred_at", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            var types = new List<string>();
            while (await reader.ReadAsync())
            {
                types.Add(reader.GetString(0));
            }

            return types;
        });

    private static string NewFlowName() => $"signal-flow-{Guid.NewGuid():N}";

    private static string NewBusinessKey() => $"business-{Guid.NewGuid():N}";

    private static string NewMessageId() => $"message-{Guid.NewGuid():N}";

    // start_step is itself a wait_signal step, and process_data plays no part
    // in signal resolution, so this map needs no automatic step or worker at
    // all: start_process's own call into enter_step is enough to land the
    // process in WAITING_SIGNAL, isolating this ticket's own logic from
    // ticket 08's.
    private async Task PublishAndActivateTwoWaitFlowAsync(string flowName)
    {
        var document = JsonNode.Parse($$"""
            {
              "flow_name": "{{flowName}}",
              "version": 1,
              "start_step": "first-wait",
              "steps": [
                {"key": "first-wait", "type": "wait_signal", "signal_type": "type-a", "outcome": "A_DONE"},
                {"key": "second-wait", "type": "wait_signal", "signal_type": "type-b", "outcome": "B_DONE"},
                {"key": "end", "type": "end", "outcome": "DONE"}
              ],
              "transitions": [
                {"from": "first-wait", "outcome": "A_DONE", "to": "second-wait"},
                {"from": "second-wait", "outcome": "B_DONE", "to": "end"}
              ]
            }
            """)!;

        var published = await factory.Postgres.FlowPublisher.PublishAsync(
            document, JsonCanonicalizer.Sha256Hex(document), CancellationToken.None);
        if (published["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish signal test flow {flowName}: {published.ToJsonString()}");
        }

        var activated = await factory.Postgres.FlowPublisher.ActivateAsync(flowName, 1, CancellationToken.None);
        if (activated["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate signal test flow {flowName}: {activated.ToJsonString()}");
        }
    }

    private async Task<Guid> StartProcessAsync(string flowName)
    {
        var startData = new JsonObject();
        var started = await factory.Postgres.ProcessRuntime.StartAsync(
            flowName, NewBusinessKey(), startData, JsonCanonicalizer.Sha256Hex(startData), CancellationToken.None);
        if (started["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to start signal test process: {started.ToJsonString()}");
        }

        return Guid.Parse(started["processId"]!.GetValue<string>());
    }
}
