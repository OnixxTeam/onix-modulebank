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
        await ActionEnvelopeWriter.WriteJsonAsync(httpContext, StatusCodes.Status200OK, document);
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

        await ActionEnvelopeWriter.WriteJsonAsync(httpContext, StatusCodes.Status200OK, document);
    }
}
