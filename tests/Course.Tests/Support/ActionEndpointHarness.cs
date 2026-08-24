using System.Text;
using Course.Api.Endpoints;
using Course.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Course.Tests.Support;

// Calls ActionEndpoint.HandleAsync directly against a hand-built HttpContext,
// without a running Kestrel/TestServer -- the handler is a static method that
// only depends on HttpContext plus the two injected interfaces, so this is a
// true unit-level call, not a network round trip.
internal static class ActionEndpointHarness
{
    public static async Task<HarnessResult> PostAsync(
        string path, string body, IJwtAuthenticator jwtFactory, RecordingActionExecutor executor, string? actionVersionHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers.Authorization = "Bearer stub-token";

        if (actionVersionHeader is not null)
        {
            context.Request.Headers["X-Action-Version"] = actionVersionHeader;
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await ActionEndpoint.HandleAsync(context, executor, jwtFactory, NullLogger<ActionEndpointLog>.Instance);

        responseBody.Seek(0, SeekOrigin.Begin);
        var text = await new StreamReader(responseBody).ReadToEndAsync();
        return new HarnessResult(context.Response.StatusCode, text);
    }
}

internal sealed record HarnessResult(int StatusCode, string Body);
