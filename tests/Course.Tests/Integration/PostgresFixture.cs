using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Infrastructure.Postgres;
using Course.Tests.Support;
using Npgsql;
using Testcontainers.PostgreSql;
using Workflow.Core.Publishing;
using Workflow.Core.Runtime;

namespace Course.Tests.Integration;

// Starts one isolated postgres:17-alpine container for the whole integration
// test run (see AssemblyInfo.cs: parallelization is disabled so nothing else
// races this), runs the same role-creation script and checksummed migrations
// bootstrap uses, publishes the real domain manifests plus the rollback-test
// fixture actions, and exposes one connection string per application role --
// mirroring compose.yaml's postgres/bootstrap sequence without docker compose.
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator-password";
    private const string PublisherPassword = "test-publisher-password";
    private const string RuntimePassword = "test-runtime-password";
    private const string WorkerPassword = "test-worker-password";
    private const string SuperuserPassword = "test-superuser-password";
    private const string DatabaseName = "course";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase(DatabaseName)
        .WithUsername("postgres")
        .WithPassword(SuperuserPassword)
        .WithEnvironment("COURSE_MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("COURSE_PUBLISHER_PASSWORD", PublisherPassword)
        .WithEnvironment("COURSE_RUNTIME_PASSWORD", RuntimePassword)
        .WithEnvironment("WORKFLOW_WORKER_PASSWORD", WorkerPassword)
        .WithResourceMapping(
            new FileInfo(RepoPaths.Combine("db", "init", "00-roles.sh")),
            new FileInfo("/docker-entrypoint-initdb.d/00-roles.sh"))
        .Build();

    public string RuntimeConnectionString => BuildConnectionString("course_runtime", RuntimePassword);

    public string PublisherConnectionString => BuildConnectionString("course_publisher", PublisherPassword);

    public string WorkerConnectionString => BuildConnectionString("workflow_worker", WorkerPassword);

    public IActionPublisher Publisher { get; private set; } = null!;

    public IWorkflowFlowPublisher FlowPublisher { get; private set; } = null!;

    public IWorkflowProcessRuntime ProcessRuntime { get; private set; } = null!;

    public IWorkflowJobRuntime JobRuntime { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        var migrator = new PostgresMigrationRunner(BuildConnectionString("course_migrator", MigratorPassword));
        await migrator.ApplyAsync(RepoPaths.Combine("db", "migrations"), CancellationToken.None);

        await ApplyFixtureScriptAsync(RepoPaths.Combine("tests", "Course.Tests", "Fixtures", "testfixture.sql"));

        Publisher = new PostgresActionPublisher(PublisherConnectionString);
        FlowPublisher = new PostgresWorkflowFlowPublisher(PublisherConnectionString);
        ProcessRuntime = new PostgresWorkflowProcessRuntime(PublisherConnectionString);
        JobRuntime = new PostgresWorkflowJobRuntime(WorkerConnectionString);

        await PublishAndActivateAsync(RepoPaths.Combine("db", "manifests", "payment.request.v1.action.json"));
        await PublishAndActivateAsync(RepoPaths.Combine("db", "manifests", "operation.get.v1.action.json"));
        await PublishAndActivateAsync(RepoPaths.Combine("db", "manifests", "workflow.get.v1.action.json"));
        await PublishAndActivateAsync(RepoPaths.Combine("db", "manifests", "workflow.probe.v1.action.json"));
        await PublishAndActivateAsync(TestManifests.ErrorAfterInsert());
        await PublishAndActivateAsync(TestManifests.UnknownOutcome());
        await PublishAndActivateAsync(TestManifests.EchoContext());
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    // Assertion helper for RollbackTests/CatalogTests: course_runtime has no
    // grant on testfixture.* or core.*, so reading those tables back needs the
    // same course_owner elevation the migration runner uses for DDL -- migrator
    // is a login member of course_owner (db/init/00-roles.sh).
    public async Task<T> QueryAsOwnerAsync<T>(Func<NpgsqlConnection, Task<T>> query)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString("course_migrator", MigratorPassword));
        await connection.OpenAsync();

        await using (var setRole = new NpgsqlCommand("set role course_owner", connection))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        return await query(connection);
    }

    // PolicyTests' direct-call boundary: course_runtime is the only role granted
    // execute on api.invoke, matching exactly what Course.Api itself connects as.
    public async Task<T> QueryAsRuntimeAsync<T>(Func<NpgsqlConnection, Task<T>> query)
    {
        await using var connection = new NpgsqlConnection(RuntimeConnectionString);
        await connection.OpenAsync();

        return await query(connection);
    }

    // Collapses the repeated "one parameterised scalar query, elevated to
    // course_owner" shape RollbackTests/IdempotencyTests/PolicyTests all need
    // for their assertions (row counts, exists checks) into one call.
    public Task<T> ScalarAsOwnerAsync<T>(string sql, params (string Name, object Value)[] parameters) =>
        QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (T)(await command.ExecuteScalarAsync())!;
        });

    private async Task PublishAndActivateAsync(string manifestPath)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        await PublishAndActivateAsync(document);
    }

    private async Task PublishAndActivateAsync(JsonNode document)
    {
        var manifest = ActionManifest.FromDocument(document);

        var publishResult = await Publisher.PublishAsync(manifest, CancellationToken.None);
        if (publishResult["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to publish {manifest.Key}: {publishResult.ToJsonString()}");
        }

        var activateResult = await Publisher.ActivateAsync(manifest.Module, manifest.Action, manifest.Version, CancellationToken.None);
        if (activateResult["status"]!.GetValue<string>() != "ok")
        {
            throw new InvalidOperationException($"failed to activate {manifest.Key}: {activateResult.ToJsonString()}");
        }
    }

    private async Task ApplyFixtureScriptAsync(string path)
    {
        var content = await File.ReadAllTextAsync(path);

        await using var connection = new NpgsqlConnection(BuildConnectionString("course_migrator", MigratorPassword));
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"set local role course_owner;\n{content}", connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private string BuildConnectionString(string username, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = DatabaseName,
            Username = username,
            Password = password,
        }.ConnectionString;
}
