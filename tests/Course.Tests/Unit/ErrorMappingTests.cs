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

    // contract-reference's matrix: "Target вернул status=error -> Контролируемый
    // error envelope". A deliberate domain rejection is the caller's problem, not
    // a runtime failure, so it keeps the target's own code at 400 rather than
    // becoming the 500 "unexpected runtime failure" that means the opposite.
    [Fact]
    public void BusinessCode_MapsToBadRequestKeepingCodeAndMessage()
    {
        var error = ActionError.ForCode("demo.rejected", "forced failure", retryable: false);

        Assert.Equal(400, error.HttpStatus);
        Assert.Equal("demo.rejected", error.Code);
        Assert.Equal("forced failure", error.Message);
    }

    [Fact]
    public void BusinessCode_KeepsRetryable()
    {
        var error = ActionError.ForCode("demo.busy", "try later", retryable: true);

        Assert.Equal(400, error.HttpStatus);
        Assert.True(error.Retryable);
    }

    // The door opens for dotted business codes only. Anything that is not shaped
    // like one cannot have come from a target that knows the contract, so it is a
    // runtime failure and says so.
    [Theory]
    [InlineData("whatever")]
    [InlineData("Demo.Rejected")]
    [InlineData("demo rejected")]
    [InlineData("demo.")]
    [InlineData("")]
    public void MalformedCode_StillMapsToInternalError(string code)
    {
        var error = ActionError.ForCode(code, "forced failure", retryable: false);

        Assert.Equal(500, error.HttpStatus);
        Assert.Equal("internal.error", error.Code);
        Assert.DoesNotContain("forced failure", error.Message);
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
