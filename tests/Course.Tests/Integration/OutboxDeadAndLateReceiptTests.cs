using Npgsql;

namespace Course.Tests.Integration;

// Ticket 05 (week 4): DEAD is the technical outcome of a delivery, not a domain
// verdict, and a late receipt outranks it.
//
// The four properties the hidden run cares about, in one place: exhaustion leaves
// evidence (state, dead_at, last_error_code, attempt_count); exhaustion does not
// touch the operation or the process; a receipt arriving afterwards still confirms
// the delivery and lets the process continue; and confirming does not erase the
// evidence of what went wrong first.
[Collection("integration")]
public sealed class OutboxDeadAndLateReceiptTests
{
    private readonly ApiFactory factory;

    public OutboxDeadAndLateReceiptTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ExhaustionRecordsWhenItHappenedAlongsideTheErrorAndTheAttemptCount()
    {
        var delivery = await NewDeliveryAsync();

        var row = await ExhaustAsync(delivery);

        Assert.Equal("DEAD", row.State);
        Assert.NotNull(row.DeadAt);
        Assert.Equal("transport.error.retryable", row.LastErrorCode);
        Assert.Equal(4, row.AttemptCount);
    }

    // A delivery can only die once, and two things keep it that way: a dead row is
    // never handed out again, and a failure reported by an owner that no longer holds
    // the lease is a no-op. So the moment of death cannot move even when a dispatcher
    // still in flight reports afterwards.
    [Fact]
    public async Task ADeadDeliveryIsNeitherReclaimedNorReDated()
    {
        var delivery = await NewDeliveryAsync();
        var first = await ExhaustAsync(delivery);

        await MakeClaimableAsync(delivery);
        Assert.Null(await ClaimAsync(delivery, "dispatcher-again"));

        var late = await FailForResultAsync(delivery, "dispatcher-3", first.AttemptCount, "transport.error.retryable");
        Assert.Contains("\"skipped\":true", late.Replace(" ", ""), StringComparison.Ordinal);

        var second = await ReadAsync(delivery);
        Assert.Equal(first.DeadAt, second.DeadAt);
        Assert.Equal(first.AttemptCount, second.AttemptCount);
    }

    // The whole point of calling DEAD "technical": the operation is still being
    // processed and the process is still waiting for its receipt. Nothing in
    // fail_outbox may reach either.
    [Fact]
    public async Task ExhaustionLeavesTheOperationAndTheProcessAlone()
    {
        var delivery = await NewDeliveryAsync(processState: "WAITING_SIGNAL", operationStatus: "PROCESSING");

        await ExhaustAsync(delivery);

        var (operationStatus, processState) = await ReadDomainStateAsync(delivery);

        Assert.Equal("PROCESSING", operationStatus);
        Assert.Equal("WAITING_SIGNAL", processState);
    }

    [Fact]
    public async Task ALateReceiptConfirmsADeadDeliveryAndKeepsItsDiagnostics()
    {
        var delivery = await NewDeliveryAsync();
        var dead = await ExhaustAsync(delivery);

        var accepted = await AcceptReceiptAsync(delivery, $"provider-payment-{Guid.NewGuid():N}");
        Assert.Contains("\"status\":\"ok\"", accepted.Replace(" ", ""), StringComparison.Ordinal);

        var confirmed = await ReadAsync(delivery);

        Assert.Equal("CONFIRMED", confirmed.State);
        Assert.NotNull(confirmed.DeliveredAt);

        // Confirming is not forgetting: the exhausted delivery's own evidence stays
        // readable, which is what makes the path reconstructible afterwards.
        Assert.Equal(dead.DeadAt, confirmed.DeadAt);
        Assert.Equal(dead.LastErrorCode, confirmed.LastErrorCode);
        Assert.Equal(dead.AttemptCount, confirmed.AttemptCount);

        // And the receipt is durably in the Inbox, which is what lets the waiting
        // process be continued afterwards: reconcile_inbox turns it into the signal,
        // and that path has its own tests.
        var inboxState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from delivery.inbox where external_request_id = @id", ("id", delivery));
        Assert.Equal("RECEIVED", inboxState);
    }

    [Fact]
    public async Task AConfirmedDeliveryIsNeverDraggedBackToDead()
    {
        var delivery = await NewDeliveryAsync();
        await ExhaustAsync(delivery);
        await AcceptReceiptAsync(delivery, $"provider-payment-{Guid.NewGuid():N}");

        // A dispatcher that was still in flight when the receipt landed reports its
        // own failure afterwards. The fencing guard makes that a no-op.
        await MakeClaimableAsync(delivery);
        var claim = await ClaimAsync(delivery, "dispatcher-latecomer");
        Assert.Null(claim);

        Assert.Equal("CONFIRMED", (await ReadAsync(delivery)).State);
    }

