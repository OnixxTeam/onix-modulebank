using System.Text;
using Course.Api.Endpoints;
using Course.Core.Security;
using Course.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Course.Tests.Support;

// Calls ActionEndpoint.HandleAsync directly against a hand-built HttpContext,
// without a running Kestrel/TestServer -- the handler is a static method that
// only depends on HttpContext plus the injected interfaces, so this is a
// true unit-level call, not a network round trip.
internal static class ActionEndpointHarness
{
    // A fixed secret the real ProviderSignatureVerifier signs/verifies with --
    // reusing the real implementation here (not a fake) since it is pure,
    // in-memory HMAC logic with no I/O, exactly what these tests want to
    // exercise (SignatureBoundaryTests supplies its own valid/invalid headers
    // against this same secret).
    public static readonly ISignatureVerifier DefaultSignatureVerifier = new ProviderSignatureVerifier(Encoding.UTF8.GetBytes("harness-hmac-secret"));

    public static async Task<HarnessResult> PostAsync(
        string path, string body, IJwtAuthenticator jwtFactory, RecordingActionExecutor executor, string? actionVersionHeader = null,
        ISignatureVerifier? signatureVerifier = null, string? signatureHeader = null)
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

        if (signatureHeader is not null)
        {
            context.Request.Headers["X-Provider-Signature"] = signatureHeader;
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await ActionEndpoint.HandleAsync(
            context, executor, jwtFactory, signatureVerifier ?? DefaultSignatureVerifier, NullLogger<ActionEndpointLog>.Instance);

        responseBody.Seek(0, SeekOrigin.Begin);
        var text = await new StreamReader(responseBody).ReadToEndAsync();
        return new HarnessResult(context.Response.StatusCode, text, context);
    }
}

internal sealed record HarnessResult(int StatusCode, string Body, HttpContext Context);
