using System.Text;
using Course.Api.Endpoints;
using Course.Api.Middleware;
using Course.Core.Actions;
using Course.Core.OpenApi;
using Course.Core.Security;
using Course.Core.Validation;
using Course.Infrastructure.OpenApi;
using Course.Infrastructure.Postgres;
using Course.Infrastructure.Security;
using Course.Infrastructure.Validation;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Framework request logging defaults to Debug and would otherwise write headers
// (including Authorization) to the log; Warning keeps it out without needing a
// per-property redaction list.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var jwtOptions = new JwtOptions(
    Issuer: RequireEnv("COURSE_JWT_ISSUER"),
    Audience: RequireEnv("COURSE_JWT_AUDIENCE"),
    SigningKey: RequireEnv("COURSE_JWT_SIGNING_KEY"));

// Ticket 07: exact UTF-8 bytes of the env var, never trimmed or decoded --
// the most common way to get the signature boundary wrong (SPEC "Обработать
// секрет как закодированный"). Missing entirely is a startup failure via
// RequireEnv, not a silently-open boundary (SPEC "Отсутствие секрета в
// конфигурации приводит к явному отказу на старте").
var providerHmacSecret = Encoding.UTF8.GetBytes(RequireEnv("PROVIDER_HMAC_SECRET"));

// One NpgsqlDataSource for the process lifetime; connections are taken from it
// per request/health-check, never cached, so readiness recovers on its own once
// PostgreSQL comes back -- no state to reset after a stop/start cycle.
var runtimeDataSource = NpgsqlDataSource.Create(
    PostgresConnectionString.WithoutGssNegotiation(RequireEnv("COURSE_RUNTIME_DSN")));

builder.Services.AddSingleton(runtimeDataSource);
builder.Services.AddSingleton<ISchemaValidator, JsonSchemaValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IActionExecutor, PostgresActionExecutor>();
builder.Services.AddSingleton<IOpenApiBuilder, CatalogOpenApiBuilder>();
builder.Services.AddSingleton<IJwtAuthenticator>(new JwtContextFactory(jwtOptions));
builder.Services.AddSingleton<ISignatureVerifier>(new ProviderSignatureVerifier(providerHmacSecret));

var app = builder.Build();

// This process has no async startup work of its own and does not wait on schema or
// built-in publication -- readiness flips as soon as the host has finished building,
// distinct from (and required alongside) the "select 1" dependency check below.
// Migrations/publication happen in the separate `bootstrap` service (compose
// depends_on: postgres, not this container) or, for the checker's own fixtures,
// through explicit `cli migration apply` / `cli action publish` calls -- neither is
// a startup precondition for `api` itself.
var readiness = new ReadinessState();
readiness.MarkInitialized();

app.UseMiddleware<ErrorEnvelopeMiddleware>();

app.MapGet("/health/live", () => Results.Ok());

app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    if (!readiness.IsInitialized)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return Results.Ok();
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/openapi/default.json", OpenApiEndpoints.HandleDefaultAsync);
app.MapGet("/openapi/actions/{module}/{action}/{version}.json", OpenApiEndpoints.HandleVersionAsync);

app.MapPost("/api/{*rest}", ActionEndpoint.HandleAsync);

app.Run();

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

internal sealed class ReadinessState
{
    private volatile bool isInitialized;

    public bool IsInitialized => isInitialized;

    public void MarkInitialized() => isInitialized = true;
}

// Top-level statements otherwise generate an internal Program class; WebApplicationFactory<Program>
// (Integration/ApiFactory.cs) needs a public one to boot this app in-process for testing.
public partial class Program;
