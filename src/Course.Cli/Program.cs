using Course.Cli;
using Course.Cli.Commands;
using Course.Core.Cli;
using Course.Infrastructure.Postgres;

try
{
    var parsed = ArgumentParser.Parse(args);
    var runner = new PostgresMigrationRunner(BuildConnectionString());

    CliEnvelope envelope;
    switch (parsed.Name)
    {
        case "migration apply":
            envelope = await MigrationApplyCommand.ExecuteAsync(parsed, runner, CancellationToken.None);
            break;
        case "bootstrap":
            envelope = await BootstrapCommand.ExecuteAsync(runner, CancellationToken.None);
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

static string BuildConnectionString()
{
    var host = Environment.GetEnvironmentVariable("COURSE_DB_HOST") ?? "postgres";
    var port = Environment.GetEnvironmentVariable("COURSE_DB_PORT") ?? "5432";
    var database = Environment.GetEnvironmentVariable("COURSE_DB_NAME") ?? "course";
    var password = Environment.GetEnvironmentVariable("COURSE_MIGRATOR_PASSWORD")
        ?? throw new InvalidOperationException("COURSE_MIGRATOR_PASSWORD is not set");

    return new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = host,
        Port = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture),
        Database = database,
        Username = "course_migrator",
        Password = password,
    }.ConnectionString;
}
