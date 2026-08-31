using Course.Cli;
using Course.Cli.Commands;
using Course.Core.Cli;
using Course.Infrastructure.Postgres;
using Course.Infrastructure.Validation;
using Workflow.Core.Catalog;
using Workflow.Core.Maps;
using Workflow.Core.Security;
using Workflow.Core.Validation;

try
{
    var parsed = ArgumentParser.Parse(args);

    CliEnvelope envelope;
    switch (parsed.Name)
    {
        case "migration apply":
            envelope = await MigrationApplyCommand.ExecuteAsync(
                parsed, new PostgresMigrationRunner(BuildConnectionString("course_migrator", "COURSE_MIGRATOR_PASSWORD")), CancellationToken.None);
            break;
        case "bootstrap":
            envelope = await BootstrapCommand.ExecuteAsync(
                new PostgresMigrationRunner(BuildConnectionString("course_migrator", "COURSE_MIGRATOR_PASSWORD")),
                new PostgresActionPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                new JsonSchemaValidator(),
                ManifestSchema.Load(),
                CancellationToken.None);
            break;
        case "action validate":
            envelope = await ActionValidateCommand.ExecuteAsync(
                parsed, new JsonSchemaValidator(), ManifestSchema.Load(), CancellationToken.None);
            break;
        case "action publish":
            envelope = await ActionPublishCommand.ExecuteAsync(
                parsed,
                new PostgresActionPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                new JsonSchemaValidator(),
                ManifestSchema.Load(),
                CancellationToken.None);
            break;
        case "action list":
            envelope = await ActionListCommand.ExecuteAsync(
                new PostgresActionPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")), CancellationToken.None);
            break;
        case "action activate":
            envelope = await ActionActivateCommand.ExecuteAsync(
                parsed,
                new PostgresActionPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        case "action disable":
            envelope = await ActionDisableCommand.ExecuteAsync(
                parsed,
                new PostgresActionPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        case "flow validate":
            envelope = await FlowValidateCommand.ExecuteAsync(
                parsed,
                BuildWorkflowMapValidator(),
                CancellationToken.None);
            break;
        case "flow publish":
            envelope = await FlowPublishCommand.ExecuteAsync(
                parsed,
                BuildWorkflowMapValidator(),
                new PostgresWorkflowFlowPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        case "flow list":
            envelope = await FlowListCommand.ExecuteAsync(
                new PostgresWorkflowFlowPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        case "flow activate":
            envelope = await FlowActivateCommand.ExecuteAsync(
                parsed,
                new PostgresWorkflowFlowPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        case "flow start":
            envelope = await FlowStartCommand.ExecuteAsync(
                parsed,
                new PostgresWorkflowProcessRuntime(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        case "flow get":
            envelope = await FlowGetCommand.ExecuteAsync(
                parsed,
                new PostgresWorkflowProcessRuntime(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD")),
                CancellationToken.None);
            break;
        default:
            Console.Error.WriteLine($"unknown command: {parsed.Name}");
            Console.Out.Write(CliEnvelope.Fail("command.unknown", $"unknown command: {parsed.Name}").ToJson());
            return 2;
    }

    Console.Out.Write(envelope.ToJson());
    return envelope.Status == "ok" ? 0 : 1;
}
catch (ArgumentParserException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Out.Write(CliEnvelope.Fail("command.invalid", ex.Message).ToJson());
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.Out.Write(CliEnvelope.Fail("internal.error", "unexpected CLI failure").ToJson());
    return 1;
}

static WorkflowMapValidator BuildWorkflowMapValidator() => new(
    new JsonSchemaValidator(),
    MapSchema.Load(),
    new ActionPublisherCatalog(
        new PostgresActionPublisher(BuildConnectionString("course_publisher", "COURSE_PUBLISHER_PASSWORD"))),
    WorkerScopes());

static string[] WorkerScopes()
{
    var raw = Environment.GetEnvironmentVariable("WORKFLOW_WORKER_SCOPES");
    return string.IsNullOrEmpty(raw) ? [.. WorkflowWorkerPrincipal.DefaultScopes] : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

static string BuildConnectionString(string username, string passwordEnvironmentVariable)
{
    var host = Environment.GetEnvironmentVariable("COURSE_DB_HOST") ?? "postgres";
    var port = Environment.GetEnvironmentVariable("COURSE_DB_PORT") ?? "5432";
    var database = Environment.GetEnvironmentVariable("COURSE_DB_NAME") ?? "course";
    var password = Environment.GetEnvironmentVariable(passwordEnvironmentVariable)
        ?? throw new InvalidOperationException($"{passwordEnvironmentVariable} is not set");

    var connectionString = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture),
        Database = database,
        Username = username,
        Password = password,
    }.ConnectionString;

    return PostgresConnectionString.WithoutGssNegotiation(connectionString);
}
