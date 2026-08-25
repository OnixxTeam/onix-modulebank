using Course.Infrastructure.Postgres;
using Npgsql;

namespace Course.Tests.Unit;

public sealed class PostgresConnectionStringTests
{
    private const string Dsn = "Host=postgres;Port=5432;Database=course;Username=course_runtime;Password=runtime";

    [Fact]
    public void WithoutGssNegotiation_DisablesGssEncryption()
    {
        var result = new NpgsqlConnectionStringBuilder(PostgresConnectionString.WithoutGssNegotiation(Dsn));

        Assert.Equal(GssEncryptionMode.Disable, result.GssEncryptionMode);
    }

    [Fact]
    public void WithoutGssNegotiation_KeepsEveryOtherSetting()
    {
        var result = new NpgsqlConnectionStringBuilder(PostgresConnectionString.WithoutGssNegotiation(Dsn));

        Assert.Equal("postgres", result.Host);
        Assert.Equal(5432, result.Port);
        Assert.Equal("course", result.Database);
        Assert.Equal("course_runtime", result.Username);
        Assert.Equal("runtime", result.Password);
    }

    // The point is that no deployment can turn the probe back on by accident:
    // a DSN asking for GSS still comes back with it disabled.
    [Fact]
    public void WithoutGssNegotiation_OverridesAnExplicitMode()
    {
        var result = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.WithoutGssNegotiation($"{Dsn};Gss Encryption Mode=prefer"));

        Assert.Equal(GssEncryptionMode.Disable, result.GssEncryptionMode);
    }
}
