using System.Text.Json.Nodes;
using Course.Api.Http;
using Course.Core.Errors;
using Course.Core.OpenApi;

namespace Course.Api.Endpoints;

// Both routes are unauthenticated (contract-reference §7): they describe the
// published contract, not a trusted operation, so there is no JWT/correlationId
// here -- an unknown version still reports through the shared error envelope.
public static class OpenApiEndpoints
{
    public static async Task HandleDefaultAsync(HttpContext httpContext, IOpenApiBuilder builder)
    {
        var document = await builder.BuildDefaultDocumentAsync(httpContext.RequestAborted);
        await WriteJsonAsync(httpContext, document);
    }

    public static async Task HandleVersionAsync(
        HttpContext httpContext, IOpenApiBuilder builder, string module, string action, string version)
    {
        if (!int.TryParse(version, out var parsedVersion) || parsedVersion < 1)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(httpContext, ActionError.ActionNotFound(), null, null);
            return;
        }

        var document = await builder.BuildVersionDocumentAsync(module, action, parsedVersion, httpContext.RequestAborted);
        if (document is null)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(httpContext, ActionError.ActionNotFound(), null, null);
            return;
        }

        await WriteJsonAsync(httpContext, document);
    }

    private static async Task WriteJsonAsync(HttpContext httpContext, JsonNode document)
    {
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsync(document.ToJsonString());
    }
}
