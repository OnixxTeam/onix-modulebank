using Npgsql;

namespace Course.Infrastructure.Postgres;

// Every process here reaches PostgreSQL over the private compose network with a
// password (scram-sha-256), and no Kerberos realm exists anywhere in this
// deployment -- so the one thing worth saying about a connection string is that
// GSS is not part of the picture.
public static class PostgresConnectionString
{
    // Npgsql tries to negotiate GSS encryption before authenticating, which makes
    // the .NET native security shim load libgssapi_krb5.so.2. The runtime images
    // (mcr.microsoft.com/dotnet/runtime:10.0, Ubuntu 24.04) do not ship Kerberos,
    // so the probe failed on every connection and wrote
    //
    //   Cannot load library libgssapi_krb5.so.2
    //   Error: libgssapi_krb5.so.2: cannot open shared object file: ...
    //
    // straight to stderr, from underneath the logging pipeline -- past
    // AddJsonConsole in the API, and into the operator's terminal on every CLI
    // command. The connection then continued over the ordinary path, so the noise
    // reported a failure that changed nothing.
    //
    // Turning the negotiation off is the honest description of this deployment; the
    // alternative, installing krb5 into both images, would carry a dependency for a
    // mechanism that can never be used here. Applied centrally rather than at each
    // call site so a new entry point cannot quietly reintroduce the probe, and last
    // so a supplied DSN cannot ask for GSS back.
    public static string WithoutGssNegotiation(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
}
