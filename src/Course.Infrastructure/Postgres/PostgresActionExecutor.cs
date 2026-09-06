using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Core.Errors;
using Course.Core.Validation;
using Npgsql;
using NpgsqlTypes;

namespace Course.Infrastructure.Postgres;

// The resource-owning entry point: opens the connection and transaction, runs the
// pipeline (step 5-13 of spec §5.1) inside them, and decides whether to commit.
// Every failure rolls the transaction back via `await using` -- except when the
// pipeline reports TargetReturnedError, which commits first: migration 015's
// api.invoke already rolled the domain effect back to a savepoint for that case,
// leaving only the dispatch row to persist as the technical audit trail
// (contract-reference: "Target вернул status=error" row).
public sealed class PostgresActionExecutor(NpgsqlDataSource dataSource, ISchemaValidator validator, TimeProvider timeProvider)
    : IActionExecutor
{
    public async Task<ActionExecutionResult> ExecuteAsync(
        string moduleName,
        string actionName,
        int? explicitVersion,
        VerifiedClaims claims,
        Guid correlationId,
        string? requestId,
        JsonNode payload,
        CancellationToken cancellationToken,
        int? signatureVersion = null,
        string? signatureBodyHashHex = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var outcome = await ExecuteInTransactionAsync(
            connection, transaction, moduleName, actionName, explicitVersion, claims, correlationId, requestId, payload,
            callerContext: null, cancellationToken, signatureVersion, signatureBodyHashHex);

        if (outcome.Result.IsSuccess || outcome.TargetReturnedError)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return outcome.Result;
    }

    // The same pipeline, running inside a connection and transaction the caller
    // already owns: it never commits or rolls back, only returns what happened,
    // because week 1's network path and a week-2 worker commit on different
    // conditions for TargetReturnedError (see ExecuteAsync above vs. a worker that
    // must retry without leaving a dispatch row behind).
    public async Task<ActionPipelineOutcome> ExecuteInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string moduleName,
        string actionName,
        int? explicitVersion,
        VerifiedClaims claims,
        Guid correlationId,
        string? requestId,
        JsonNode payload,
        ActionCallerContext? callerContext,
        CancellationToken cancellationToken,
        int? signatureVersion = null,
        string? signatureBodyHashHex = null)
    {
        // Every early exit here is a rejection before (or instead of) invoking the
        // target, so the transaction owner must never commit it -- only the
        // TargetReturnedError branch below sets that flag true.
        static ActionPipelineOutcome Rejected(ActionError error, int? actionVersion = null) =>
            new(ActionExecutionResult.Failure(error, actionVersion), TargetReturnedError: false);

        // A worker call already carries the pinned action version's manifest
        // fields (claim_jobs's own jsonb_build_object -- migration 022), so it
        // skips this pipeline's own api.get_action_manifest read entirely
        // (ticket 10: workflow_worker's EXECUTE list stays exactly four
        // functions, and this read would be a fifth). IdempotencyMode is a
        // placeholder that can never trip IdempotencyRequired below: every
        // worker call supplies a request id (TrustedContext.ExecutionId).
        // TimeoutMs is likewise never read back out -- a caller that supplies
        // Manifest always supplies TimeoutOverride too (WorkerLoop's own
        // contract), which outranks it unconditionally a few lines down.
        // Version comes from the manifest itself, never from explicitVersion
        // (ticket 02): the two are supplied by the same caller and must agree,
        // but only PrefetchedManifest.Version is a non-nullable guarantee.
        var manifest = callerContext?.Manifest is { } prefetched
            ? new RuntimeManifest(
                Version: prefetched.Version,
                IdempotencyMode: "not_required",
                RequiredPolicy: prefetched.RequiredPolicy,
                RequestSchema: prefetched.RequestSchema,
                ResponseSchema: prefetched.ResponseSchema,
                Outcomes: prefetched.Outcomes,
                TimeoutMs: 0)
            : await ReadManifestAsync(connection, transaction, moduleName, actionName, explicitVersion, cancellationToken);
        if (manifest is null)
        {
            return Rejected(ActionError.ActionNotFound());
        }

        if (manifest.IdempotencyMode == "required" && requestId is null)
        {
            return Rejected(ActionError.IdempotencyRequired(), manifest.Version);
        }

        if (!manifest.RequiredPolicy.All(claims.Scopes.Contains))
        {
            return Rejected(ActionError.AccessDenied(), manifest.Version);
        }

        // The caller's map, when it declares its own timeout, outranks the
        // manifest's timeout_ms -- otherwise fall back to the manifest as before.
        var timeoutMs = callerContext?.TimeoutOverride is { } timeoutOverride
            ? (int)timeoutOverride.TotalMilliseconds
            : manifest.TimeoutMs;

        await SetStatementTimeoutAsync(connection, transaction, timeoutMs, cancellationToken);

        var payloadValidation = validator.Validate(manifest.RequestSchema, payload);
        if (!payloadValidation.IsValid)
        {
            return Rejected(ActionError.PayloadInvalid(string.Join("; ", payloadValidation.Errors)), manifest.Version);
        }

        var context = new TrustedContext(
            claims.Principal,
            claims.Consumer,
            claims.Scopes,
            correlationId,
            requestId,
            timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs),
            callerContext?.ProcessId,
            callerContext?.JobId,
            callerContext?.ExecutionId,
            callerContext?.AttemptId,
            signatureVersion,
            signatureBodyHashHex);

        // Pass the version the manifest actually resolved to, not the caller's original
        // (possibly null/default) request -- so invoke can never resolve a different
        // version than the one C# just validated the payload against.
        var envelope = await InvokeAsync(
            connection, transaction, moduleName, actionName, manifest.Version, context, payload, timeoutMs, cancellationToken);

        var status = envelope["status"]?.GetValue<string>();
        if (status == "error")
        {
            var code = envelope["code"]?.GetValue<string>() ?? "internal.error";
            var message = envelope["message"]?.GetValue<string>();
            var retryable = envelope["retryable"]?.GetValue<bool>() ?? false;

            return new ActionPipelineOutcome(
                ActionExecutionResult.Failure(ActionError.ForCode(code, message, retryable), manifest.Version), true);
        }

        var outcome = envelope["outcome"]?.GetValue<string>();
        var result = envelope["result"];

        if (status != "ok" || outcome is null || result is null || !manifest.Outcomes.Contains(outcome))
        {
            return Rejected(ActionError.ContractViolation(), manifest.Version);
        }

        var resultValidation = validator.Validate(manifest.ResponseSchema, result);
        if (!resultValidation.IsValid)
        {
            return Rejected(ActionError.ContractViolation(), manifest.Version);
        }

        // Only a replayed envelope carries correlationId: api.invoke stamps it onto
        // the stored copy (migration 018), never onto a fresh target result.
        var success = envelope["correlationId"]?.GetValue<string>() is { } replayed && Guid.TryParse(replayed, out var original)
            ? ActionExecutionResult.Replay(manifest.Version, outcome, result, original)
            : ActionExecutionResult.Success(manifest.Version, outcome, result);

        return new ActionPipelineOutcome(success, false);
    }

    private static async Task<RuntimeManifest?> ReadManifestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string moduleName, string actionName, int? explicitVersion,
        CancellationToken cancellationToken)
    {
        var document = await ExecuteScalarJsonAsync(
            connection,
            transaction,
            "select api.get_action_manifest(@module, @action, @version)",
            command =>
            {
                command.Parameters.AddWithValue("module", moduleName);
                command.Parameters.AddWithValue("action", actionName);
                command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer)
                {
                    Value = (object?)explicitVersion ?? DBNull.Value,
                });
            },
            cancellationToken);

        if (document is null)
        {
            return null;
        }

        return new RuntimeManifest(
            Version: document["version"]!.GetValue<int>(),
            IdempotencyMode: document["idempotencyMode"]!.GetValue<string>(),
            RequiredPolicy: document["requiredPolicy"]!.AsArray().Select(node => node!.GetValue<string>()).ToList(),
            RequestSchema: document["requestSchema"]!,
            ResponseSchema: document["responseSchema"]!,
            Outcomes: document["outcomes"]!.AsArray().Select(node => node!.GetValue<string>()).ToList(),
            TimeoutMs: document["timeoutMs"]!.GetValue<int>());
    }

    // SET LOCAL does not accept bound parameters; timeoutMs is a trusted integer read
    // back from JsonNode.GetValue<int>() a moment ago (catalog data, never request
    // input), so interpolating it here carries no injection risk.
    private static async Task SetStatementTimeoutAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int timeoutMs, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"set local statement_timeout = {timeoutMs}", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<JsonNode> InvokeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string moduleName, string actionName, int version,
        TrustedContext context, JsonNode payload, int timeoutMs, CancellationToken cancellationToken)
    {
        var contextJson = new JsonObject
        {
            ["principal"] = context.Principal,
            ["consumer"] = context.Consumer,
            ["scopes"] = new JsonArray(context.Scopes.Select(scope => (JsonNode)scope).ToArray()),
            ["correlationId"] = context.CorrelationId.ToString(),
            ["requestId"] = context.RequestId,
            ["deadline"] = context.Deadline.ToString("o"),
        };

        // Only present when the caller set them (the workflow worker), so the
        // network path serializes byte-identical context to before.
        if (context.ProcessId is { } processId)
        {
            contextJson["processId"] = processId.ToString();
        }

        if (context.JobId is { } jobId)
        {
            contextJson["jobId"] = jobId.ToString();
        }

        if (context.ExecutionId is { } executionId)
        {
            contextJson["executionId"] = executionId;
        }

        if (context.AttemptId is { } attemptId)
        {
            contextJson["attemptId"] = attemptId.ToString();
        }

        // Nested, not flat: SPEC "Общая граница подписи" fixes the shape as
        // {"transport": {"signatureVerified": true, "signatureVersion": N}}.
        // Absent whenever no signature was verified, same "only when set" rule
        // as every field above -- an action that never asks for it sees no
        // context-shape change at all.
        if (context.SignatureVersion is { } signatureVersion)
        {
            contextJson["transport"] = new JsonObject
            {
                ["signatureVerified"] = true,
                ["signatureVersion"] = signatureVersion,
            };
        }

        // A sibling of transport, not a third field inside it (SPEC's own
        // "только два маркера" for a verified signature stays literally true).
        // Co-occurs with transport always (both come from the same verified
        // signature), so a target that already requires transport.signatureVerified
        // (ticket 08's receipt.accept) can rely on this being present too --
        // the exact SHA-256 of the same bytes the HMAC ran over, never a hash
        // of the parsed-and-reserialized payload.
        if (context.SignatureBodyHashHex is { } signatureBodyHashHex)
        {
            contextJson["rawBodyHash"] = signatureBodyHashHex;
        }

        var envelope = await ExecuteScalarJsonAsync(
            connection,
            transaction,
            "select api.invoke(@module, @action, @version, @context, @payload)",
            command =>
            {
                // Client-side backstop alongside statement_timeout (server) and
                // context.Deadline (informational): three expressions of one timeout_ms.
                // The +2s margin (ticket 08's own "клиентский предел с запасом")
                // keeps the client strictly behind the server: without it the two
                // round to the same second and a slow network hop could trip the
                // client first, which would cancel the command asynchronously while
                // the server-side transaction is still deciding on its own.
                command.CommandTimeout = (int)Math.Ceiling(timeoutMs / 1000.0) + 2;
                command.Parameters.AddWithValue("module", moduleName);
                command.Parameters.AddWithValue("action", actionName);
                command.Parameters.AddWithValue("version", version);
                command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb) { Value = contextJson.ToJsonString() });
                command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.ToJsonString() });
            },
            cancellationToken);

        // api.invoke always returns a jsonb envelope, never sql null (migration 003).
        return envelope!;
    }

    private static async Task<JsonNode?> ExecuteScalarJsonAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlCommand> configureCommand,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        configureCommand(command);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        return raw is string json ? JsonNode.Parse(json) : null;
    }

    private sealed record RuntimeManifest(
        int Version,
        string IdempotencyMode,
        IReadOnlyList<string> RequiredPolicy,
        JsonNode RequestSchema,
        JsonNode ResponseSchema,
        IReadOnlyList<string> Outcomes,
        int TimeoutMs);
}
