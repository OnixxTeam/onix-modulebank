using System.Text.Json.Nodes;
using Course.Core.OpenApi;
using Npgsql;
using NpgsqlTypes;

namespace Course.Infrastructure.OpenApi;

// No Swashbuckle/NSwag: they generate a document from C# types and controller
// attributes, and this API has one generic route with no per-action types. The
// document is assembled by hand as a JsonNode straight from the catalog, with
// request_schema/response_schema inserted verbatim since they are already jsonb.
public sealed class CatalogOpenApiBuilder(NpgsqlDataSource dataSource) : IOpenApiBuilder
{
    public async Task<JsonNode> BuildDefaultDocumentAsync(CancellationToken cancellationToken)
    {
        var rows = await ExecuteScalarJsonAsync(
            "select api.openapi_default_actions()", static _ => { }, cancellationToken);

        var document = NewDocument();
        var paths = (JsonObject)document["paths"]!;

        // api.openapi_default_actions() coalesces to '[]'::jsonb, never sql null
        // (migration 014), so rows is always a JsonArray here.
        foreach (var row in rows!.AsArray())
        {
            var (path, operation) = BuildPathAndOperation(row!);
            paths[path] = operation;
        }

        return document;
    }

    public async Task<JsonNode?> BuildVersionDocumentAsync(
        string moduleName, string actionName, int version, CancellationToken cancellationToken)
    {
        var row = await ExecuteScalarJsonAsync(
            "select api.openapi_action_version(@module, @action, @version)",
            command =>
            {
                command.Parameters.AddWithValue("module", moduleName);
                command.Parameters.AddWithValue("action", actionName);
                command.Parameters.AddWithValue("version", version);
            },
            cancellationToken);

        if (row is null)
        {
            return null;
        }

        var document = NewDocument();
        var (path, operation) = BuildPathAndOperation(row);
        ((JsonObject)document["paths"]!)[path] = operation;
        return document;
    }

    private static (string Path, JsonObject Operation) BuildPathAndOperation(JsonNode row)
    {
        var module = row["module"]!.GetValue<string>();
        var action = row["action"]!.GetValue<string>();

        var operation = new JsonObject
        {
            ["post"] = new JsonObject
            {
                ["x-outcomes"] = row["outcomes"]!.DeepClone(),
                // The only version selector a caller has (contract-reference:
                // "Версия передаётся только в X-Action-Version"), pinned to the
                // version this operation was built from: without it the default
                // document and the exact-version documents describe different
                // contracts under the same path with nothing to tell them apart.
                // Optional, because omitting the header selects the default.
                ["parameters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "X-Action-Version",
                        ["in"] = "header",
                        ["required"] = false,
                        ["schema"] = new JsonObject { ["const"] = row["version"]!.GetValue<int>() },
                    },
                },
                ["requestBody"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = row["requestSchema"]!.DeepClone() },
                    },
                },
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject { ["schema"] = row["responseSchema"]!.DeepClone() },
                        },
                    },
                },
            },
        };

        return ($"/api/{module}/{action}", operation);
    }

    private static JsonObject NewDocument() =>
        new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "Course actions", ["version"] = "1" },
            ["paths"] = new JsonObject(),
        };

    private async Task<JsonNode?> ExecuteScalarJsonAsync(
        string sql, Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        addParameters(command);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        return raw is string json ? JsonNode.Parse(json) : null;
    }
}
