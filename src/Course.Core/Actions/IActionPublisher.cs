using System.Text.Json.Nodes;

namespace Course.Core.Actions;

// All four operations forward the jsonb result Postgres returns as-is: the
// SQL functions already produce the exact envelope/result shape the CLI
// needs, so there is no C# type to keep in sync with the SQL side.
public interface IActionPublisher
{
    Task<JsonNode> PublishAsync(ActionManifest manifest, CancellationToken cancellationToken);

    Task<JsonNode> ListAsync(CancellationToken cancellationToken);

    Task<JsonNode> ActivateAsync(string moduleName, string actionName, int version, CancellationToken cancellationToken);

    Task<JsonNode> DisableAsync(
        string moduleName, string actionName, int version, int? replacementVersion, CancellationToken cancellationToken);
}
