using System.Text;
using System.Text.Json.Nodes;
using Npgsql;

namespace Course.Tests.Integration;

// Ticket 12 (week 4): diagnostics.stalled version 1.
//
// The predicate is four conditions and each one carries its weight. DEAD alone
// would keep an operation a late receipt already completed in the selection
// forever, because migration 049 deliberately keeps dead_at across DEAD ->
// CONFIRMED; a threshold on attempt_count instead of state would catch
// deliveries that still have a retry scheduled. Both are tested here.
//
// The container is shared with every other test in this collection, several of
// which leave DEAD deliveries of their own behind, so these tests assert about
// their own chains' presence or absence rather than about the size of the
// selection -- except the one empty-selection test, which proves the rendering
// inside a transaction it rolls back.
[Collection("integration")]
public sealed class DiagnosticsStalledTests
{
    private readonly ApiFactory factory;

    public DiagnosticsStalledTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task TheActionIsRegisteredAsThePublishedManifestDescribesIt()
    {
        var row = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select version, http_method, outcomes, enabled, is_default, target_schema, target_function "
                + "from autocheck.action_definitions where module = 'diagnostics' and action = 'stalled'",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "diagnostics.stalled is not in the catalog");
            var read = (
                Version: reader.GetInt32(0),
                Method: reader.GetString(1),
                Outcomes: reader.GetFieldValue<JsonNode>(2).ToJsonString(),
                Enabled: reader.GetBoolean(3),
                IsDefault: reader.GetBoolean(4),
                Schema: reader.GetString(5),
                Function: reader.GetString(6));
            Assert.False(await reader.ReadAsync(), "more than one version of diagnostics.stalled is published");
            return read;
        });

        Assert.Equal(1, row.Version);
        Assert.Equal("POST", row.Method);
        Assert.Equal("[\"FOUND\"]", row.Outcomes);
        Assert.True(row.Enabled);
        Assert.True(row.IsDefault);
        Assert.Equal("diagnostics", row.Schema);
        Assert.Equal("stalled_v1", row.Function);
    }

    [Fact]
    public async Task AnExhaustedDeliveryStillWaitingForItsReceiptIsReported()
    {
        var chain = await NewChainAsync();
        await ExhaustAsync(chain.ExternalRequestId);

        var items = await StalledItemsAsync();

        var item = Assert.Single(items, node => node!["operationId"]!.GetValue<string>() == chain.OperationId.ToString());
        Assert.Equal(
            ItemFields,
            item!.AsObject().Select(property => property.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(chain.ProcessId.ToString(), item["processId"]!.GetValue<string>());
        Assert.Equal(chain.ExternalRequestId, item["externalRequestId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADeliveryWithARetryStillScheduledIsNotReported()
    {
        var chain = await NewChainAsync();

        // One failure short of the profile's budget: the row is RETRY_WAIT with a
        // next attempt ahead of it, which is the case an attempt-count threshold
        // would wrongly report as stalled.
        await FailOnceAsync(chain.ExternalRequestId, "dispatcher-partial");
        Assert.Equal("RETRY_WAIT", await StateAsync(chain.ExternalRequestId));

        var items = await StalledItemsAsync();

        Assert.DoesNotContain(items, node => node!["operationId"]!.GetValue<string>() == chain.OperationId.ToString());
    }

    [Fact]
    public async Task AnOperationCompletedByALateReceiptLeavesTheSelection()
    {
        var chain = await NewChainAsync();
        await ExhaustAsync(chain.ExternalRequestId);
        Assert.Contains(
            await StalledItemsAsync(), node => node!["operationId"]!.GetValue<string>() == chain.OperationId.ToString());

        await AcceptReceiptAsync(chain.ExternalRequestId, $"late-{Guid.NewGuid():N}");
        Assert.Equal("CONFIRMED", await StateAsync(chain.ExternalRequestId));
        // dead_at survives the confirmation on purpose, which is exactly why the
        // predicate cannot be "just DEAD".
        Assert.NotNull(await DeadAtAsync(chain.ExternalRequestId));
        await ContinueProcessAsync(chain.ProcessId, chain.OperationId);

        var items = await StalledItemsAsync();

        Assert.DoesNotContain(items, node => node!["operationId"]!.GetValue<string>() == chain.OperationId.ToString());
    }

    [Fact]
    public async Task OneOperationWithTwoExhaustedDeliveriesIsReportedOnce()
    {
        var chain = await NewChainAsync();
        await ExhaustAsync(chain.ExternalRequestId);
        var second = await AddDeliveryAsync(chain.OperationId, $"{chain.ExternalRequestId}-second");
        await ExhaustAsync(second);

        var items = await StalledItemsAsync();

        Assert.Single(items, node => node!["operationId"]!.GetValue<string>() == chain.OperationId.ToString());
    }

    [Fact]
    public async Task TheSelectionIsOrderedByOperationIdentifier()
    {
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var chain = await NewChainAsync();
            await ExhaustAsync(chain.ExternalRequestId);
        }

        var items = await StalledItemsAsync();

        var operationIds = items.Select(node => node!["operationId"]!.GetValue<string>()).ToList();
        Assert.True(operationIds.Count >= 3, "the three chains this test created are not in the selection");
        Assert.Equal(operationIds.OrderBy(id => id, StringComparer.Ordinal).ToList(), operationIds);
        Assert.Equal(operationIds.Distinct(StringComparer.Ordinal).Count(), operationIds.Count);
    }

    // The rendering of an empty selection cannot be observed against a shared
    // container that other tests leave DEAD deliveries in, so it is observed with
    // the predicate's first condition temporarily false, inside a transaction
    // that is rolled back and never committed.
    [Fact]
    public async Task AnEmptySelectionIsStillItemsAndStillFound()
    {
        var chain = await NewChainAsync();
        await ExhaustAsync(chain.ExternalRequestId);

        var envelope = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var hide = new NpgsqlCommand(
                "update delivery.outbox set state = 'RETRY_WAIT' where state = 'DEAD'", connection, transaction);
            await hide.ExecuteNonQueryAsync();

            await using var call = new NpgsqlCommand(
                "select diagnostics.stalled_v1('{}'::jsonb, '{}'::jsonb)", connection, transaction);
            var json = (string)(await call.ExecuteScalarAsync())!;
            await transaction.RollbackAsync();
            return JsonNode.Parse(json)!;
        });

        Assert.Equal("ok", envelope["status"]!.GetValue<string>());
        Assert.Equal("FOUND", envelope["outcome"]!.GetValue<string>());
        Assert.Empty(envelope["result"]!["items"]!.AsArray());

        // And the row really is still DEAD afterwards: the probe above rolled back.
        Assert.Equal("DEAD", await StateAsync(chain.ExternalRequestId));
    }

    [Fact]
    public async Task TheCallChangesNoStoredState()
    {
        var chain = await NewChainAsync();
        await ExhaustAsync(chain.ExternalRequestId);

        var before = await SnapshotAsync(chain);
        await StalledItemsAsync();
        await StalledItemsAsync();
        var after = await SnapshotAsync(chain);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task WithoutAJwtTheActionIsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/diagnostics/stalled")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheDiagnosticsPolicyTheActionIsForbidden()
    {
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/diagnostics/stalled",
            ApiFactory.CreateToken("candidate-client", "web", "payment:read workflow:read"),
            new JsonObject());

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("access.denied", response.Body["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"operationId\":\"6b8f9a1e-0000-4000-8000-000000000000\"}")]
    [InlineData("{\"limit\":10}")]
    [InlineData("[]")]
    public async Task APayloadOutsideTheSchemaIsRejected(string body)
    {
        using var client = factory.CreateClient();

        var response = await client.PostRawActionAsync("/api/diagnostics/stalled", DiagnosticsToken(), body);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("payload.invalid", response.Body["code"]!.GetValue<string>());
    }

    private static readonly string[] ItemFields = ["externalRequestId", "operationId", "processId"];

    private async Task<JsonArray> StalledItemsAsync()
    {
        using var client = factory.CreateClient();
        var response = await client.PostActionAsync("/api/diagnostics/stalled", DiagnosticsToken(), new JsonObject());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ok", response.Body["status"]!.GetValue<string>());
        Assert.Equal("FOUND", response.Body["outcome"]!.GetValue<string>());
        Assert.NotNull(response.Body["meta"]!["correlationId"]);
        return response.Body["result"]!["items"]!.AsArray();
    }

    private sealed record Chain(Guid OperationId, Guid ProcessId, string ExternalRequestId);

    // The domain path that produces a delivery is tested elsewhere; what is under
    // test here is the predicate over the stored rows, so the chain is written
    // directly -- process WAITING_SIGNAL and operation PROCESSING, which is what
    // the chain genuinely looks like while it waits for a receipt.
    private async Task<Chain> NewChainAsync()
    {
        var externalRequestId = $"stalled-probe-{Guid.NewGuid():N}";

        var ids = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var insert = new NpgsqlCommand(
                """
                with process as (
                  insert into workflow.process_instance
                         (flow_name, flow_version, business_key, state,
                          current_step_key, start_data, start_data_hash)
                  select flow_name, version, @id, 'WAITING_SIGNAL',
                         'wait_receipt', '{}'::jsonb, repeat('0', 64)
                    from workflow.flow_version
                   order by flow_name, version
                   limit 1
                  returning process_id
                ), operation as (
                  insert into core.operations
                         (request_id, operation_kind, amount, currency, status, process_id)
                  select @id, 'PAYMENT_EXECUTION', 10.00, 'RUB', 'PROCESSING', process_id
                    from process
                  returning operation_id, process_id
                ), request as (
                  insert into core.external_requests (external_request_id, operation_id, payload_hash, state)
                  select @id, operation_id, repeat('0', 64), 'SENT' from operation
                  returning external_request_id
                ), delivery as (
                  insert into delivery.outbox (external_request_id, correlation_id, amount, currency, state)
                  select external_request_id, pg_catalog.gen_random_uuid(), '10.00', 'RUB', 'PENDING'
                    from request
                  returning outbox_id
                )
                select operation_id, process_id from operation
                """,
                connection);
            insert.Parameters.AddWithValue("id", externalRequestId);
            await using var reader = await insert.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (OperationId: reader.GetGuid(0), ProcessId: reader.GetGuid(1));
        });

        return new Chain(ids.OperationId, ids.ProcessId, externalRequestId);
    }

    private Task<string> AddDeliveryAsync(Guid operationId, string externalRequestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var insert = new NpgsqlCommand(
                """
                with request as (
                  insert into core.external_requests (external_request_id, operation_id, payload_hash, state)
                  values (@id, @operationId, repeat('0', 64), 'SENT')
                  returning external_request_id
                )
                insert into delivery.outbox (external_request_id, correlation_id, amount, currency, state)
                select external_request_id, pg_catalog.gen_random_uuid(), '10.00', 'RUB', 'PENDING' from request
                """,
                connection);
            insert.Parameters.AddWithValue("id", externalRequestId);
            insert.Parameters.AddWithValue("operationId", operationId);
            await insert.ExecuteNonQueryAsync();
            return externalRequestId;
        });

    // Four attempts including the first: the published test profile's budget, so
    // the fourth failure is the one that spends it.
    private async Task ExhaustAsync(string externalRequestId)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await FailOnceAsync(externalRequestId, $"dispatcher-{attempt}");
        }

        Assert.Equal("DEAD", await StateAsync(externalRequestId));
    }

    private async Task FailOnceAsync(string externalRequestId, string owner)
    {
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
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

        var leaseVersion = await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var claim = new NpgsqlCommand(
                "select lease_version from delivery.claim_outbox(@owner, 500) where external_request_id = @id", connection);
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("id", externalRequestId);
            return (long?)await claim.ExecuteScalarAsync();
        });
        Assert.NotNull(leaseVersion);

        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var fail = new NpgsqlCommand(
                """
                select delivery.fail_outbox(
                  (select outbox_id from delivery.outbox where external_request_id = @id),
                  @owner, @leaseVersion, 'transport.error.retryable')
                """,
                connection);
            fail.Parameters.AddWithValue("id", externalRequestId);
            fail.Parameters.AddWithValue("owner", owner);
            fail.Parameters.AddWithValue("leaseVersion", leaseVersion.Value);
            return await fail.ExecuteScalarAsync();
        });
    }

    // The real registered action, with the context its signature boundary stamps
    // -- not a hand-written copy of its UPDATE, because the property under test is
    // that a late receipt takes a DEAD delivery to CONFIRMED.
    private Task<string> AcceptReceiptAsync(string externalRequestId, string messageId)
    {
        var body =
            $"{{\"messageId\":\"{messageId}\",\"externalRequestId\":\"{externalRequestId}\"," +
            "\"version\":1,\"outcome\":\"COMPLETED\",\"providerPaymentId\":\"provider-late\"," +
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

    // What the process and the operation look like once the late receipt has been
    // applied: the two conditions the predicate keeps beyond "DEAD".
    private Task<int> ContinueProcessAsync(Guid processId, Guid operationId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                """
                with p as (
                  update workflow.process_instance set state = 'COMPLETED' where process_id = @processId
                  returning process_id
                )
                update core.operations set status = 'COMPLETED' where operation_id = @operationId
                """,
                connection);
            command.Parameters.AddWithValue("processId", processId);
            command.Parameters.AddWithValue("operationId", operationId);
            return await command.ExecuteNonQueryAsync();
        });

    private Task<string> StateAsync(string externalRequestId) =>
        factory.Postgres.ScalarAsOwnerAsync<string>(
            "select state from delivery.outbox where external_request_id = @id", ("id", externalRequestId));

    private async Task<DateTime?> DeadAtAsync(string externalRequestId) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select dead_at from delivery.outbox where external_request_id = @id", connection);
            command.Parameters.AddWithValue("id", externalRequestId);
            var value = await command.ExecuteScalarAsync();
            return value is DBNull or null ? (DateTime?)null : (DateTime)value;
        });

    // Everything the action is forbidden to touch: the delivery, the operation,
    // the process and the number of jobs standing against that process.
    private Task<string> SnapshotAsync(Chain chain) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                """
                select pg_catalog.jsonb_build_object(
                  'outbox', (select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(o) order by o.outbox_id)
                               from delivery.outbox o
                               join core.external_requests e on e.external_request_id = o.external_request_id
                              where e.operation_id = @operationId),
                  'operation', (select pg_catalog.to_jsonb(o) from core.operations o where o.operation_id = @operationId),
                  'process', (select pg_catalog.to_jsonb(p) from workflow.process_instance p where p.process_id = @processId),
                  'jobs', (select pg_catalog.count(*) from workflow.workflow_job j where j.process_id = @processId),
                  'inbox', (select pg_catalog.count(*) from delivery.inbox i
                              join core.external_requests e on e.external_request_id = i.external_request_id
                             where e.operation_id = @operationId)
                )::text
                """,
                connection);
            command.Parameters.AddWithValue("operationId", chain.OperationId);
            command.Parameters.AddWithValue("processId", chain.ProcessId);
            return (string)(await command.ExecuteScalarAsync())!;
        });

    private static string DiagnosticsToken() => ApiFactory.CreateToken("operator", "backoffice", "diagnostics:read");
}
