using System.Text;
using Course.Api.Endpoints;
using Course.Api.Observability;
using Course.Api.Middleware;
using Course.Core.Actions;
using Course.Core.Diagnostics;
using Course.Core.OpenApi;
using Course.Core.Security;
using Course.Core.Validation;
using Course.Infrastructure.OpenApi;
using Course.Infrastructure.Postgres;
using Course.Infrastructure.Security;
using Course.Infrastructure.Validation;
using Course.Observability;
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
// Week 4, ticket 13: the two failpoints this process owns are on the generic
// action seam inside PostgresActionExecutor, so the gate is a constructor
// dependency rather than anything the endpoints know about. Outside
// COURSE_TEST_PROFILE=1 this is the no-op gate and there is no other way to arm
// one -- no endpoint activates a failpoint in any profile.
builder.Services.AddSingleton(Failpoints.FromEnvironment(Console.Out));
builder.Services.AddSingleton<IActionExecutor>(services => new PostgresActionExecutor(
    services.GetRequiredService<NpgsqlDataSource>(),
    services.GetRequiredService<ISchemaValidator>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<IFailpointGate>(),
    // The ack's instanceId is this process's Compose service name, the same rule
    // the worker follows with its lease owner.
    Environment.GetEnvironmentVariable("COURSE_SERVICE_NAME") ?? "api"));
builder.Services.AddSingleton<IOpenApiBuilder, CatalogOpenApiBuilder>();
builder.Services.AddSingleton<IJwtAuthenticator>(new JwtContextFactory(jwtOptions));
builder.Services.AddSingleton<ISignatureVerifier>(new ProviderSignatureVerifier(providerHmacSecret));

var app = builder.Build();

app.UseMiddleware<ErrorEnvelopeMiddleware>();

// Week 4, tickets 07 and 08: the three routes come from the one shared host every
// C# process uses, so the bodies, the media types and the OpenMetrics rendering
// cannot drift between `api` and the workers.
//
// PostgreSQL is the single readiness condition. There used to be a second one, an
// "initialized" flag, but it was set unconditionally one line after it was
// created, so it could never be false: this process has no async startup work and
// does not wait on schema or built-in publication (migrations and publication
// happen in the separate bootstrap path). A condition that cannot fail is not a
// condition.
//
// The six required series are one call to one registered function per scrape: the
// values have to agree with each other, and a second query would be a second
// snapshot.
//
// The one tolerated failure is "the snapshot function is not there yet": on a cold
// start bootstrap may not have applied migration 047, and an empty-but-valid
// document is a better answer than a failed scrape. Every other database error
// propagates, because a scrape that silently publishes none of the six required
// series is indistinguishable from a healthy empty queue.
var queueSnapshot = new PostgresQueueSnapshot(runtimeDataSource);

app.MapObservability(new ObservabilityOptions(
    PostgresReadiness.Probe(runtimeDataSource),
    async cancellationToken =>
    {
        try
        {
            return QueueMetrics.From(await queueSnapshot.ReadAsync(cancellationToken));
        }
        catch (PostgresException error) when (
            error.SqlState is PostgresErrorCodes.UndefinedFunction
                or PostgresErrorCodes.InvalidSchemaName
                or PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }));

app.MapGet("/openapi/default.json", OpenApiEndpoints.HandleDefaultAsync);
app.MapGet("/openapi/actions/{module}/{action}/{version}.json", OpenApiEndpoints.HandleVersionAsync);

app.MapPost("/api/{*rest}", ActionEndpoint.HandleAsync);

app.Run();

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

// Top-level statements otherwise generate an internal Program class; WebApplicationFactory<Program>
// (Integration/ApiFactory.cs) needs a public one to boot this app in-process for testing.
public partial class Program;
