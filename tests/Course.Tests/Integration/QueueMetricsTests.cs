using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Npgsql;

namespace Course.Tests.Integration;

// Ticket 08 (week 4): the six required series, proven where they are decided --
// through `api`'s own /metrics over HTTP, parsed, and against a queue whose state
// the test changes underneath. A test that only checked the six names were present
// would pass on six hard-coded zeroes.
[Collection("integration")]
public sealed class QueueMetricsTests
{
    private static readonly string[] RequiredSamples =
    [
        "workflow_jobs_ready",
        "workflow_job_oldest_age_seconds",
        "workflow_processes_waiting",
        "outbox_pending",
        "outbox_oldest_age_seconds",
        "workflow_failures_total",
    ];

    private readonly ApiFactory factory;

    public QueueMetricsTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task AllSixRequiredSeriesArePublishedWithTheirContractTypes()
    {
        var (types, samples) = await ScrapeAsync();

        foreach (var name in RequiredSamples)
        {
            Assert.Contains(name, samples.Keys);
        }

        Assert.Equal("gauge", types["workflow_jobs_ready"]);
        Assert.Equal("gauge", types["workflow_job_oldest_age_seconds"]);
        Assert.Equal("gauge", types["workflow_processes_waiting"]);
        Assert.Equal("gauge", types["outbox_pending"]);
        Assert.Equal("gauge", types["outbox_oldest_age_seconds"]);

        // The counter is declared by family name, without the suffix its sample
        // carries -- the shape the contract spells out explicitly.
        Assert.Equal("counter", types["workflow_failures"]);
        Assert.DoesNotContain("workflow_failures_total", types.Keys);
    }

    [Fact]
    public async Task APendingDeliveryIsCountedAndAgesFromItsCreation()
    {
        var before = (await ScrapeAsync()).Samples["outbox_pending"];

        var externalRequestId = await InsertPendingDeliveryAsync("PENDING", TimeSpan.FromSeconds(30));

        try
        {
            var after = await ScrapeAsync();

            Assert.Equal(before + 1, after.Samples["outbox_pending"]);
            Assert.True(
                after.Samples["outbox_oldest_age_seconds"] >= 30,
                $"oldest age was {after.Samples["outbox_oldest_age_seconds"]}");
        }
        finally
        {
            await DeleteDeliveryAsync(externalRequestId);
        }
    }

    // DEAD is the technical outcome of a delivery, not a pending one: the contract
    // excludes it from both Outbox series while the process it belongs to keeps
    // waiting for a receipt.
    [Fact]
    public async Task ADeadDeliveryIsNotCountedAsPending()
    {
        var before = (await ScrapeAsync()).Samples["outbox_pending"];

        var externalRequestId = await InsertPendingDeliveryAsync("DEAD", TimeSpan.FromSeconds(5));

        try
        {
            Assert.Equal(before, (await ScrapeAsync()).Samples["outbox_pending"]);
        }
        finally
        {
            await DeleteDeliveryAsync(externalRequestId);
        }
    }

    [Fact]
    public async Task AFailedProcessIsCountedAsATerminalWorkflowFailure()
    {
        var before = (await ScrapeAsync()).Samples["workflow_failures_total"];

        var processId = await InsertProcessAsync("FAILED");

        try
        {
            Assert.Equal(before + 1, (await ScrapeAsync()).Samples["workflow_failures_total"]);

            // Scraped again, because a counter that walked backwards between two
            // scrapes is the failure this series is computed from stored state to
            // avoid -- an in-memory counter would reset on the restart this week is
            // built around.
            Assert.Equal(before + 1, (await ScrapeAsync()).Samples["workflow_failures_total"]);
        }
        finally
        {
            await DeleteProcessAsync(processId);
        }
    }

    [Fact]
    public async Task AWaitingProcessIsCountedAsWaiting()
    {
        var before = (await ScrapeAsync()).Samples["workflow_processes_waiting"];

        var processId = await InsertProcessAsync("WAITING_SIGNAL");

        try
        {
            Assert.Equal(before + 1, (await ScrapeAsync()).Samples["workflow_processes_waiting"]);
        }
        finally
        {
            await DeleteProcessAsync(processId);
        }
    }

