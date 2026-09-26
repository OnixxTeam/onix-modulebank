using System.Text;
using System.Text.Json.Nodes;
using Course.Core.Actions;
using Course.Core.Validation;
using Course.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Workflow.Core.Runtime;
using Workflow.Core.Security;
using Workflow.Worker;

namespace Course.Tests.Integration;

// Ticket 09 acceptance: the whole SQL boundary the Python perimeter will ever
// see, proven here by calling it directly (and, for the privilege checks,
// literally as the outbox_dispatcher/inbox_reconciler roles) -- no Python
// exists yet. claim_outbox/succeed_outbox/fail_outbox never regress a
// CONFIRMED delivery and never move a stale owner/lease_version; fail_outbox
// alone decides retryable-vs-terminal from the error code's own naming
// convention and reads its retry budget from delivery.retry_policy, not a
// literal; reconcile_inbox turns a RECEIVED message into a workflow signal
// whether or not the pinned process exists yet, leaving an unresolvable one
// RECEIVED rather than losing it.
[Collection("integration")]
public sealed class DeliveryFunctionsAndPerimeterRolesTests : IDisposable
{
    private readonly ApiFactory factory;

    // One data source for the whole test rather than one per CreateLoop call:
    // an undisposed data source per call holds its pooled connections open for
    // the rest of the shared-container run, and this class drives the loop many
    // times over.
    private readonly NpgsqlDataSource workerDataSource;

    public DeliveryFunctionsAndPerimeterRolesTests(ApiFactory factory)
    {
        this.factory = factory;
        workerDataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
    }

    public void Dispose() => workerDataSource.Dispose();

    [Fact]
    public async Task ClaimOutbox_ReturnsAPendingRow_LeasedToTheCaller_WithTheContractShape()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("1000.00");

        var claimed = await ClaimAsDispatcherAsync("dispatcher-a", 10);

        var row = Assert.Single(claimed, r => r.ExternalRequestId == externalRequestId);
        Assert.Equal(1, row.LeaseVersion);
        Assert.Equal("1000.00", row.Amount);
        Assert.Equal("RUB", row.Currency);
        Assert.NotEqual(Guid.Empty, row.CorrelationId);

