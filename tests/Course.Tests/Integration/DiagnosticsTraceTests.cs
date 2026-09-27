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

// Ticket 11 (week 4): diagnostics.trace version 1 over the seam that matters --
// the real HTTP action against a real PostgreSQL, driving a real payment to its
// receipt first.
//
// The property the whole ticket exists for is the first test here: eleven
// different identifiers of one chain return one and the same set of subject
// facts, because every section is computed from the chain's root and not from
// whichever identifier the operator happened to know.
[Collection("integration")]
public sealed class DiagnosticsTraceTests : IDisposable
{
    private readonly ApiFactory factory;
    private readonly NpgsqlDataSource workerDataSource;

    public DiagnosticsTraceTests(ApiFactory factory)
    {
        this.factory = factory;
        workerDataSource = NpgsqlDataSource.Create(factory.Postgres.WorkerConnectionString);
    }

    public void Dispose() => workerDataSource.Dispose();

    // The row the verifying run reads out of the read-only projection before it
    // calls anything: module, action, version, method, outcome, enabled, default.
    [Fact]
    public async Task TheActionIsRegisteredAsThePublishedManifestDescribesIt()
    {
        var row = await factory.Postgres.QueryAsRuntimeAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "select version, http_method, outcomes, enabled, is_default, target_schema, target_function "
                + "from autocheck.action_definitions where module = 'diagnostics' and action = 'trace'",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "diagnostics.trace is not in the catalog");
            var read = (
                Version: reader.GetInt32(0),
                Method: reader.GetString(1),
                Outcomes: reader.GetFieldValue<JsonNode>(2).ToJsonString(),
                Enabled: reader.GetBoolean(3),
                IsDefault: reader.GetBoolean(4),
                Schema: reader.GetString(5),
                Function: reader.GetString(6));
            Assert.False(await reader.ReadAsync(), "more than one version of diagnostics.trace is published");
            return read;
        });

        Assert.Equal(1, row.Version);
        Assert.Equal("POST", row.Method);
        Assert.Equal("[\"FOUND\"]", row.Outcomes);
        Assert.True(row.Enabled);
        Assert.True(row.IsDefault);
        Assert.Equal("diagnostics", row.Schema);
        Assert.Equal("trace_v1", row.Function);
    }

    [Fact]
    public async Task EveryIdentifierOfOneChainReturnsTheSameSubjectFacts()
    {
        var chain = await BuildProviderChainAsync();

        string? reference = null;
        foreach (var (kind, identifier) in chain.Identifiers())
        {
            var response = await TraceAsync(identifier);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("FOUND", response.Body["outcome"]!.GetValue<string>());

            var result = response.Body["result"]!;
            var query = result["query"]!;
            Assert.Equal(identifier, query["identifier"]!.GetValue<string>());

            var matchedBy = query["matchedBy"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
            Assert.Contains(kind, matchedBy);
            Assert.Equal(matchedBy.Count, matchedBy.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(matchedBy, SortByContractOrder(matchedBy));

            // The operation's own section is the cheapest proof the resolver
            // landed on the right chain at all, before the byte comparison below
            // makes the stronger claim.
            Assert.Equal(chain.OperationId.ToString(), result["operation"]!["operationId"]!.GetValue<string>());
            Assert.Equal(chain.ProcessId.ToString(), result["process"]!["processId"]!.GetValue<string>());

            var subjectFacts = WithoutQuery(result);
            reference ??= subjectFacts;
            Assert.Equal(reference, subjectFacts);
        }
    }

    [Fact]
    public async Task AllTwelveSectionsArePresentAndTheChainsOwnEvidenceIsInThem()
    {
        var chain = await BuildProviderChainAsync();

        var result = (await TraceAsync(chain.OperationId.ToString())).Body["result"]!.AsObject();

        Assert.Equal(
            TwelveSections,
            result.Select(property => property.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.NotEmpty(result["dispatches"]!.AsArray());
        // Every hop of the chain that goes through the HTTP surface: the command
        // that started it, the worker's own calls, and the receipt arriving.
        var modules = result["dispatches"]!.AsArray()
            .Select(item => $"{item!["module"]!.GetValue<string>()}.{item["action"]!.GetValue<string>()}")
            .ToList();
        Assert.Contains("payment.submit", modules);
        Assert.Contains("receipt.accept", modules);
        Assert.Contains("payment.prepare_external", modules);
        Assert.NotEmpty(result["operationEvents"]!.AsArray());
        Assert.NotEmpty(result["steps"]!.AsArray());
        Assert.NotEmpty(result["jobs"]!.AsArray());
        Assert.NotEmpty(result["attempts"]!.AsArray());
        Assert.NotEmpty(result["outbox"]!.AsArray());
        Assert.NotEmpty(result["inbox"]!.AsArray());
        Assert.NotEmpty(result["receipts"]!.AsArray());
        Assert.NotEmpty(result["decisions"]!.AsArray());

        var outbox = result["outbox"]!.AsArray().Single(item => item!["externalRequestId"]!.GetValue<string>() == chain.ExternalRequestId);
        Assert.Equal("CONFIRMED", outbox!["state"]!.GetValue<string>());
        Assert.Contains(result["inbox"]!.AsArray(), item => item!["messageId"]!.GetValue<string>() == chain.MessageId);

        var receipt = result["receipts"]!.AsArray().Single(item => item!["messageId"]!.GetValue<string>() == chain.MessageId);
        Assert.True(receipt!["signatureValid"]!.GetValue<bool>());
        Assert.Equal("COMPLETED", receipt["outcome"]!.GetValue<string>());
    }

    // The published vocabularies differ from the ones week 2 wrote into the
    // tables, and the contract's are what leaves the process.
    [Fact]
    public async Task StatesAndFormatsFollowThePublishedVocabulary()
    {
        var chain = await BuildProviderChainAsync();

        var result = (await TraceAsync(chain.OperationId.ToString())).Body["result"]!;

        Assert.Equal("1000.00", result["operation"]!["amount"]!.GetValue<string>());
        Assert.EndsWith("Z", result["operation"]!["createdAt"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("T", result["operation"]!["createdAt"]!.GetValue<string>(), StringComparison.Ordinal);

        string[] jobStates = ["READY", "LEASED", "RETRY_WAIT", "SUCCEEDED", "DEAD"];
        foreach (var job in result["jobs"]!.AsArray())
        {
            Assert.Contains(job!["state"]!.GetValue<string>(), jobStates);
        }

        Assert.Contains("SUCCEEDED", result["jobs"]!.AsArray().Select(job => job!["state"]!.GetValue<string>()));

        string[] attemptStates = ["RUNNING", "SUCCEEDED", "FAILED", "STALE"];
        foreach (var attempt in result["attempts"]!.AsArray())
        {
            Assert.Contains(attempt!["status"]!.GetValue<string>(), attemptStates);
        }
    }

    // A trace is handed to an operator, so it must not become the way a secret
    // leaves the system. The decision this chain carries is a MANUAL one with a
    // reason hash precisely so this test has something forbidden to look for.
    [Fact]
    public async Task TheResultCarriesNoSecretNoHashAndNoStoredText()
    {
        var chain = await BuildProviderChainAsync();

        var raw = (await TraceAsync(chain.OperationId.ToString())).Body["result"]!.ToJsonString();

        // The reason hash stands in for the reviewer's own words, and a hash is
        // still derived from them -- neither the value nor a field carrying one
        // may appear.
        Assert.DoesNotContain(chain.ReasonHash, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hash", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.ProviderHmacSecret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", raw, StringComparison.OrdinalIgnoreCase);
        // The provider callback's own free-text field, and the receipt body it
        // arrived in. signatureValid is a published boolean and stays.
        Assert.DoesNotContain("\"message\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("providerPaymentId", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("v1=", raw, StringComparison.Ordinal);
    }

    // What another client chose as its Idempotency-Key must not change what the
    // trace says. Seeding the operator's own input into the dispatch section made
    // it change, which is how the real asymmetry showed up: receipt-adapter uses
    // the message id as its key, so asking by message id returned a dispatch row
    // the other ten identifiers could not see.
    [Fact]
    public async Task AnUnrelatedCommandKeyedOnAChainIdentifierDoesNotChangeTheTrace()
    {
        var chain = await BuildProviderChainAsync();
        var byRoot = WithoutQuery((await TraceAsync(chain.OperationId.ToString())).Body["result"]!);

        // A command from somewhere else that happens to use this chain's external
        // request id as its Idempotency-Key. It belongs to no chain of its own.
        using var client = factory.CreateClient();
        var unrelated = await client.PostActionAsync(
            "/api/workflow/probe", WorkerToken(),
            new JsonObject { ["mode"] = "SUCCESS", ["value"] = $"unrelated-{Guid.NewGuid():N}" },
            idempotencyKey: chain.ExternalRequestId);
        Assert.Equal(200, unrelated.StatusCode);

        Assert.Equal(byRoot, WithoutQuery((await TraceAsync(chain.OperationId.ToString())).Body["result"]!));
        Assert.Equal(byRoot, WithoutQuery((await TraceAsync(chain.ExternalRequestId)).Body["result"]!));
    }

    [Fact]
    public async Task TwoIdenticalCallsReturnByteIdenticalResults()
    {
        var chain = await BuildProviderChainAsync();

        var first = await TraceAsync(chain.ProcessId.ToString());
        var second = await TraceAsync(chain.ProcessId.ToString());

        Assert.Equal(first.Body["result"]!.ToJsonString(), second.Body["result"]!.ToJsonString());
    }

    // The manual card records the decision a reviewer actually made; the
    // provider card never does. This is the half of the contract's "receipt or
    // audited decision" that the provider chain cannot show.
    [Fact]
    public async Task AManuallyDecidedChainCarriesTheAuditedDecisionAndNotItsReason()
    {
        var (operationId, processId, stepInstanceId) = await BuildManualChainAsync();

        var result = (await TraceAsync(operationId.ToString())).Body["result"]!;

        var decision = Assert.Single(
            result["decisions"]!.AsArray(), item => item!["stepInstanceId"]!.GetValue<string>() == stepInstanceId.ToString());
        Assert.Equal("MANUAL", decision!["source"]!.GetValue<string>());
        Assert.Equal("reviewer", decision["principal"]!.GetValue<string>());
        Assert.Equal("APPROVED", decision["outcome"]!.GetValue<string>());
        Assert.Null(decision["ruleVersion"]);
        Assert.Equal(processId.ToString(), result["process"]!["processId"]!.GetValue<string>());
        Assert.DoesNotContain("Documents verified", result.ToJsonString(), StringComparison.Ordinal);
    }

    // The contract's own escape hatch: a chain that never produced an operation
    // still resolves, by the one identifier it does have.
    [Fact]
    public async Task ADispatchThatNeverReachedAnOperationStillResolves()
    {
        using var client = factory.CreateClient();
        var failed = await client.PostActionAsync(
            "/api/operation/get", ReadToken(), new JsonObject { ["operationId"] = Guid.NewGuid().ToString() });
        Assert.Equal(404, failed.StatusCode);
        var correlationId = failed.Body["meta"]!["correlationId"]!.GetValue<string>();

        var result = (await TraceAsync(correlationId)).Body["result"]!;

        Assert.Equal(["correlationId"], result["query"]!["matchedBy"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Null(result["operation"]);
        Assert.Null(result["process"]);
        var dispatch = Assert.Single(result["dispatches"]!.AsArray());
        Assert.Equal("ERROR", dispatch!["status"]!.GetValue<string>());
        Assert.Equal("operation", dispatch["module"]!.GetValue<string>());
        Assert.Empty(result["steps"]!.AsArray());
        Assert.Empty(result["receipts"]!.AsArray());
    }

    [Fact]
    public async Task AnUnknownIdentifierIsNotFoundAndSaysSo()
    {
        var response = await TraceAsync($"unknown-{Guid.NewGuid():N}");

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("error", response.Body["status"]!.GetValue<string>());
        Assert.Equal("diagnostics.trace_not_found", response.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithoutAJwtTheActionIsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/diagnostics/trace")
        {
            Content = new StringContent("{\"identifier\":\"anything\"}", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheDiagnosticsPolicyTheActionIsForbidden()
    {
        using var client = factory.CreateClient();

        var response = await client.PostActionAsync(
            "/api/diagnostics/trace",
            ApiFactory.CreateToken("candidate-client", "web", "payment:read workflow:read"),
            new JsonObject { ["identifier"] = "anything" });

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("access.denied", response.Body["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"identifier\":\"a\",\"extra\":1}")]
    [InlineData("{\"identifier\":\"\"}")]
    [InlineData("{\"identifier\":123}")]
    public async Task APayloadOutsideTheSchemaIsRejected(string body)
    {
        using var client = factory.CreateClient();

        var response = await client.PostRawActionAsync("/api/diagnostics/trace", DiagnosticsToken(), body);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("payload.invalid", response.Body["code"]!.GetValue<string>());
    }

    private static readonly string[] TwelveSections =
    [
        "attempts", "decisions", "dispatches", "inbox", "jobs", "operation",
        "operationEvents", "outbox", "process", "query", "receipts", "steps",
    ];

    private static readonly string[] ContractOrder =
    [
        "correlationId", "requestId", "operationId", "processId", "stepInstanceId", "jobId",
        "executionId", "attemptId", "externalRequestId", "messageId", "decisionId",
    ];

    private static List<string> SortByContractOrder(IEnumerable<string> kinds) =>
        kinds.OrderBy(kind => Array.IndexOf(ContractOrder, kind)).ToList();

    private static string WithoutQuery(JsonNode result)
    {
        var copy = result.DeepClone().AsObject();
        copy.Remove("query");
        return copy.ToJsonString();
    }

    private async Task<ActionResponse> TraceAsync(string identifier)
    {
        using var client = factory.CreateClient();
        return await client.PostActionAsync(
            "/api/diagnostics/trace", DiagnosticsToken(), new JsonObject { ["identifier"] = identifier });
    }

    private sealed record Chain(
        Guid OperationId,
        Guid ProcessId,
        string RequestId,
        string SubmitCorrelationId,
        string SubmitIdempotencyKey,
        Guid StepInstanceId,
        Guid JobId,
        string ExecutionId,
        Guid AttemptId,
        string ExternalRequestId,
        string MessageId,
        Guid DecisionId,
        string ReasonHash)
    {
        public IEnumerable<(string Kind, string Identifier)> Identifiers()
        {
            yield return ("correlationId", SubmitCorrelationId);
            yield return ("requestId", SubmitIdempotencyKey);
            yield return ("operationId", OperationId.ToString());
            yield return ("processId", ProcessId.ToString());
            yield return ("stepInstanceId", StepInstanceId.ToString());
            yield return ("jobId", JobId.ToString());
            yield return ("executionId", ExecutionId);
            yield return ("attemptId", AttemptId.ToString());
            yield return ("externalRequestId", ExternalRequestId);
            yield return ("messageId", MessageId);
            yield return ("decisionId", DecisionId.ToString());
        }
    }

    // A real payment, driven the way the e2e smoke drives it: submit over HTTP,
    // let the worker run the card, deliver, accept the receipt, reconcile, finish.
    //
    // The one row inserted by hand is the decision. No published card both
    // delivers to a provider and records a decision, and this chain needs an
    // eleventh identifier; the row is real in shape (MANUAL carries a reason
    // hash and no rule version), which is also what gives
    // TheResultCarriesNoSecretNoHashAndNoStoredText something forbidden to find.
    private async Task<Chain> BuildProviderChainAsync()
    {
        var requestId = $"req-{Guid.NewGuid():N}";
        var operationId = await InsertOperationAsync(requestId, "1000.00", "PAYMENT_EXECUTION");

        using var client = factory.CreateClient();
        var submitKey = $"submit-{Guid.NewGuid():N}";
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: submitKey);
        Assert.Equal(200, submit.StatusCode);

        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());
        var submitCorrelationId = submit.Body["meta"]!["correlationId"]!.GetValue<string>();

        await DrainAsync(CreateLoop()); // validate_operation, prepare_external_request -> wait_receipt

        var externalRequestId = $"ext-{operationId}";
        var messageId = $"provider-{Guid.NewGuid():N}";
        var body = new JsonObject
        {
            ["externalRequestId"] = externalRequestId,
            ["messageId"] = messageId,
            ["occurredAt"] = "2026-09-27T12:00:00Z",
            ["outcome"] = "COMPLETED",
            ["providerPaymentId"] = messageId,
            ["version"] = 1,
        }.ToJsonString();
        // The Idempotency-Key is the message id, exactly as receipt-adapter sends
        // it (adapter.py uses callback.provider_payment_id). That is not a detail:
        // it makes the message id match as a requestId too, and an earlier version
        // of section_dispatches seeded the operator's input into the chain, so this
        // one identifier returned a dispatch row the other ten could not see.
        var accepted = await client.PostRawActionAsync(
            "/api/receipt/accept", ProviderToken(), body, messageId,
            ProviderSignature.Header(ApiFactory.ProviderHmacSecret, Encoding.UTF8.GetBytes(body)));
        Assert.Equal(200, accepted.StatusCode);

        Assert.True(await factory.Postgres.ScalarAsOwnerAsync<int>("select delivery.reconcile_inbox(50)") >= 1);
        await DrainAsync(CreateLoop()); // apply_receipt, complete_operation

        var (stepInstanceId, jobId, executionId, attemptId) = await ReadRuntimeIdentifiersAsync(processId);
        var reasonHash = new string('a', 64);
        var decisionId = await InsertManualDecisionAsync(processId, stepInstanceId, reasonHash);

        return new Chain(
            operationId, processId, requestId, submitCorrelationId, submitKey,
            stepInstanceId, jobId, executionId, attemptId, externalRequestId, messageId, decisionId, reasonHash);
    }

    private async Task<(Guid OperationId, Guid ProcessId, Guid StepInstanceId)> BuildManualChainAsync()
    {
        var operationId = await InsertOperationAsync($"req-{Guid.NewGuid():N}", "150000.00", "PAYMENT_APPROVAL");

        using var client = factory.CreateClient();
        var submit = await client.PostActionAsync(
            "/api/payment/submit", ClientToken(), new JsonObject { ["operationId"] = operationId.ToString() },
            idempotencyKey: $"submit-{Guid.NewGuid():N}");
        Assert.Equal(200, submit.StatusCode);
        var processId = Guid.Parse(submit.Body["result"]!["processId"]!.GetValue<string>());

        await DrainAsync(CreateLoop()); // validate, check_limit -> WAITING_MANUAL

        var stepInstanceId = await factory.Postgres.ScalarAsOwnerAsync<Guid>(
            "select step_instance_id from workflow.step_instance "
            + "where process_id = @processId and step_key = 'wait_manual_decision' and state = 'WAITING'",
            ("processId", processId));

        var decided = await client.PostActionAsync(
            "/api/workflow/manual", ReviewerToken(),
            new JsonObject
            {
                ["processId"] = processId.ToString(),
                ["stepInstanceId"] = stepInstanceId.ToString(),
                ["decision"] = "APPROVED",
                ["reason"] = "Documents verified",
            },
            idempotencyKey: $"manual-{Guid.NewGuid():N}");
        Assert.Equal(200, decided.StatusCode);

        await DrainAsync(CreateLoop()); // approve_operation

        return (operationId, processId, stepInstanceId);
    }

    private Task<(Guid StepInstanceId, Guid JobId, string ExecutionId, Guid AttemptId)> ReadRuntimeIdentifiersAsync(Guid processId) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                """
                select a.attempt_id, j.job_id, j.execution_id, j.step_instance_id
                  from workflow.workflow_attempt a
                  join workflow.workflow_job j on j.job_id = a.job_id
                 where j.process_id = @processId
                 order by a.started_at, a.attempt_id
                 limit 1
                """,
                connection);
            command.Parameters.AddWithValue("processId", processId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "the chain produced no attempt to identify it by");
            return (reader.GetGuid(3), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(0));
        });

    private Task<Guid> InsertManualDecisionAsync(Guid processId, Guid stepInstanceId, string reasonHash) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.decisions (process_id, step_instance_id, source, principal, reason_hash, outcome) "
                + "values (@processId, @stepInstanceId, 'MANUAL', 'reviewer', @reasonHash, 'APPROVED') returning decision_id",
                connection);
            command.Parameters.AddWithValue("processId", processId);
            command.Parameters.AddWithValue("stepInstanceId", stepInstanceId);
            command.Parameters.AddWithValue("reasonHash", reasonHash);
            return (Guid)(await command.ExecuteScalarAsync())!;
        });

    private Task<Guid> InsertOperationAsync(string requestId, string amount, string kind) =>
        factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(
                "insert into core.operations (request_id, operation_kind, amount, currency, status) "
                + "values (@requestId, @kind, @amount, 'RUB', 'CREATED') returning operation_id",
                connection);
            command.Parameters.AddWithValue("requestId", requestId);
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric)
            {
                Value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            });
            return (Guid)(await command.ExecuteScalarAsync())!;
        });

    private static string DiagnosticsToken() => ApiFactory.CreateToken("operator", "backoffice", "diagnostics:read");

    private static string ReadToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:read workflow:read");

    private static string ClientToken() => ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read workflow:read");

    private static string ReviewerToken() => ApiFactory.CreateToken("reviewer", "backoffice", "workflow:manual payment:read");

    private static string WorkerToken() =>
        ApiFactory.CreateToken("workflow-worker", "worker", "workflow:execute workflow:read payment:internal");

    private static string ProviderToken() => ApiFactory.CreateToken("receipt-provider", "receipt-adapter", "receipt:write");

    private WorkerLoop CreateLoop()
    {
        var validator = factory.Services.GetRequiredService<ISchemaValidator>();
        var actionExecutor = (PostgresActionExecutor)factory.Services.GetRequiredService<IActionExecutor>();

        return new WorkerLoop(
            $"owner-{Guid.NewGuid():N}", workerDataSource, factory.Postgres.JobRuntime, actionExecutor, validator, TimeProvider.System,
            new IntervalProfile(LeaseMs: 30_000, PollIntervalMs: 100), WorkflowWorkerPrincipal.DefaultScopes);
    }

    private static async Task DrainAsync(WorkerLoop loop, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var step = await loop.RunOnceAsync(CancellationToken.None);
            if (step.Outcome == WorkerStepOutcome.NoJob)
            {
                return;
            }

            Assert.True(step.Outcome == WorkerStepOutcome.Succeeded, $"outcome={step.Outcome} errorCode={step.ErrorCode}");
        }

        throw new TimeoutException("worker loop did not drain to an empty queue within the expected number of steps");
    }
}
