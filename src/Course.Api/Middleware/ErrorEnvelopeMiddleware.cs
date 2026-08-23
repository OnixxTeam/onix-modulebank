using Course.Api.Http;
using Course.Infrastructure.Postgres;

namespace Course.Api.Middleware;

// The unified error handler (contract-reference, error matrix): every exception
// that escapes IActionExecutor.ExecuteAsync -- connectivity loss, statement
// timeout, or anything truly unexpected -- lands here and is mapped through
// NpgsqlErrorMapper. meta.actionVersion is always null on this path (the
// exception could have happened before the manifest was even resolved); the
// exception's message, type and stack trace never reach the client, only the
// log, keyed by correlationId.
public sealed partial class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items.TryGetValue(ActionEnvelopeWriter.CorrelationIdItemKey, out var value)
                ? (Guid?)value
                : null;

            LogUnhandledException(ex, context.Request.Path, correlationId);

            if (context.Response.HasStarted)
            {
                return;
            }

            await ActionEnvelopeWriter.WriteErrorAsync(context, NpgsqlErrorMapper.Map(ex), correlationId, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "unhandled exception while serving {Path}, correlationId={CorrelationId}")]
    private partial void LogUnhandledException(Exception exception, string path, Guid? correlationId);
}