    // The projection, not the physical table: the verifying run reads these four
    // columns by the contract's own names, and `lease_until` is an alias of the
    // column this schema calls lease_expires_at.
    [Fact]
    public async Task TheProjectionExposesTheLeaseColumnsAndTheMomentOfDeath()
    {
        var delivery = await NewDeliveryAsync();
        await ClaimAsync(delivery, "dispatcher-projection");

        var leased = await ReadProjectionAsync(delivery);
        Assert.Equal("dispatcher-projection", leased.LeaseOwner);
        // lease_version is not nullable: it starts at 0 and counts up, so the
        // "empty until the state is reached" rule covers the owner and the deadline.
        Assert.True(leased.LeaseVersion >= 1);
        Assert.NotNull(leased.LeaseUntil);
        Assert.Null(leased.DeadAt);

        await MakeClaimableAsync(delivery);
        await ExhaustAsync(delivery);

        var dead = await ReadProjectionAsync(delivery);
        Assert.NotNull(dead.DeadAt);
        // The lease is released when the delivery dies, so its owner and deadline
        // are empty again while lease_version keeps counting.
        Assert.Null(dead.LeaseOwner);
        Assert.Null(dead.LeaseUntil);
    }

    [Fact]
    public async Task TheReadOnlyReaderCanSeeTheWidenedProjection()
    {
        var delivery = await NewDeliveryAsync();
        await ClaimAsync(delivery, "dispatcher-reader-probe");

        await using var connection = new NpgsqlConnection(factory.Postgres.AutocheckReaderConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select lease_owner, lease_version, lease_until
              from autocheck.outbox
             where external_request_id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", delivery);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "the reader saw no row for the claimed delivery");
        Assert.Equal("dispatcher-reader-probe", reader.GetString(0));
        Assert.True(reader.GetInt64(1) >= 1);
        Assert.False(reader.IsDBNull(2));
    }

    private sealed record OutboxRow(
        string State, DateTime? DeadAt, string? LastErrorCode, int AttemptCount, DateTime? DeliveredAt);

    private sealed record ProjectionRow(
        string? LeaseOwner, long LeaseVersion, DateTime? LeaseUntil, DateTime? DeadAt);

    // The domain path that produces a delivery has its own tests; what is under test
    // here is what happens to it afterwards, so the row is written directly -- with
    // a process and an operation attached, because two of these tests are about
    // exactly those staying untouched.
    private async Task<string> NewDeliveryAsync(
        string processState = "WAITING_SIGNAL", string operationStatus = "PROCESSING")
    {
        var externalRequestId = $"dead-probe-{Guid.NewGuid():N}";

        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var insert = new NpgsqlCommand(
                """
                with process as (
                  insert into workflow.process_instance
                         (flow_name, flow_version, business_key, state,
                          current_step_key, start_data, start_data_hash)
                  select flow_name, version, @id, @processState,
                         'dead-probe-step', '{}'::jsonb, repeat('0', 64)
                    from workflow.flow_version
                   order by flow_name, version
                   limit 1
                  returning process_id
                ), operation as (
                  insert into core.operations
                         (request_id, operation_kind, amount, currency, status, process_id)
                  select @id, 'PAYMENT_EXECUTION', 10.00, 'RUB', @operationStatus, process_id
                    from process
                  returning operation_id
                ), request as (
                  insert into core.external_requests (external_request_id, operation_id, payload_hash, state)
                  select @id, operation_id, repeat('0', 64), 'SENT' from operation
                  returning external_request_id
                )
                insert into delivery.outbox (external_request_id, correlation_id, amount, currency, state)
                select external_request_id, pg_catalog.gen_random_uuid(), '10.00', 'RUB', 'PENDING'
                  from request
                """,
                connection);
            insert.Parameters.AddWithValue("id", externalRequestId);
            insert.Parameters.AddWithValue("processState", processState);
            insert.Parameters.AddWithValue("operationStatus", operationStatus);
            return await insert.ExecuteNonQueryAsync();
        });

