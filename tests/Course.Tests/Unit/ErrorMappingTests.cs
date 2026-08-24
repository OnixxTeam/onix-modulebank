using Course.Core.Errors;
using Course.Infrastructure.Postgres;
using Npgsql;

namespace Course.Tests.Unit;

public sealed class ErrorMappingTests
{
    // A target's own status=error business code, outside the fixed transport matrix,
    // still gets a controlled 404 when its last segment is not_found (issue 03:
    // operation.get's operation.not_found must not collapse into a 500).
    [Fact]
    public void BusinessNotFoundCode_MapsToNotFoundKeepingCode()
    {
        var error = ActionError.ForCode("operation.not_found", "operation not found", retryable: false);

        Assert.Equal(404, error.HttpStatus);
        Assert.Equal("operation.not_found", error.Code);
        Assert.Equal("operation not found", error.Message);
    }

    // A business code outside the matrix that does not end in .not_found still collapses
    // into internal.error -- a target cannot pick its own status by inventing a code.
    [Fact]
    public void UnknownBusinessCode_StillMapsToInternalError()
    {
        var error = ActionError.ForCode("demo.whatever", "forced failure", retryable: false);

        Assert.Equal(500, error.HttpStatus);
        Assert.Equal("internal.error", error.Code);
    }

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
