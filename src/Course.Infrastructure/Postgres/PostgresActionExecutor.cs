using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Context;
using Course.Core.Errors;
using Course.Core.Validation;
using Npgsql;
using NpgsqlTypes;

namespace Course.Infrastructure.Postgres;

// The entire step 5-13 pipeline from spec §5.1 (BEGIN .. manifest .. idempotency/policy
// checks .. statement timeout .. request validation .. api.invoke .. envelope/response
// validation .. COMMIT) lives in this one method. Every early return rolls the
// transaction back via `await using` -- except the status=="error" branch below, which
// commits first: migration 015's api.invoke already rolled the domain effect back to a
// savepoint for that case, leaving only the dispatch row to persist as the technical
// audit trail (contract-reference: "Target вернул status=error" row).
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
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var manifest = await ReadManifestAsync(connection, transaction, moduleName, actionName, explicitVersion, cancellationToken);
        if (manifest is null)
        {
            return ActionExecutionResult.Failure(ActionError.ActionNotFound());
        }

        if (manifest.IdempotencyMode == "required" && requestId is null)
        {
            return ActionExecutionResult.Failure(ActionError.IdempotencyRequired(), manifest.Version);
        }

        if (!manifest.RequiredPolicy.All(claims.Scopes.Contains))
        {
            return ActionExecutionResult.Failure(ActionError.AccessDenied(), manifest.Version);
        }

        await SetStatementTimeoutAsync(connection, transaction, manifest.TimeoutMs, cancellationToken);

        var payloadValidation = validator.Validate(manifest.RequestSchema, payload);
        if (!payloadValidation.IsValid)
        {
            return ActionExecutionResult.Failure(
                ActionError.PayloadInvalid(string.Join("; ", payloadValidation.Errors)), manifest.Version);
        }

        var context = new TrustedContext(
            claims.Principal,
            claims.Consumer,
            claims.Scopes,
            correlationId,
            requestId,
            timeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(manifest.TimeoutMs));

        // Pass the version the manifest actually resolved to, not the caller's original
        // (possibly null/default) request -- so invoke can never resolve a different
        // version than the one C# just validated the payload against.
        var envelope = await InvokeAsync(
            connection, transaction, moduleName, actionName, manifest.Version, context, payload, manifest.TimeoutMs, cancellationToken);

        var status = envelope["status"]?.GetValue<string>();
        if (status == "error")
        {
            var code = envelope["code"]?.GetValue<string>() ?? "internal.error";
            var message = envelope["message"]?.GetValue<string>();
            var retryable = envelope["retryable"]?.GetValue<bool>() ?? false;

            // api.invoke already rolled the domain effect (and any idempotency claim)
            // back to a savepoint for this branch, so all that is left in the
            // transaction is the dispatch row recording the technical failure --
            // commit to keep it instead of discarding it with everything else.
            await transaction.CommitAsync(cancellationToken);
            return ActionExecutionResult.Failure(ActionError.ForCode(code, message, retryable), manifest.Version);
        }

        var outcome = envelope["outcome"]?.GetValue<string>();
        var result = envelope["result"];

        if (status != "ok" || outcome is null || result is null || !manifest.Outcomes.Contains(outcome))
        {
            return ActionExecutionResult.Failure(ActionError.ContractViolation(), manifest.Version);
        }

        var resultValidation = validator.Validate(manifest.ResponseSchema, result);
        if (!resultValidation.IsValid)
        {
            return ActionExecutionResult.Failure(ActionError.ContractViolation(), manifest.Version);
        }

        await transaction.CommitAsync(cancellationToken);

        // Only a replayed envelope carries correlationId: api.invoke stamps it onto
        // the stored copy (migration 018), never onto a fresh target result.
        return envelope["correlationId"]?.GetValue<string>() is { } replayed && Guid.TryParse(replayed, out var original)
            ? ActionExecutionResult.Replay(manifest.Version, outcome, result, original)
            : ActionExecutionResult.Success(manifest.Version, outcome, result);
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

        var envelope = await ExecuteScalarJsonAsync(
            connection,
            transaction,
            "select api.invoke(@module, @action, @version, @context, @payload)",
            command =>
            {
                // Client-side backstop alongside statement_timeout (server) and
                // context.Deadline (informational): three expressions of one timeout_ms.
                command.CommandTimeout = (int)Math.Ceiling(timeoutMs / 1000.0);
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
