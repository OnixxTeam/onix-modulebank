using System.Text.Json.Nodes;

namespace Course.Core.OpenApi;

// Both documents are assembled from the catalog on every call, never cached
// (spec §5.1, §7): activate/disable are reflected on the very next request
// with no rebuild, so there is nothing to invalidate.
public interface IOpenApiBuilder
{
    Task<JsonNode> BuildDefaultDocumentAsync(CancellationToken cancellationToken);

    Task<JsonNode?> BuildVersionDocumentAsync(
        string moduleName, string actionName, int version, CancellationToken cancellationToken);
}
