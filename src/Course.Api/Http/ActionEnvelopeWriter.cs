using System.Text.Json.Nodes;
using Course.Core.Errors;

namespace Course.Api.Http;

// Builds the exact success/error envelope shapes from contract-reference, "HTTP API",
// used by both ActionEndpoint (known failures) and ErrorEnvelopeMiddleware (unhandled
// exceptions) so there is one place that knows the wire format.
public static class ActionEnvelopeWriter
{
    public const string CorrelationIdItemKey = "trusted.correlationId";

    public static Task WriteSuccessAsync(
        HttpContext context, string outcome, JsonNode result, Guid correlationId, int actionVersion)
    {
        var body = new JsonObject
        {
            ["status"] = "ok",
            ["outcome"] = outcome,
            ["result"] = result.DeepClone(),
            ["meta"] = new JsonObject
            {
                ["correlationId"] = correlationId.ToString(),
                ["actionVersion"] = actionVersion,
            },
        };

        return WriteAsync(context, 200, body);
    }

    public static Task WriteErrorAsync(HttpContext context, ActionError error, Guid? correlationId, int? actionVersion)
    {
        var body = new JsonObject
        {
            ["status"] = "error",
            ["code"] = error.Code,
            ["message"] = error.Message,
            ["retryable"] = error.Retryable,
            ["details"] = error.Details?.DeepClone() ?? new JsonObject(),
            ["meta"] = new JsonObject
            {
                ["correlationId"] = correlationId?.ToString(),
                ["actionVersion"] = actionVersion,
            },
        };

        return WriteAsync(context, error.HttpStatus, body);
    }

    // Not just the two action envelope shapes: OpenApiEndpoints writes plain documents
    // through this same method, so there is still only one place in the process that
    // sets the JSON content type and serializes the response body.
    public static Task WriteJsonAsync(HttpContext context, int statusCode, JsonNode body) =>
        WriteAsync(context, statusCode, body);

    private static async Task WriteAsync(HttpContext context, int statusCode, JsonNode body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body.ToJsonString());
    }
}