        return externalRequestId;
    }

    // Four attempts including the first, which is the published test profile's
    // budget: the fourth failure is the one that exhausts it.
    private async Task<OutboxRow> ExhaustAsync(string externalRequestId)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await MakeClaimableAsync(externalRequestId);
            var leaseVersion = await ClaimAsync(externalRequestId, $"dispatcher-{attempt}");
            Assert.NotNull(leaseVersion);
            await FailAsync(externalRequestId, $"dispatcher-{attempt}", leaseVersion.Value, "transport.error.retryable");
        }

        return await ReadAsync(externalRequestId);
    }

    // Both halves of claim_outbox's own predicate: a due next attempt, and -- for a
    // row still held by a previous owner -- an expired lease. Bringing only the
    // attempt forward leaves a LEASED row unclaimable, which is how the lease is
    // supposed to work.
    private Task<int> MakeClaimableAsync(string externalRequestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var due = new NpgsqlCommand(
                """
                update delivery.outbox
                   set next_attempt_at  = now(),
                       lease_expires_at = case when state = 'LEASED' then now() - interval '1 second'
                                               else lease_expires_at end
                 where external_request_id = @id
                """,
                connection);
            due.Parameters.AddWithValue("id", externalRequestId);
            return await due.ExecuteNonQueryAsync();
        });

    private Task<long?> ClaimAsync(string externalRequestId, string owner) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var claim = new NpgsqlCommand(
                """
                select lease_version
                  from delivery.claim_outbox(@owner, 500)
                 where external_request_id = @id
                """,
                connection);
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("id", externalRequestId);
            return (long?)await claim.ExecuteScalarAsync();
        });

    private Task<int> FailAsync(string externalRequestId, string owner, long leaseVersion, string errorCode) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var fail = new NpgsqlCommand(
                """
                select delivery.fail_outbox(
                  (select outbox_id from delivery.outbox where external_request_id = @id),
                  @owner, @leaseVersion, @errorCode)
                """,
                connection);
            fail.Parameters.AddWithValue("id", externalRequestId);
            fail.Parameters.AddWithValue("owner", owner);
            fail.Parameters.AddWithValue("leaseVersion", leaseVersion);
            fail.Parameters.AddWithValue("errorCode", errorCode);
            await fail.ExecuteScalarAsync();
            return 0;
        });

    // The real registered action, not a hand-written copy of its UPDATE: the property
    // under test is that receipt.accept_v1 confirms a delivery which is already DEAD,
    // and a test re-implementing that statement could not notice the function
    // regressing. Called with the context its signature boundary would have stamped --
    // the boundary itself has its own tests (ReceiptAcceptTests).
    private Task<string> AcceptReceiptAsync(string externalRequestId, string messageId)
    {
        var body =
            $"{{\"messageId\":\"{messageId}\",\"externalRequestId\":\"{externalRequestId}\"," +
            "\"version\":1,\"outcome\":\"COMPLETED\",\"providerPaymentId\":\"provider-late-receipt\"," +
            "\"occurredAt\":\"2026-09-27T00:00:00Z\"}";

        return factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var accept = new NpgsqlCommand(
                """
                select receipt.accept_v1(
                  jsonb_build_object(
                    'rawBodyHash', encode(sha256(@body::bytea), 'hex'),
                    'transport', jsonb_build_object('signatureVerified', true)),
                  @body::jsonb)::text
                """,
                connection);
            accept.Parameters.AddWithValue("body", body);
            return (string)(await accept.ExecuteScalarAsync())!;
        });
    }

    private Task<string> FailForResultAsync(
        string externalRequestId, string owner, long leaseVersion, string errorCode) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var fail = new NpgsqlCommand(
                """
                select delivery.fail_outbox(
                  (select outbox_id from delivery.outbox where external_request_id = @id),
                  @owner, @leaseVersion, @errorCode)::text
                """,
                connection);
            fail.Parameters.AddWithValue("id", externalRequestId);
            fail.Parameters.AddWithValue("owner", owner);
            fail.Parameters.AddWithValue("leaseVersion", leaseVersion);
            fail.Parameters.AddWithValue("errorCode", errorCode);
            return (string)(await fail.ExecuteScalarAsync())!;
        });

    private Task<OutboxRow> ReadAsync(string externalRequestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var read = new NpgsqlCommand(
                """
                select state, dead_at, last_error_code, attempt_count, delivered_at
                  from delivery.outbox
                 where external_request_id = @id
                """,
                connection);
            read.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await read.ExecuteReaderAsync();
            await reader.ReadAsync();

            return new OutboxRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4));
        });

    private Task<ProjectionRow> ReadProjectionAsync(string externalRequestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var read = new NpgsqlCommand(
                """
                select lease_owner, lease_version, lease_until, dead_at
                  from autocheck.outbox
                 where external_request_id = @id
                """,
                connection);
            read.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await read.ExecuteReaderAsync();
            await reader.ReadAsync();

            return new ProjectionRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3));
        });

    private Task<(string OperationStatus, string ProcessState)> ReadDomainStateAsync(string externalRequestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var read = new NpgsqlCommand(
                """
                select operations.status, process.state
                  from core.external_requests requests
                  join core.operations operations on operations.operation_id = requests.operation_id
                  join workflow.process_instance process on process.process_id = operations.process_id
                 where requests.external_request_id = @id
                """,
                connection);
            read.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await read.ExecuteReaderAsync();
            await reader.ReadAsync();

            return (reader.GetString(0), reader.GetString(1));
        });
}
