using System.Text.Json.Nodes;

namespace Course.Core.Validation;

// One validator, shared by every schema consumer (manifest, request payload,
// response result) so format validation (uuid, etc.) is enabled in exactly
// one place rather than re-configured per call site.
public interface ISchemaValidator
{
    SchemaValidationResult Validate(JsonNode schema, JsonNode instance);
}

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static SchemaValidationResult Success { get; } = new(true, []);

    public static SchemaValidationResult Failure(IReadOnlyList<string> errors) => new(false, errors);
}
