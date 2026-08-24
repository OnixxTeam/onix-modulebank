using Course.Infrastructure.Postgres;
using Npgsql;

namespace Course.Tests.Unit;

public sealed class ErrorMappingTests
{
    // Checked before the generic IsTransient branch: PostgresException is itself
    // an NpgsqlException, so 57014 (query_canceled) must win the switch before
    // falling through to dependency.unavailable.
    [Fact]
    public void QueryCanceled_MapsToTimeout()
    {
        var exception = new PostgresException(
            "canceling statement due to statement timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled);

        var error = NpgsqlErrorMapper.Map(exception);

        Assert.Equal(504, error.HttpStatus);
        Assert.Equal("action.timeout", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void TransientNpgsqlException_MapsToDependencyUnavailable()
    {
        var exception = new TransientNpgsqlException("connection reset by peer");

        var error = NpgsqlErrorMapper.Map(exception);

        Assert.Equal(503, error.HttpStatus);
        Assert.Equal("dependency.unavailable", error.Code);
        Assert.True(error.Retryable);
    }

    // Anything outside the recognised exception shapes collapses to a flat,
    // detail-free internal.error -- the exception's own message (which could
    // name a table, a connection string fragment, or a stack frame) must never
    // reach the client.
    [Fact]
    public void UnrecognizedException_MapsToInternalErrorWithoutLeakingMessage()
    {
        var exception = new InvalidOperationException("connection string contains password=hunter2");

        var error = NpgsqlErrorMapper.Map(exception);

        Assert.Equal(500, error.HttpStatus);
        Assert.Equal("internal.error", error.Code);
        Assert.DoesNotContain("hunter2", error.Message);
    }

    private sealed class TransientNpgsqlException(string message) : NpgsqlException(message)
    {
        public override bool IsTransient => true;
    }
}
