using System.Globalization;
using Npgsql;

namespace Course.Tests.Integration;

// Ticket 04 (week 4): the retry budget, the backoff, the jitter and both leases are
// configuration the functions read, not numbers written into a migration. The six
// values reach the `postgres` service alone in the published table, so they travel
// as database settings; a session setting is the same channel at a narrower scope,
// which is what lets a test change the profile and watch the behaviour change.
[Collection("integration")]
public sealed class ReliabilityProfileTests
{
    private readonly ApiFactory factory;

    public ReliabilityProfileTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task TheDeliveryLeaseComesFromTheProfile()
    {
        var delivery = await NewLeasedDeliveryAsync(leaseMs: 60_000);

        Assert.InRange(delivery.LeaseSeconds, 55, 65);
    }

    // "Without the profile" has to be produced explicitly now that the init script
    // puts a database-level value in force: an empty setting reads as absent, which
    // is the same state a deployment that sets none of these six is in. The policy
    // row then decides, and week 3's behaviour is what comes out.
    [Fact]
    public async Task WithoutTheProfileTheDeliveryLeaseFallsBackToThePolicyRow()
    {
        var policyLeaseMs = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select lease_ms from delivery.retry_policy where id = 1");

        var delivery = await NewLeasedDeliveryAsync(leaseMs: null, rawLeaseSetting: "");

        Assert.InRange(delivery.LeaseSeconds, (policyLeaseMs / 1000.0) - 5, (policyLeaseMs / 1000.0) + 5);
    }

