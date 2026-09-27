using System.Globalization;
using Course.Core.Diagnostics;
using Course.Infrastructure.Postgres;
using Course.Infrastructure.Validation;
using Course.Observability;
using Npgsql;
using Workflow.Core.Security;

namespace Workflow.Worker;

// An explicit, namespaced entry point rather than top-level statements: since
// this process serves /health/* and /metrics it is an ASP.NET project, and the
// generated global `Program` of two ASP.NET projects in one solution collides in
// the test assembly that references both (Microsoft.AspNetCore.Mvc.Testing makes
// each one's internal Program visible to it). Naming the class here keeps
// Course.Api's `Program` the only one, so ApiFactory needs no workaround.
//
// Workflow.Worker.csproj sets StartupObject to this type.
internal static class WorkerHost
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Framework request logging defaults to Debug and would otherwise write
        // headers to the container log; Warning keeps it out, same as Course.Api.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // COURSE_DB_HOST/PORT/NAME mirror Course.Cli's own BuildConnectionString --
        // the worker connects the same way the CLI does, just permanently and under
        // workflow_worker instead of an admin role.
        //
        // owner doubles as the failpoint ack's instanceId and (in compose) the
        // service name -- SPEC "instanceId в ack == lease owner == имя Compose-
        // сервиса", all one value from this one variable.
        var owner = RequireEnv("WORKFLOW_WORKER_OWNER");
        var connectionString = BuildConnectionString();
        var intervals = BuildIntervalProfile();
        var failpoints = Failpoints.FromEnvironment(owner, Console.Out);

        // One JsonSchemaValidator for the whole process: a second instance compiling
        // the same catalog schema text (the loop's pre-check and the executor's own
        // internal one) would try to register it a second time in Json.Schema.Net's
        // process-wide SchemaRegistry, which throws on the second registration.
        var schemaValidator = new JsonSchemaValidator();

        var dataSource = NpgsqlDataSource.Create(connectionString);
        // Explicitly no gate: this process's own two failpoints are on WorkerLoop,
        // and the loop drives ExecuteInTransactionAsync, never ExecuteAsync -- so the
        // two boundaries the executor owns are unreachable from here by construction.
        var actionExecutor = new PostgresActionExecutor(
            dataSource, schemaValidator, TimeProvider.System, NoopFailpointGate.Instance);
        var jobRuntime = new PostgresWorkflowJobRuntime(connectionString);

        // The host's own logger, not the default NullLogger: without this argument the
        // structured step line (ticket 10) exists only in tests, and the claim-failure
        // line disappears entirely. Built before app.Build() because the loop is
        // constructed here, so it comes from a throwaway factory that shares this
        // process's logging configuration.
        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddJsonConsole();
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        });

        var loop = new WorkerLoop(
            owner,
            dataSource,
            jobRuntime,
            actionExecutor,
            schemaValidator,
            TimeProvider.System,
            intervals,
            WorkerScopes(),
            failpoints,
            loggerFactory.CreateLogger<WorkerLoop>());

        // The data source is registered so the host disposes it on shutdown, which
        // is what the previous `await using` did.
        builder.Services.AddSingleton(dataSource);

        // The loop runs as a hosted service, on its own task: a worker parked on a
        // failpoint blocks that task and nothing else, so /health/live keeps
        // answering 200 while the verifying run waits for the acknowledgement it
        // just read and then stops the container.
        builder.Services.AddSingleton(loop);
        builder.Services.AddHostedService<WorkerLoopService>();

        var app = builder.Build();

        // PostgreSQL is this process's only critical dependency (the rule itself is
        // stated once, on ObservabilityOptions).
        app.MapObservability(ObservabilityOptions.WithoutMetrics(PostgresReadiness.Probe(dataSource)));

        await app.RunAsync();
    }

    private static string[] WorkerScopes()
    {
        var raw = Environment.GetEnvironmentVariable("WORKFLOW_WORKER_SCOPES");
        return string.IsNullOrEmpty(raw)
            ? [.. WorkflowWorkerPrincipal.DefaultScopes]
            : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // COURSE_TEST_PROFILE=1 selects the test defaults (lease 2s, poll <=100ms);
    // anything else keeps the conservative production defaults (lease 30s, poll
    // 1s). COURSE_JOB_LEASE_MS and COURSE_WORKER_POLL_MS are the published names
    // and override their own value regardless of profile. The older
    // WORKFLOW_WORKER_LEASE_MS/_POLL_MS are kept as aliases for a local override
    // written before week 4; nothing tracked in this repository sets them.
    private static IntervalProfile BuildIntervalProfile()
    {
        var testProfile = Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE") == "1";
        var defaultLeaseMs = testProfile ? 2_000 : 30_000;
        var defaultPollMs = testProfile ? 100 : 1_000;

        return new IntervalProfile(
            LeaseMs: GetEnvInt("COURSE_JOB_LEASE_MS") ?? GetEnvInt("WORKFLOW_WORKER_LEASE_MS") ?? defaultLeaseMs,
            PollIntervalMs: GetEnvInt("COURSE_WORKER_POLL_MS") ?? GetEnvInt("WORKFLOW_WORKER_POLL_MS") ?? defaultPollMs);
    }

    private static int? GetEnvInt(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(raw) ? null : int.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

    // The role and its password both come from configuration under the names the
    // published table uses. COURSE_DB_USER is what makes this process's database
    // principal readable from the Compose file rather than only from this source
    // file, which is how least privilege is verified from outside; it still
    // defaults to the only role a worker may connect as.
    private static string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("COURSE_DB_HOST") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("COURSE_DB_PORT") ?? "5432";
        var database = Environment.GetEnvironmentVariable("COURSE_DB_NAME") ?? "course";
        var username = Environment.GetEnvironmentVariable("COURSE_DB_USER") ?? "workflow_worker";
        var password = RequireEnv("COURSE_WORKER_PASSWORD");

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = int.Parse(port, CultureInfo.InvariantCulture),
            Database = database,
            Username = username,
            Password = password,
        }.ConnectionString;

        return PostgresConnectionString.WithoutGssNegotiation(connectionString);
    }
}

// Runs the claim loop for as long as the host lives. RunAsync only returns when
// the token is cancelled (a container stop), so a return here is a shutdown, not
// a crash.
internal sealed class WorkerLoopService(WorkerLoop loop) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => loop.RunAsync(stoppingToken);
}
