using System.Text.Json;
using System.Text.Json.Serialization;

namespace Course.Core.Cli;

public sealed record CliMeta(string ContractVersion);

public sealed class CliEnvelope
{
    private const string ContractVersion = "course-1";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string Status { get; init; }
    public object? Result { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public required CliMeta Meta { get; init; }

    public static CliEnvelope Ok(object result) => new()
    {
        Status = "ok",
        Result = result,
        Meta = new CliMeta(ContractVersion),
    };

    public static CliEnvelope Fail(string code, string message) => new()
    {
        Status = "error",
        Code = code,
        Message = message,
        Meta = new CliMeta(ContractVersion),
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
