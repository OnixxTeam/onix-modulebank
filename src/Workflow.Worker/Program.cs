using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Course.Infrastructure.Validation;
using Npgsql;
using Workflow.Core.Security;
using Workflow.Worker;

// COURSE_DB_HOST/PORT/NAME mirror Course.Cli's own BuildConnectionString --
// the worker connects the same way the CLI does, just permanently and under
// workflow_worker instead of an admin role. Production defaults (long lease,
// slow poll) are what ships until ticket 11 adds the test-profile switch.
var owner = RequireEnv("WORKFLOW_WORKER_OWNER");
var connectionString = BuildConnectionString("workflow_worker", "WORKFLOW_WORKER_PASSWORD");
var intervals = new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 1_000);

// One JsonSchemaValidator for the whole process: a second instance compiling
// the same catalog schema text (this pre-check below and the executor's own
// internal one) would try to register it a second time in Json.Schema.Net's
// process-wide SchemaRegistry, which throws on the second registration.
var schemaValidator = new JsonSchemaValidator();

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var actionExecutor = new PostgresActionExecutor(dataSource, schemaValidator, TimeProvider.System);
var jobRuntime = new PostgresWorkflowJobRuntime(connectionString);

var loop = new WorkerLoop(
    owner,
    dataSource,
    jobRuntime,
    actionExecutor,
    schemaValidator,
    TimeProvider.System,
    intervals,
    WorkerScopes());

await loop.RunAsync(CancellationToken.None);

static string[] WorkerScopes()
{
    var raw = Environment.GetEnvironmentVariable("WORKFLOW_WORKER_SCOPES");
    return string.IsNullOrEmpty(raw) ? [.. WorkflowWorkerPrincipal.DefaultScopes] : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

static string BuildConnectionString(string username, string passwordEnvironmentVariable)
{
    var host = Environment.GetEnvironmentVariable("COURSE_DB_HOST") ?? "postgres";
    var port = Environment.GetEnvironmentVariable("COURSE_DB_PORT") ?? "5432";
    var database = Environment.GetEnvironmentVariable("COURSE_DB_NAME") ?? "course";
    var password = RequireEnv(passwordEnvironmentVariable);

    var connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture),
        Database = database,
        Username = username,
        Password = password,
    }.ConnectionString;

    return PostgresConnectionString.WithoutGssNegotiation(connectionString);
}
