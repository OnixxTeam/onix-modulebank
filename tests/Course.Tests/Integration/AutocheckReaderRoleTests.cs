using Npgsql;

namespace Course.Tests.Integration;

// Ticket 01 (week 4): the week-4 public checker runs *every* SQL statement of
// its run as `psql -U autocheck_reader` and asserts, before anything else, that
// current_user is that role and transaction_read_only is on. Week 3's checker
// opened its channel differently, so 58/58 there says nothing about this role
// existing -- without it the week-4 run dies on the database contract before a
// single subject scenario is measured.
//
// Everything below is the checker's own catalogue introspection, phrased as
// tests: the role reads every autocheck view and nothing else, cannot write,
// has no membership, no CREATE/TEMP, no privilege on a physical relation, no
// sequence privilege and no EXECUTE on an application function.
[Collection("integration")]
public sealed class AutocheckReaderRoleTests
{
    private readonly ApiFactory factory;

    public AutocheckReaderRoleTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task TheReaderSessionIsTheReaderRoleAndIsReadOnly()
    {
        await using var connection = await OpenAsReaderAsync();

        Assert.Equal("autocheck_reader", await ScalarAsync<string>(connection, "select current_user"));
        Assert.Equal("on", await ScalarAsync<string>(connection, "select current_setting('transaction_read_only')"));
    }

    // The read-only property has to come from the role itself: the checker sets
    // no session parameter of its own, it just connects and reads the setting
    // back. A `set transaction read only` in some wrapper would satisfy the
    // assertion above and still leave every other connection writable.
    [Fact]
    public async Task EveryAutocheckViewIsReadable()
    {
        await using var connection = await OpenAsReaderAsync();

        var views = await QueryAsync(
            connection,
            "select table_name from information_schema.views where table_schema = 'autocheck' order by table_name");

        Assert.NotEmpty(views);

        foreach (var view in views)
        {
            await using var command = new NpgsqlCommand($"select count(*) from autocheck.{view}", connection);
            await command.ExecuteScalarAsync();
        }
    }

    [Fact]
    public async Task WritingThroughAnAutocheckViewIsRejected()
    {
        await using var connection = await OpenAsReaderAsync();

        await using var command = new NpgsqlCommand("delete from autocheck.operations", connection);

        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ReadingAPhysicalTableIsRejected()
    {
        await using var connection = await OpenAsReaderAsync();

        await using var command = new NpgsqlCommand("select count(*) from core.operations", connection);

        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TheReaderHasNoMemberships()
    {
        await using var connection = await OpenAsReaderAsync();

        var memberships = await QueryAsync(
            connection,
            """
            select granted.rolname
              from pg_catalog.pg_auth_members membership
              join pg_catalog.pg_roles member on member.oid = membership.member
              join pg_catalog.pg_roles granted on granted.oid = membership.roleid
             where member.rolname = current_user
            """);

        Assert.Empty(memberships);
    }

    [Fact]
    public async Task TheReaderIsNotElevated()
    {
        await using var connection = await OpenAsReaderAsync();

        var attributes = await QueryAsync(
            connection,
            """
            select attribute
              from pg_catalog.pg_roles,
                   lateral (values
                     ('rolsuper', rolsuper),
                     ('rolcreaterole', rolcreaterole),
                     ('rolcreatedb', rolcreatedb),
                     ('rolreplication', rolreplication),
                     ('rolbypassrls', rolbypassrls)
                   ) as granted(attribute, value)
             where rolname = current_user and granted.value
            """);

        Assert.Empty(attributes);
    }

    [Fact]
    public async Task TheReaderHasNoSchemaCreateAnywhere()
    {
        await using var connection = await OpenAsReaderAsync();

        var schemas = await QueryAsync(
            connection,
            """
            select nspname
              from pg_catalog.pg_namespace
             where nspname not like 'pg_%'
               and nspname <> 'information_schema'
               and has_schema_privilege(current_user, oid, 'CREATE')
            """);

        Assert.Empty(schemas);
    }

    [Fact]
    public async Task TheReaderHasNoDatabaseCreateOrTemp()
    {
        await using var connection = await OpenAsReaderAsync();

        var privileges = await QueryAsync(
            connection,
            """
            select privilege
              from (values ('CREATE'), ('TEMP')) as p(privilege)
             where has_database_privilege(current_user, current_database(), p.privilege)
            """);

        Assert.Empty(privileges);
    }

    // The checker tolerates USAGE on `public` (every role has it by default) and
    // requires it on `autocheck`; any other application schema must stay closed.
    [Fact]
    public async Task TheReaderHasNoUsageOnAnUnrelatedSchema()
    {
        await using var connection = await OpenAsReaderAsync();

        var schemas = await QueryAsync(
            connection,
            """
            select nspname
              from pg_catalog.pg_namespace
             where nspname not like 'pg_%'
               and nspname not in ('information_schema', 'public', 'autocheck')
               and has_schema_privilege(current_user, oid, 'USAGE')
            """);

        Assert.Empty(schemas);
    }

    [Fact]
    public async Task TheReaderHasNoExcessRelationPrivilege()
    {
        await using var connection = await OpenAsReaderAsync();

        var relations = await QueryAsync(
            connection,
            """
            select n.nspname || '.' || c.relname
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p', 'v', 'm', 'f')
               and n.nspname not in ('pg_catalog', 'information_schema')
               and n.nspname not like 'pg_toast%'
               and ((n.nspname <> 'autocheck' and has_table_privilege(current_user, c.oid, 'SELECT'))
                 or has_table_privilege(current_user, c.oid, 'INSERT')
                 or has_table_privilege(current_user, c.oid, 'UPDATE')
                 or has_table_privilege(current_user, c.oid, 'DELETE')
                 or has_table_privilege(current_user, c.oid, 'TRUNCATE')
                 or has_table_privilege(current_user, c.oid, 'REFERENCES')
                 or has_table_privilege(current_user, c.oid, 'TRIGGER'))
            """);

        Assert.Empty(relations);
    }

    [Fact]
    public async Task TheReaderHasNoSequencePrivilege()
    {
        await using var connection = await OpenAsReaderAsync();

        var sequences = await QueryAsync(
            connection,
            """
            select n.nspname || '.' || c.relname
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind = 'S'
               and n.nspname not like 'pg_%'
               and (has_sequence_privilege(current_user, c.oid, 'USAGE')
                 or has_sequence_privilege(current_user, c.oid, 'SELECT')
                 or has_sequence_privilege(current_user, c.oid, 'UPDATE'))
            """);

        Assert.Empty(sequences);
    }

    // Functions are the one privilege PostgreSQL grants to PUBLIC by default, so
    // this is the assertion most likely to catch a *future* migration: any new
    // application function that forgets `revoke all ... from public` lands here
    // as a failure rather than as a hidden-check surprise.
    [Fact]
    public async Task TheReaderCannotExecuteAnApplicationFunction()
    {
        await using var connection = await OpenAsReaderAsync();

        var functions = await QueryAsync(
            connection,
            """
            select n.nspname || '.' || p.proname
                   || '(' || pg_catalog.oidvectortypes(p.proargtypes) || ')'
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname not like 'pg_%'
               and n.nspname not in ('information_schema', 'autocheck')
               and has_function_privilege(current_user, p.oid, 'EXECUTE')
             order by 1
            """);

        Assert.Empty(functions);
    }

    private async Task<NpgsqlConnection> OpenAsReaderAsync()
    {
        var connection = new NpgsqlConnection(factory.Postgres.AutocheckReaderConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> QueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
