using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Course.Api.Http;
using Course.Core.Actions;
using Course.Core.Errors;
using Course.Core.Security;

namespace Course.Api.Endpoints;

// The one generic route for every action (POST /api/{module}/{action}): no
// per-action controller exists, so a newly published action is callable without
// rebuilding this image. Only HTTP-shaped concerns live here (route/header/JSON
// parsing, auth, envelope writing) -- everything transactional is IActionExecutor's.
public static partial class ActionEndpoint
{
    public static async Task HandleAsync(
        HttpContext httpContext, IActionExecutor executor, IJwtAuthenticator jwtFactory, ILogger<ActionEndpointLog> logger)
    {
        var cancellationToken = httpContext.RequestAborted;

        // Exactly two segments after /api; segments[0] is always "api" (the route
        // prefix that got us here at all).
        var segments = httpContext.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(
                httpContext, ActionError.RequestInvalid("route must be /api/{module}/{action}"), null, null);
            return;
        }

        var module = segments[1];
        var action = segments[2];

        var authResult = await jwtFactory.AuthenticateAsync(httpContext.Request.Headers.Authorization, cancellationToken);
        if (authResult.Error is not null)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(httpContext, authResult.Error, null, null);
            return;
        }

        // meta.correlationId is required from here on (contract-reference: "obligatory
        // after successful authentication"), so stash it for ErrorEnvelopeMiddleware too.
        var correlationId = Guid.NewGuid();
        httpContext.Items[ActionEnvelopeWriter.CorrelationIdItemKey] = correlationId;

        int? explicitVersion = null;
        if (httpContext.Request.Headers.TryGetValue("X-Action-Version", out var versionHeader))
        {
            if (!int.TryParse(versionHeader, out var parsedVersion) || parsedVersion < 1)
            {
                await ActionEnvelopeWriter.WriteErrorAsync(
                    httpContext, ActionError.RequestInvalid("X-Action-Version must be an integer >= 1"), correlationId, null);
                return;
            }

            explicitVersion = parsedVersion;
        }

        JsonNode? payload;
        try
        {
            payload = await JsonNode.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(
                httpContext, ActionError.RequestInvalid("request body is not valid JSON"), correlationId, null);
            return;
        }

        if (payload is null)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(
                httpContext, ActionError.RequestInvalid("request body must be a JSON document"), correlationId, null);
            return;
        }

        var requestId = httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null;

        var stopwatch = Stopwatch.StartNew();
        var result = await executor.ExecuteAsync(
            module, action, explicitVersion, authResult.Claims!, correlationId, requestId, payload, cancellationToken);
        stopwatch.Stop();

        // Whitelisted fields only (spec §5, "Логи"): never the token, the payload
        // itself, or anything else that didn't make this list. Hashing is skipped
        // entirely when this level isn't enabled, not just the log write.
        if (logger.IsEnabled(LogLevel.Information))
        {
            var payloadHash = HashPayload(payload);
            LogDispatch(
                logger, correlationId, module, action, result.ActionVersion, authResult.Claims!.Principal,
                result.Outcome, result.IsSuccess ? "OK" : "ERROR", stopwatch.ElapsedMilliseconds, payloadHash);
        }

        if (!result.IsSuccess)
        {
            await ActionEnvelopeWriter.WriteErrorAsync(httpContext, result.Error!, correlationId, result.ActionVersion);
            return;
        }

        // A replay answers with the first call's envelope, so meta carries that
        // call's correlationId; the log above still reports this request's own.
        await ActionEnvelopeWriter.WriteSuccessAsync(
            httpContext, result.Outcome!, result.Result!, result.ReplayedCorrelationId ?? correlationId, result.ActionVersion!.Value);
    }

    private static string HashPayload(JsonNode payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString())));

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "correlationId={CorrelationId} module={Module} action={Action} version={Version} principal={Principal} "
            + "outcome={Outcome} status={Status} duration_ms={DurationMs} payload_hash={PayloadHash}")]
    private static partial void LogDispatch(
        ILogger logger, Guid correlationId, string module, string action, int? version, string principal,
        string? outcome, string status, long durationMs, string payloadHash);
}

// ActionEndpoint is static, which can't be used as an ILogger<T> category, hence
// this marker type purely to name the logging category "Course.Api.Endpoints.ActionEndpointLog".
public sealed class ActionEndpointLog;
