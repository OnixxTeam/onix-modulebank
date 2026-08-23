using System.Net.Sockets;
using Course.Core.Errors;
using Npgsql;

namespace Course.Infrastructure.Postgres;

// The exception-driven half of the error matrix (contract-reference, HTTP API table):
// everything reachable via a normal return value (payload.invalid, access.denied, ...)
// is built inline by PostgresActionExecutor; only failures that surface as .NET/Npgsql
// exceptions -- connectivity, statement timeout, anything unexpected -- come through
// here, checked in this order because PostgresException is itself a NpgsqlException and
// the 57014 (query_canceled) case must be caught before the generic IsTransient check.
public static class NpgsqlErrorMapper
{
    public static ActionError Map(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } => ActionError.Timeout(),
        OperationCanceledException => ActionError.Timeout(),
        NpgsqlException { IsTransient: true } => ActionError.DependencyUnavailable(),
        SocketException => ActionError.DependencyUnavailable(),
        _ => ActionError.InternalError(),
    };
}