    private async Task<(Dictionary<string, string> Types, Dictionary<string, double> Samples)> ScrapeAsync()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# EOF", lines[^1]);

        foreach (var line in lines[..^1])
        {
            var type = Regex.Match(line, @"^# TYPE ([A-Za-z_:][A-Za-z0-9_:]*) (counter|gauge)$");
            if (type.Success)
            {
                types[type.Groups[1].Value] = type.Groups[2].Value;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            var sample = Regex.Match(line, @"^([A-Za-z_:][A-Za-z0-9_:]*)(\{[^}]*\})? (-?[0-9]+(?:\.[0-9]+)?)$");
            Assert.True(sample.Success, $"not a valid OpenMetrics sample: {line}");
            samples[sample.Groups[1].Value] = double.Parse(sample.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        return (types, samples);
    }

    // Written straight into the physical tables as the owner: what is under test is
    // the projection, not the domain path that produces a delivery, and the domain
    // path has its own tests.
    private async Task<string> InsertPendingDeliveryAsync(string state, TimeSpan age)
    {
        var externalRequestId = $"metrics-probe-{Guid.NewGuid():N}";

        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var insert = new NpgsqlCommand(
                """
                with operation as (
                  insert into core.operations (request_id, operation_kind, amount, currency, status)
                  -- CREATED, not PROCESSING: core.operations keeps status and the
                  -- pinned process in step (operations_created_iff_no_process), and
                  -- this probe has no process of its own.
                  values (@id, 'PAYMENT_EXECUTION', 10.00, 'RUB', 'CREATED')
                  returning operation_id
                ), request as (
                  insert into core.external_requests (external_request_id, operation_id, payload_hash, state)
                  select @id, operation_id, repeat('0', 64), 'CREATED' from operation
                  returning external_request_id
                )
                insert into delivery.outbox
                       (external_request_id, correlation_id, amount, currency, state, created_at)
                select external_request_id, pg_catalog.gen_random_uuid(), '10.00', 'RUB', @state,
                       pg_catalog.now() - make_interval(secs => @age)
                  from request
                """,
                connection);
            insert.Parameters.AddWithValue("id", externalRequestId);
            insert.Parameters.AddWithValue("state", state);
            insert.Parameters.AddWithValue("age", age.TotalSeconds);
            return await insert.ExecuteNonQueryAsync();
        });

        return externalRequestId;
    }

    private Task<int> DeleteDeliveryAsync(string externalRequestId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var delete = new NpgsqlCommand(
                """
                delete from delivery.outbox where external_request_id = @id;
                delete from core.external_requests where external_request_id = @id;
                delete from core.operations where request_id = @id;
                """,
                connection);
            delete.Parameters.AddWithValue("id", externalRequestId);
            return await delete.ExecuteNonQueryAsync();
        });

    private async Task<Guid> InsertProcessAsync(string state)
    {
        var processId = Guid.NewGuid();

        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into workflow.process_instance
                       (process_id, flow_name, flow_version, business_key, state,
                        current_step_key, start_data, start_data_hash)
                select @processId, flow_name, version, @businessKey, @state,
                       'metrics-probe-step', '{}'::jsonb, repeat('0', 64)
                  from workflow.flow_version
                 order by flow_name, version
                 limit 1
                """,
                connection);
            insert.Parameters.AddWithValue("processId", processId);
            insert.Parameters.AddWithValue("businessKey", $"metrics-probe-{processId:N}");
            insert.Parameters.AddWithValue("state", state);
            return await insert.ExecuteNonQueryAsync();
        });

        return processId;
    }

    private Task<int> DeleteProcessAsync(Guid processId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var delete = new NpgsqlCommand(
                "delete from workflow.process_instance where process_id = @processId", connection);
            delete.Parameters.AddWithValue("processId", processId);
            return await delete.ExecuteNonQueryAsync();
        });
}
