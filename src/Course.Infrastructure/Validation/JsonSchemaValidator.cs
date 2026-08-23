using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Core.Validation;
using Json.Schema;

namespace Course.Infrastructure.Validation;

public sealed class JsonSchemaValidator : ISchemaValidator
{
    // RequireFormatValidation is turned on here, once, for every consumer of this
    // validator: in draft 2020-12 "format" is an annotation by default, so a
    // uuid-shaped field would otherwise pass validation and fail later as a 500
    // instead of a 422.
    private static readonly EvaluationOptions Options = new()
    {
        RequireFormatValidation = true,
        OutputFormat = OutputFormat.List,
    };

    // A schema that declares $id registers into Json.Schema.Net's process-wide
    // SchemaRegistry on every Deserialize<JsonSchema>() call, and a second
    // registration of the same $id throws -- which repeated action manifests
    // (bootstrap publishing several in one process) and repeated requests
    // against the same action (a fresh JsonNode from the catalog every call)
    // both do. Compiling each distinct schema exactly once avoids re-registering.
    private readonly ConcurrentDictionary<string, JsonSchema> compiledSchemas = new();

    public SchemaValidationResult Validate(JsonNode schema, JsonNode instance)
    {
        var compiled = compiledSchemas.GetOrAdd(schema.ToJsonString(), CompileSchema);
        var element = instance.Deserialize<JsonElement>();
        var result = compiled.Evaluate(element, Options);

        if (result.IsValid)
        {
            return SchemaValidationResult.Success;
        }

        var errors = (result.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
            .ToList();

        return SchemaValidationResult.Failure(errors);
    }

    private static JsonSchema CompileSchema(string schemaJson) =>
        JsonNode.Parse(schemaJson)!.Deserialize<JsonSchema>()
            ?? throw new InvalidOperationException("schema document did not deserialize");
}
