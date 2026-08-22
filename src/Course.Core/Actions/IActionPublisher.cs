using System.Text.Json.Nodes;

namespace Course.Core.Actions;

// Both operations forward the jsonb result Postgres returns as-is: publish_action
// and list_actions already produce the exact envelope/result shape the CLI needs,
// so there is no C# type to keep in sync with the SQL side.
public interface IActionPublisher
{
    Task<JsonNode> PublishAsync(ActionManifest manifest, CancellationToken cancellationToken);

    Task<JsonNode> ListAsync(CancellationToken cancellationToken);
}