        var state = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from delivery.outbox where external_request_id = @id", ("id", externalRequestId));
        Assert.Equal("LEASED", state);
    }

    [Fact]
    public async Task ClaimOutbox_DoesNotHandTheSameRow_ToASecondOwner()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("1500.00");

        var first = await ClaimAsDispatcherAsync("dispatcher-a", 10);
        Assert.Contains(first, r => r.ExternalRequestId == externalRequestId);

        var second = await ClaimAsDispatcherAsync("dispatcher-b", 10);
        Assert.DoesNotContain(second, r => r.ExternalRequestId == externalRequestId);
    }

    [Fact]
    public async Task ClaimOutbox_ReclaimsAnExpiredLease()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("1600.00");
        await ClaimAsDispatcherAsync("dispatcher-a", 10);

        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "update delivery.outbox set lease_expires_at = now() - interval '1 second' where external_request_id = @id",
                connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            return await command.ExecuteNonQueryAsync();
        });

        var reclaimed = await ClaimAsDispatcherAsync("dispatcher-b", 10);
        var row = Assert.Single(reclaimed, r => r.ExternalRequestId == externalRequestId);
        Assert.Equal(2, row.LeaseVersion);
    }

    [Fact]
    public async Task SucceedOutbox_CorrectOwnerAndLease_MovesToDelivered()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("2000.00");
        var claim = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-a", 10), r => r.ExternalRequestId == externalRequestId);

        var result = await SucceedAsDispatcherAsync(claim.OutboxId, "dispatcher-a", claim.LeaseVersion, "provider-123");

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        var (state, deliveredAt) = await OutboxStateAsync(externalRequestId);
        Assert.Equal("DELIVERED", state);
        Assert.NotNull(deliveredAt);
    }

    [Fact]
    public async Task SucceedOutbox_StaleOwner_IsANoOp()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("2100.00");
        var claim = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-a", 10), r => r.ExternalRequestId == externalRequestId);

        var result = await SucceedAsDispatcherAsync(claim.OutboxId, "someone-else", claim.LeaseVersion, "provider-123");

        Assert.True(result["skipped"]?.GetValue<bool>() ?? false);
        var (state, deliveredAt) = await OutboxStateAsync(externalRequestId);
        Assert.Equal("LEASED", state);
        Assert.Null(deliveredAt);
    }

    [Fact]
    public async Task SucceedOutbox_NeverRegressesAConfirmedDelivery()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("2200.00");
        var claim = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-a", 10), r => r.ExternalRequestId == externalRequestId);
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, $"provider-{Guid.NewGuid():N}"), $"receipt-{Guid.NewGuid():N}");

        var before = await OutboxStateAsync(externalRequestId);
        Assert.Equal("CONFIRMED", before.State);

        var result = await SucceedAsDispatcherAsync(claim.OutboxId, "dispatcher-a", claim.LeaseVersion, "provider-123");

        Assert.True(result["skipped"]?.GetValue<bool>() ?? false);
        var after = await OutboxStateAsync(externalRequestId);
        Assert.Equal("CONFIRMED", after.State);
    }

    [Fact]
    public async Task FailOutbox_RetryableWithBudgetRemaining_MovesToRetryWait_ClearsLease_AndSchedulesTheFuture()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("3000.00");
        var claim = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-a", 10), r => r.ExternalRequestId == externalRequestId);

        var before = DateTimeOffset.UtcNow;
        var result = await FailAsDispatcherAsync(claim.OutboxId, "dispatcher-a", claim.LeaseVersion, "transport.error.retryable");

        Assert.Equal("RETRY_WAIT", result["state"]!.GetValue<string>());

        var row = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, lease_owner, next_attempt_at, attempt_count, last_error_code from delivery.outbox "
                + "where external_request_id = @id",
                connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                State: reader.GetString(0),
                LeaseOwner: reader.IsDBNull(1) ? null : reader.GetString(1),
                NextAttemptAt: reader.GetFieldValue<DateTimeOffset>(2),
                AttemptCount: reader.GetInt32(3),
                LastErrorCode: reader.GetString(4));
        });
        Assert.Equal("RETRY_WAIT", row.State);
        Assert.Null(row.LeaseOwner);
        Assert.True(row.NextAttemptAt > before);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("transport.error.retryable", row.LastErrorCode);
    }

    // Ticket 19: the published test profile is four total attempts -- the first
    // one plus three retries at 200/400/800 ms (docs/week3/07-autocheck-outline.md,
    // "Максимум попыток Outbox 3"). The seeded policy used to be five attempts
    // with five delays, which spends one extra request on a failing provider.
    [Fact]
    public async Task RetryProfile_IsFourTotalAttempts_WithThreeDelays()
    {
        var policy = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select max_attempts, delays_ms::text from delivery.retry_policy where id = 1", connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (MaxAttempts: reader.GetInt32(0), DelaysMs: reader.GetString(1));
        });

        Assert.Equal(4, policy.MaxAttempts);
        Assert.Equal([200, 400, 800], JsonNode.Parse(policy.DelaysMs)!.AsArray().Select(d => d!.GetValue<int>()));
    }

    [Fact]
    public async Task FailOutbox_RetryableButBudgetExhausted_MovesToDeadAfterExactlyFourAttempts()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("3100.00");

        JsonNode last = new JsonObject();
        for (var i = 0; i < 3; i++)
        {
            await MakeDueNowAsync(externalRequestId);
            var claim = Assert.Single(await ClaimAsDispatcherAsync($"dispatcher-{i}", 10), r => r.ExternalRequestId == externalRequestId);
            last = await FailAsDispatcherAsync(claim.OutboxId, $"dispatcher-{i}", claim.LeaseVersion, "transport.error.retryable");
            Assert.Equal("RETRY_WAIT", last["state"]!.GetValue<string>());
        }

        await MakeDueNowAsync(externalRequestId);
        var fourth = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-4", 10), r => r.ExternalRequestId == externalRequestId);
        last = await FailAsDispatcherAsync(fourth.OutboxId, "dispatcher-4", fourth.LeaseVersion, "transport.error.retryable");

        Assert.Equal("DEAD", last["state"]!.GetValue<string>());
        Assert.Equal(4, last["attemptCount"]!.GetValue<int>());
        var state = await OutboxStateAsync(externalRequestId);
        Assert.Equal("DEAD", state.State);
    }

    [Fact]
    public async Task FailOutbox_SchedulesTheProfileDelays_InOrder()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("3120.00");

        foreach (var (attempt, expectedDelayMs) in new[] { (0, 200), (1, 400), (2, 800) })
        {
            await MakeDueNowAsync(externalRequestId);
            var claim = Assert.Single(
                await ClaimAsDispatcherAsync($"dispatcher-delay-{attempt}", 10), r => r.ExternalRequestId == externalRequestId);
            await FailAsDispatcherAsync(claim.OutboxId, $"dispatcher-delay-{attempt}", claim.LeaseVersion, "transport.error.retryable");

            // Measured server-side against the row's own next_attempt_at, so no
            // client clock or round trip takes part in the comparison.
            var scheduledInMs = await factory.Postgres.ScalarAsOwnerAsync<double>(
                "select (extract(epoch from (next_attempt_at - now())) * 1000)::double precision from delivery.outbox where external_request_id = @id",
                ("id", externalRequestId));
            Assert.InRange(scheduledInMs, expectedDelayMs - 150, expectedDelayMs);
        }
    }

    // The lease was a literal `interval '30 seconds'` inside claim_outbox, so
    // the profile could not reach it at all and a crashed owner held a delivery
    // for 30 seconds even under the 2-second test profile.
    [Fact]
    public async Task ClaimOutbox_TakesItsLeaseFromTheProfileRow_NotAHardcodedInterval()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("1700.00");

        await ClaimAsDispatcherAsync("dispatcher-lease", 10);

        var profileLeaseMs = await factory.Postgres.ScalarAsOwnerAsync<int>(
            "select lease_ms from delivery.retry_policy where id = 1");
        var actualLeaseMs = await factory.Postgres.ScalarAsOwnerAsync<double>(
            "select (extract(epoch from (lease_expires_at - now())) * 1000)::double precision from delivery.outbox where external_request_id = @id",
            ("id", externalRequestId));

        Assert.InRange(actualLeaseMs, profileLeaseMs - 500, profileLeaseMs);
    }

    [Fact]
    public async Task SetLeaseMs_IsWhatTheProfileUsesOnTheNextClaim()
    {
        var original = await factory.Postgres.ScalarAsOwnerAsync<int>("select lease_ms from delivery.retry_policy where id = 1");
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("1750.00");

        try
        {
            var applied = await factory.Postgres.QueryAsOwnerAsync(async connection =>
            {
                await using var command = new NpgsqlCommand("select delivery.set_lease_ms(2000)", connection);
                return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
            });
            Assert.Equal("ok", applied["status"]!.GetValue<string>());

            await ClaimAsDispatcherAsync("dispatcher-profile", 10);

            var actualLeaseMs = await factory.Postgres.ScalarAsOwnerAsync<double>(
                "select (extract(epoch from (lease_expires_at - now())) * 1000)::double precision from delivery.outbox where external_request_id = @id",
                ("id", externalRequestId));
            Assert.InRange(actualLeaseMs, 1_500, 2_000);
        }
        finally
        {
            await factory.Postgres.QueryAsOwnerAsync(async connection =>
            {
                await using var command = new NpgsqlCommand("select delivery.set_lease_ms(@leaseMs)", connection);
                command.Parameters.AddWithValue("leaseMs", original);
                return (await command.ExecuteScalarAsync())!;
            });
        }
    }

    [Fact]
    public async Task FailOutbox_TerminalCode_MovesToDeadImmediately_EvenOnTheFirstAttempt()
    {
        var (_, externalRequestId, _) = await PrepareExternalRequestAsync("3200.00");
        var claim = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-a", 10), r => r.ExternalRequestId == externalRequestId);

        var result = await FailAsDispatcherAsync(claim.OutboxId, "dispatcher-a", claim.LeaseVersion, "http.422.terminal");

        Assert.Equal("DEAD", result["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailOutbox_NeverRegressesAConfirmedDelivery()
    {
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("3300.00");
        var claim = Assert.Single(await ClaimAsDispatcherAsync("dispatcher-a", 10), r => r.ExternalRequestId == externalRequestId);
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, $"provider-{Guid.NewGuid():N}"), $"receipt-{Guid.NewGuid():N}");

        var result = await FailAsDispatcherAsync(claim.OutboxId, "dispatcher-a", claim.LeaseVersion, "transport.error.retryable");

        Assert.True(result["skipped"]?.GetValue<bool>() ?? false);
        var after = await OutboxStateAsync(externalRequestId);
        Assert.Equal("CONFIRMED", after.State);
    }

    // The literal "неподходящее раннее сообщение" case: a receipt can name an
    // externalRequestId (receipt.accept only checks that core.external_requests
    // row exists) before the operation is ever submitted to a process at all.
    // reconcile_inbox must leave it RECEIVED rather than erroring or losing it.
    [Fact]
    public async Task ReconcileInbox_ReceiptForAnOperationWithNoPinnedProcessYet_StaysReceived()
    {
        await DrainReconcileInboxAsync();
        var operationId = await InsertOperationAsync("4000.00");
        var externalRequestId = await CallPrepareExternalAsync(operationId);
        using var client = factory.CreateClient();
        var messageId = $"provider-{Guid.NewGuid():N}";
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, messageId), $"receipt-{Guid.NewGuid():N}");

        var appliedCount = await ReconcileAsReconcilerAsync(10);

        Assert.Equal(0, appliedCount);
        var inboxState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal("RECEIVED", inboxState);

        // Once the same operation is actually submitted, prepare_external takes
        // the already-exists branch (ticket 06) and the process reaches
        // wait_receipt directly -- the next reconcile call now resolves a
        // pinned process and applies the very message that was left behind.
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());
        await DrainAsync(CreateLoop());

        var secondApplied = await ReconcileAsReconcilerAsync(10);
        Assert.Equal(1, secondApplied);

        var (state, currentStepKey) = await ProcessStateAsync(processId);
        Assert.Equal("RUNNING", state);
        Assert.Equal("apply_receipt", currentStepKey);

        var appliedInboxState = await factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from delivery.inbox where message_id = @id", ("id", messageId));
        Assert.Equal("APPLIED", appliedInboxState);

        // See the note on ReconcileInbox_ReceiptArrivingWhileAlreadyWaiting: the
        // applied signal just scheduled a new apply_receipt job (ticket 10's own
        // target is still the ticket 06 stub here) -- drain it so it does not
        // sit READY in the shared queue for a later test's own DrainAsync.
        await DrainAsync(CreateLoop(), assertSucceeded: false);
    }

    [Fact]
    public async Task ReconcileInbox_ReceiptArrivingWhileAlreadyWaiting_AppliesImmediately_AndAdvancesTheProcess()
    {
        await DrainReconcileInboxAsync();
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("4100.00");
        var messageId = $"provider-{Guid.NewGuid():N}";
        await PostSignedReceiptAsync(client, ReceiptBody(externalRequestId, messageId), $"receipt-{Guid.NewGuid():N}");

        var applied = await ReconcileAsReconcilerAsync(10);

        Assert.Equal(1, applied);
        var signal = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select status, signal_type from workflow.workflow_signal where message_id = @id", connection);
            command.Parameters.AddWithValue("id", $"payment-receipt:{messageId}");
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (Status: reader.GetString(0), SignalType: reader.GetString(1));
        });
        Assert.Equal("APPLIED", signal.Status);
        Assert.Equal("payment.receipt", signal.SignalType);

        // The signal application scheduled a new apply_receipt job (ticket 10's
        // own target is still the ticket 06 stub here) -- drain it so it does
        // not sit READY in the shared queue and confuse a later test's own
        // DrainAsync call.
        await DrainAsync(CreateLoop(), assertSucceeded: false);
    }

    [Fact]
    public async Task ReconcileInbox_CalledAgainWithNothingLeftToApply_ReturnsZero()
    {
        await DrainReconcileInboxAsync();
        var (client, externalRequestId, _) = await PrepareExternalRequestAsync("4200.00");
        await PostSignedReceiptAsync(
            client, ReceiptBody(externalRequestId, $"provider-{Guid.NewGuid():N}"), $"receipt-{Guid.NewGuid():N}");
        Assert.Equal(1, await ReconcileAsReconcilerAsync(10));

        Assert.Equal(0, await ReconcileAsReconcilerAsync(10));

        // Same reasoning as the other two Reconcile* tests: the first call above
        // advanced the process into apply_receipt and left a job behind.
        await DrainAsync(CreateLoop(), assertSucceeded: false);
    }

    // delivery.reconcile_inbox has no per-test scope (like workflow.claim_jobs,
    // it is a global FIFO) -- a RECEIVED row left behind by an earlier test in
    // this same shared-container run (e.g. the succeed/fail "never regresses a
    // CONFIRMED delivery" tests, which post a real signed receipt but never
    // reconcile it) would otherwise inflate a later test's own applied count.
    // Draining first makes every count-asserting test start from zero.
    private async Task DrainReconcileInboxAsync()
    {
        var drained = false;
        for (var i = 0; i < 100 && !drained; i++)
        {
            drained = await ReconcileAsReconcilerAsync(50) == 0;
        }

        if (!drained)
        {
            throw new InvalidOperationException("inbox did not drain to zero within 100 reconcile calls");
        }

        // Draining an unrelated leftover RECEIVED message above may itself have
        // just applied a signal and scheduled a fresh apply_receipt job for
        // whatever process that message belonged to -- absorb every such job
        // (the stub target always rejects them) before this test starts using
        // the shared queue for its own assertions.
        await DrainAsync(CreateLoop(), assertSucceeded: false, stopOnFirstNonSuccess: false);
    }

    // Mirrors FailJobRetryPrivilegesTests' own worker-role check, for both new
    // perimeter roles at once: EXECUTE on exactly their declared functions,
    // no table DML anywhere, and no membership in any other role.
    [Fact]
    public async Task OutboxDispatcherAndInboxReconciler_ExecuteExactlyTheirDeclaredFunctions_HaveNoTableDml_AndNoRoleMembership()
    {
        var dispatcherExecutable = await ExecutableFunctionsAsync("outbox_dispatcher");
        Assert.Equal(
            new HashSet<(string, string)>
            {
                ("delivery", "claim_outbox"),
                ("delivery", "succeed_outbox"),
                ("delivery", "fail_outbox"),
            },
            dispatcherExecutable);

        var reconcilerExecutable = await ExecutableFunctionsAsync("inbox_reconciler");
        Assert.Equal(new HashSet<(string, string)> { ("delivery", "reconcile_inbox") }, reconcilerExecutable);

        Assert.Empty(await TableDmlRightsAsync("outbox_dispatcher"));
        Assert.Empty(await TableDmlRightsAsync("inbox_reconciler"));

        Assert.Empty(await RoleMembershipsAsync("outbox_dispatcher"));
        Assert.Empty(await RoleMembershipsAsync("inbox_reconciler"));
    }

    private async Task<HashSet<(string Schema, string Function)>> ExecutableFunctionsAsync(string roleName) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "with checked_role as (select oid from pg_catalog.pg_roles where rolname = @roleName) "
                + "select n.nspname as schema_name, p.proname as function_name, "
                + "has_function_privilege(r.oid, p.oid, 'EXECUTE') as can_execute "
                + "from pg_catalog.pg_proc p "
                + "join pg_catalog.pg_namespace n on n.oid = p.pronamespace "
                // Every non-system schema, not just 'delivery': the ticket's own
                // pitfall ("права проверяются не по нашему намерению, а по
                // фактическим привилегиям ... не через права по умолчанию для
                // схемы") means a stray PUBLIC EXECUTE grant on a function in
                // core/workflow/api would still make it callable in principle --
                // has_function_privilege doesn't care whether the role also
                // holds schema USAGE, and neither does the checker's own scan.
                + "left join checked_role r on true "
                + "where n.nspname <> 'information_schema' and n.nspname not like 'pg\\_%'",
                connection);
            command.Parameters.AddWithValue("roleName", roleName);
            await using var reader = await command.ExecuteReaderAsync();
            var boundaries = new HashSet<(string, string)>();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(2) && reader.GetBoolean(2))
                {
                    boundaries.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            return boundaries;
        });

    private async Task<List<string>> TableDmlRightsAsync(string roleName) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "with checked_role as (select oid from pg_catalog.pg_roles where rolname = @roleName) "
                + "select n.nspname, c.relname, "
                + "has_table_privilege(r.oid, c.oid, 'INSERT') or has_table_privilege(r.oid, c.oid, 'UPDATE') "
                + "or has_table_privilege(r.oid, c.oid, 'DELETE') "
                + "from pg_catalog.pg_class c "
                + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
                + "left join checked_role r on true "
                + "where c.relkind in ('r', 'p', 'f') and n.nspname <> 'information_schema' and n.nspname not like 'pg\\_%'",
                connection);
            command.Parameters.AddWithValue("roleName", roleName);
            await using var reader = await command.ExecuteReaderAsync();
            var offending = new List<string>();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(2) && reader.GetBoolean(2))
                {
                    offending.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                }
            }

            return offending;
        });

    private async Task<List<string>> RoleMembershipsAsync(string roleName) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select g.rolname from pg_catalog.pg_auth_members m "
                + "join pg_catalog.pg_roles r on r.oid = m.member "
                + "join pg_catalog.pg_roles g on g.oid = m.roleid "
                + "where r.rolname = @roleName",
                connection);
            command.Parameters.AddWithValue("roleName", roleName);
            await using var reader = await command.ExecuteReaderAsync();
            var groups = new List<string>();
            while (await reader.ReadAsync())
            {
                groups.Add(reader.GetString(0));
            }

            return groups;
        });

    private async Task<List<ClaimedRow>> ClaimAsDispatcherAsync(string owner, int limit)
    {
        await using var connection = new NpgsqlConnection(factory.Postgres.OutboxDispatcherConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select outbox_id, lease_version, external_request_id, correlation_id, amount, currency "
            + "from delivery.claim_outbox(@owner, @limit)",
            connection);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<ClaimedRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new ClaimedRow(
                reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5)));
        }

        return rows;
    }

    private async Task<JsonNode> SucceedAsDispatcherAsync(Guid outboxId, string owner, long leaseVersion, string providerPaymentId)
    {
        await using var connection = new NpgsqlConnection(factory.Postgres.OutboxDispatcherConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select delivery.succeed_outbox(@outboxId, @owner, @leaseVersion, @providerPaymentId)", connection);
        command.Parameters.AddWithValue("outboxId", outboxId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new NpgsqlParameter("leaseVersion", NpgsqlDbType.Bigint) { Value = leaseVersion });
        command.Parameters.AddWithValue("providerPaymentId", providerPaymentId);
        var raw = (string)(await command.ExecuteScalarAsync())!;
        return JsonNode.Parse(raw)!;
    }

    private async Task<JsonNode> FailAsDispatcherAsync(Guid outboxId, string owner, long leaseVersion, string errorCode)
    {
        await using var connection = new NpgsqlConnection(factory.Postgres.OutboxDispatcherConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select delivery.fail_outbox(@outboxId, @owner, @leaseVersion, @errorCode)", connection);
        command.Parameters.AddWithValue("outboxId", outboxId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new NpgsqlParameter("leaseVersion", NpgsqlDbType.Bigint) { Value = leaseVersion });
        command.Parameters.AddWithValue("errorCode", errorCode);
        var raw = (string)(await command.ExecuteScalarAsync())!;
        return JsonNode.Parse(raw)!;
    }

    private async Task<int> ReconcileAsReconcilerAsync(int limit)
    {
        await using var connection = new NpgsqlConnection(factory.Postgres.InboxReconcilerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select delivery.reconcile_inbox(@limit)", connection);
        command.Parameters.AddWithValue("limit", limit);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task MakeDueNowAsync(string externalRequestId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "update delivery.outbox set next_attempt_at = now() - interval '1 second' where external_request_id = @id",
                connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            return await command.ExecuteNonQueryAsync();
        });

    private async Task<(string State, DateTimeOffset? DeliveredAt)> OutboxStateAsync(string externalRequestId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, delivered_at from delivery.outbox where external_request_id = @id", connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1));
        });

    private async Task<(string State, string CurrentStepKey)> ProcessStateAsync(Guid processId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select state, current_step_key from workflow.process_instance where process_id = @processId", connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1));
        });

    private static async Task<ActionResponse> PostSignedReceiptAsync(HttpClient client, string body, string idempotencyKey)
    {
        var header = ProviderSignature.Header(ApiFactory.ProviderHmacSecret, Encoding.UTF8.GetBytes(body));
        return await client.PostRawActionAsync("/api/receipt/accept", ProviderToken(), body, idempotencyKey, header);
    }

    private static string ReceiptBody(string externalRequestId, string messageId, string outcome = "COMPLETED", int version = 1) =>
        new JsonObject
        {
            ["externalRequestId"] = externalRequestId,
            ["messageId"] = messageId,
            ["occurredAt"] = "2026-09-04T12:00:00Z",
            ["outcome"] = outcome,
            ["providerPaymentId"] = messageId,
            ["version"] = version,
        }.ToJsonString();

    private static string ProviderToken() => ApiFactory.CreateToken("receipt-provider", "receipt-adapter", "receipt:write");

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

    // Same setup PrepareExternalTests (ticket 06) and ReceiptAcceptTests
    // (ticket 08) already use: submit a fresh PAYMENT_EXECUTION operation and
    // drain the worker to wait_receipt, so a real, pinned Outbox row exists.
    private async Task<(HttpClient Client, string ExternalRequestId, Guid OperationId)> PrepareExternalRequestAsync(string amount)
    {
        var operationId = await InsertOperationAsync(amount);
        var client = factory.CreateClient();

        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);

        await DrainAsync(CreateLoop());

        return (client, $"ext-{operationId}", operationId);
    }

    private async Task<string> CallPrepareExternalAsync(Guid operationId)
    {
        var result = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand("select payment.prepare_external_v1(@context, @payload)", connection);
            command.Parameters.Add(new NpgsqlParameter("context", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["correlationId"] = Guid.NewGuid().ToString() }.ToJsonString(),
            });
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = new JsonObject { ["operationId"] = operationId.ToString() }.ToJsonString(),
            });
            var raw = (string)(await command.ExecuteScalarAsync())!;
            return JsonNode.Parse(raw)!;
        });

        return result["result"]!["externalRequestId"]!.GetValue<string>();
    }

    private async Task<Guid> InsertOperationAsync(string amount) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.operations (request_id, operation_kind, amount, currency, status) "
                + "values (@requestId, 'PAYMENT_EXECUTION', @amount, 'RUB', 'CREATED') returning operation_id",
                connection);
            command.Parameters.AddWithValue("requestId", $"req-{Guid.NewGuid():N}");
            command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric)
            {
                Value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            });
            return (Guid)(await command.ExecuteScalarAsync())!;
        });

    private WorkerLoop CreateLoop()
    {
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            $"owner-{Guid.NewGuid():N}", workerDataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    private static async Task DrainAsync(
        WorkerLoop loop, int maxSteps = 20, bool assertSucceeded = true, bool stopOnFirstNonSuccess = true)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var step = await loop.RunOnceAsync(CancellationToken.None);
            if (step.Outcome == WorkerStepOutcome.NoJob)
            {
                return;
            }

            if (assertSucceeded)
            {
                Assert.True(step.Outcome == WorkerStepOutcome.Succeeded, $"outcome={step.Outcome} errorCode={step.ErrorCode}");
            }
            else if (step.Outcome != WorkerStepOutcome.Succeeded && stopOnFirstNonSuccess)
            {
                return;
            }
        }

        throw new TimeoutException("worker loop did not drain to an empty queue within the expected number of steps");
    }

    private sealed record ClaimedRow(
        Guid OutboxId, long LeaseVersion, string ExternalRequestId, Guid CorrelationId, string Amount, string Currency);
}