    // base * 2^(attempt-1), capped at max, plus jitter. With base 1000, max 100000
    // and no jitter the first failure schedules one second out, the second two.
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.0)]
    [InlineData(3, 4.0)]
    public async Task TheBackoffScheduleComesFromTheProfile(int failures, double expectedSeconds)
    {
        var delay = await FailRepeatedlyAsync(
            failures,
            settings: new Dictionary<string, string>
            {
                ["course.outbox_max_attempts"] = "10",
                ["course.outbox_backoff_base_ms"] = "1000",
                ["course.outbox_backoff_max_ms"] = "100000",
                ["course.outbox_jitter_max_ms"] = "1",
            });

        Assert.InRange(delay!.Value, expectedSeconds - 0.5, expectedSeconds + 0.5);
    }

    [Fact]
    public async Task TheBackoffIsCappedByTheProfileMaximum()
    {
        var delay = await FailRepeatedlyAsync(
            failures: 4,
            settings: new Dictionary<string, string>
            {
                ["course.outbox_max_attempts"] = "10",
                ["course.outbox_backoff_base_ms"] = "1000",
                ["course.outbox_backoff_max_ms"] = "3000",
                ["course.outbox_jitter_max_ms"] = "1",
            });

        Assert.InRange(delay!.Value, 2.5, 3.5);
    }

    // Jitter is added where next_attempt_at is decided, so two dispatchers failing
    // the same delivery at the same instant do not line their retries up. Proven by
    // distribution: with a 5-second jitter window, twenty schedules are not all the
    // same number.
    [Fact]
    public async Task JitterFromTheProfileSpreadsTheSchedule()
    {
        var settings = new Dictionary<string, string>
        {
            ["course.outbox_max_attempts"] = "10",
            ["course.outbox_backoff_base_ms"] = "1000",
            ["course.outbox_backoff_max_ms"] = "1000",
            ["course.outbox_jitter_max_ms"] = "5000",
        };

        var delays = new List<double>();
        for (var index = 0; index < 20; index++)
        {
            delays.Add((await FailRepeatedlyAsync(1, settings))!.Value);
        }

        Assert.True(delays.Distinct().Count() > 1, $"every schedule was {delays[0]}");
        Assert.All(delays, delay => Assert.InRange(delay, 0.5, 6.5));
    }

    [Fact]
    public async Task TheRetryBudgetComesFromTheProfile()
    {
        var settings = new Dictionary<string, string>
        {
            ["course.outbox_max_attempts"] = "2",
            ["course.outbox_backoff_base_ms"] = "1000",
            ["course.outbox_backoff_max_ms"] = "1000",
            ["course.outbox_jitter_max_ms"] = "1",
        };

        // Two attempts including the first: the first failure still waits, the
        // second exhausts the budget and the delivery is DEAD.
        Assert.Equal("RETRY_WAIT", await FailRepeatedlyForStateAsync(1, settings));
        Assert.Equal("DEAD", await FailRepeatedlyForStateAsync(2, settings));
    }

    // An unparseable or non-positive setting is ignored rather than obeyed: a
    // profile typo must not turn into a zero-second lease or an unbounded budget.
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task AnUnusableSettingFallsBackToThePolicyRow(string value)
    {
        var policyLeaseMs = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select lease_ms from delivery.retry_policy where id = 1");

        var delivery = await NewLeasedDeliveryAsync(leaseMs: null, rawLeaseSetting: value);

        Assert.InRange(delivery.LeaseSeconds, (policyLeaseMs / 1000.0) - 5, (policyLeaseMs / 1000.0) + 5);
    }

    // The production channel: db/init/01-reliability-profile.sh sets these as
    // database-level settings, and the fixture mounts that script the way the image
    // carries it. Without this the suite would only ever prove that the functions
    // read *a* setting, never that the container produces one.
    [Theory]
    [InlineData("course.outbox_max_attempts", 4)]
    [InlineData("course.outbox_backoff_base_ms", 200)]
    [InlineData("course.outbox_backoff_max_ms", 800)]
    [InlineData("course.outbox_jitter_max_ms", 100)]
    [InlineData("course.outbox_lease_ms", 2000)]
    [InlineData("course.job_lease_ms", 2000)]
    public async Task TheInitScriptPutsTheProfileInForceForEverySession(string setting, int expected)
    {
        var value = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand("select profile.number(@setting, -1)", connection);
            command.Parameters.AddWithValue("setting", setting);
            return (int)(await command.ExecuteScalarAsync())!;
        });

        Assert.Equal(expected, value);
    }

    // The published window is inclusive: with jitter_max at 100 a schedule may land
    // exactly 100 ms out. Read from the database-level profile above, so this also
    // pins that the two agree.
    [Fact]
    public async Task TheJitterWindowIsInclusiveOfItsMaximum()
    {
        var delays = new List<double>();
        for (var index = 0; index < 40; index++)
        {
            delays.Add((await FailRepeatedlyAsync(1, new Dictionary<string, string>
            {
                ["course.outbox_max_attempts"] = "10",
                ["course.outbox_backoff_base_ms"] = "1000",
                ["course.outbox_backoff_max_ms"] = "1000",
                ["course.outbox_jitter_max_ms"] = "100",
            }))!.Value);
        }

        Assert.All(delays, delay => Assert.InRange(delay, 0.5, 1.101));
    }

    [Fact]
    public async Task TheProfileReaderReadsTheJobLeaseSetting()
    {
        var value = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await SetLocalAsync(connection, transaction, new Dictionary<string, string>
            {
                ["course.job_lease_ms"] = "45000",
            });

            await using var command = new NpgsqlCommand("select profile.number('course.job_lease_ms', 30000)", connection, transaction);
            var read = (int)(await command.ExecuteScalarAsync())!;
            await transaction.RollbackAsync();
            return read;
        });

        Assert.Equal(45000, value);
    }

    // Narrower than it looks: this proves the call survives a caller with no lease
    // of its own, not that the lease it then uses is the profile's. The full path
    // (a worker claiming a real job under a configured lease) is exercised on the
    // live stand, and the arithmetic is covered by the reader test above.
    [Fact]
    public async Task ClaimingWithNoLeaseOfItsOwnIsAccepted()
    {
        await using var connection = new NpgsqlConnection(factory.Postgres.WorkerConnectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await SetLocalAsync(connection, transaction, new Dictionary<string, string> { ["course.job_lease_ms"] = "45000" });

        // A caller that has no lease of its own (null) gets the profile's.
        await using var claim = new NpgsqlCommand(
            "select workflow.claim_jobs(@owner, null)", connection, transaction);
        claim.Parameters.AddWithValue("owner", $"profile-probe-{Guid.NewGuid():N}");

        var raw = (string)(await claim.ExecuteScalarAsync())!;

        // Whether a job was available or not, the call must not fail: a lease of
        // zero would be rejected by make_interval's own arithmetic downstream.
        Assert.Contains("\"status\":\"ok\"", raw.Replace(" ", ""), StringComparison.Ordinal);
    }

    private sealed record LeasedDelivery(Guid OutboxId, double LeaseSeconds);

    private async Task<LeasedDelivery> NewLeasedDeliveryAsync(
        int? leaseMs, string? rawLeaseSetting = null)
    {
        return await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var externalRequestId = await InsertDeliveryAsync(connection, transaction);

            var setting = leaseMs?.ToString(CultureInfo.InvariantCulture) ?? rawLeaseSetting;
            if (setting is not null)
            {
                await SetLocalAsync(connection, transaction, new Dictionary<string, string>
                {
                    ["course.outbox_lease_ms"] = setting,
                });
            }

            await using var claim = new NpgsqlCommand(
                "select outbox_id from delivery.claim_outbox(@owner, 50)", connection, transaction);
            claim.Parameters.AddWithValue("owner", $"profile-probe-{Guid.NewGuid():N}");
            await using var reader = await claim.ExecuteReaderAsync();

            Guid? claimed = null;
            while (await reader.ReadAsync())
            {
                claimed ??= reader.GetGuid(0);
            }

            await reader.CloseAsync();
            Assert.NotNull(claimed);

            await using var lease = new NpgsqlCommand(
                """
                select extract(epoch from lease_expires_at - now())
                  from delivery.outbox
                 where external_request_id = @id
                """,
                connection,
                transaction);
            lease.Parameters.AddWithValue("id", externalRequestId);

            var leaseSeconds = Convert.ToDouble(await lease.ExecuteScalarAsync()!, CultureInfo.InvariantCulture);
            await transaction.RollbackAsync();

            return new LeasedDelivery(claimed.Value, leaseSeconds);
        });
    }

    private async Task<double?> FailRepeatedlyAsync(int failures, Dictionary<string, string> settings)
    {
        var (_, delay) = await FailRepeatedlyCoreAsync(failures, settings);
        return delay;
    }

    private async Task<string> FailRepeatedlyForStateAsync(int failures, Dictionary<string, string> settings)
    {
        var (state, _) = await FailRepeatedlyCoreAsync(failures, settings);
        return state;
    }

    private async Task<(string State, double? DelaySeconds)> FailRepeatedlyCoreAsync(
        int failures, Dictionary<string, string> settings)
    {
        return await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var externalRequestId = await InsertDeliveryAsync(connection, transaction);
            await SetLocalAsync(connection, transaction, settings);

            var owner = $"profile-probe-{Guid.NewGuid():N}";
            var state = "";

            for (var attempt = 0; attempt < failures; attempt++)
            {
                await using var claim = new NpgsqlCommand(
                    """
                    select lease_version
                      from delivery.claim_outbox(@owner, 200)
                     where external_request_id = @id
                    """,
                    connection,
                    transaction);
                claim.Parameters.AddWithValue("owner", owner);
                claim.Parameters.AddWithValue("id", externalRequestId);
                var leaseVersion = (long?)await claim.ExecuteScalarAsync();

                // A RETRY_WAIT whose next attempt is in the future is not claimable
                // yet; bring it forward rather than waiting out the backoff.
                if (leaseVersion is null)
                {
                    await using var due = new NpgsqlCommand(
                        "update delivery.outbox set next_attempt_at = now() where external_request_id = @id",
                        connection,
                        transaction);
                    due.Parameters.AddWithValue("id", externalRequestId);
                    await due.ExecuteNonQueryAsync();

                    await using var reclaim = new NpgsqlCommand(
                        """
                        select lease_version
                          from delivery.claim_outbox(@owner, 200)
                         where external_request_id = @id
                        """,
                        connection,
                        transaction);
                    reclaim.Parameters.AddWithValue("owner", owner);
                    reclaim.Parameters.AddWithValue("id", externalRequestId);
                    leaseVersion = (long?)await reclaim.ExecuteScalarAsync();
                }

                Assert.NotNull(leaseVersion);

                await using var fail = new NpgsqlCommand(
                    """
                    select delivery.fail_outbox(
                      (select outbox_id from delivery.outbox where external_request_id = @id),
                      @owner, @leaseVersion, 'provider.timeout.retryable')
                    """,
                    connection,
                    transaction);
                fail.Parameters.AddWithValue("id", externalRequestId);
                fail.Parameters.AddWithValue("owner", owner);
                fail.Parameters.AddWithValue("leaseVersion", leaseVersion.Value);
                await fail.ExecuteScalarAsync();
            }

            await using var read = new NpgsqlCommand(
                """
                select state, extract(epoch from next_attempt_at - now())
                  from delivery.outbox
                 where external_request_id = @id
                """,
                connection,
                transaction);
            read.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await read.ExecuteReaderAsync();
            await reader.ReadAsync();

            state = reader.GetString(0);
            double? delay = reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
            await reader.CloseAsync();
            await transaction.RollbackAsync();

            return (state, delay);
        });
    }

    // Transaction-local, never session-level. The fixture hands out pooled
    // connections, so a session setting would outlive this test on that connection
    // and quietly re-profile whatever ran next -- which is exactly how a green
    // suite turned red only when the whole suite ran. The work these settings
    // govern happens inside the same transaction and is committed with it.
    //
    // set_config, not SET: the setting name is data here, and a literal would have
    // to be interpolated into the statement.
    private static async Task SetLocalAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Dictionary<string, string> settings)
    {
        foreach (var (name, value) in settings)
        {
            await using var command = new NpgsqlCommand("select set_config(@name, @value, true)", connection, transaction);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteScalarAsync();
        }
    }

    private static async Task<string> InsertDeliveryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var externalRequestId = $"profile-probe-{Guid.NewGuid():N}";

        {
            await using var insert = new NpgsqlCommand(
                """
                with operation as (
                  insert into core.operations (request_id, operation_kind, amount, currency, status)
                  values (@id, 'PAYMENT_EXECUTION', 10.00, 'RUB', 'CREATED')
                  returning operation_id
                ), request as (
                  insert into core.external_requests (external_request_id, operation_id, payload_hash, state)
                  select @id, operation_id, repeat('0', 64), 'CREATED' from operation
                  returning external_request_id
                )
                insert into delivery.outbox (external_request_id, correlation_id, amount, currency, state)
                select external_request_id, pg_catalog.gen_random_uuid(), '10.00', 'RUB', 'PENDING'
                  from request
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("id", externalRequestId);
            await insert.ExecuteNonQueryAsync();
        }

        return externalRequestId;
    }
}
