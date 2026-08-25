using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

// Synthetic manifests for the test-only target functions in Fixtures/testfixture.sql.
// None of these are stored under db/manifests -- they exist purely to drive
// PostgresActionExecutor against controlled rollback/publish scenarios that the
// real domain actions have no reason to produce. Schemas below deliberately omit
// $id: Json.Schema.Net registers any schema that declares one into a process-wide
// registry and throws on a second registration of the same $id.
internal static class TestManifests
{
    private const string GenericObjectSchema = """
        { "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object" }
        """;

    // Target writes a canary row and then reports a controlled business failure;
    // idempotency is irrelevant to what this manifest is testing.
    public static JsonNode ErrorAfterInsert() => JsonNode.Parse($$"""
        {
          "contract_version": "course-1",
          "module": "testfixture",
          "action": "error",
          "version": 1,
          "http_method": "POST",
          "target_schema": "testfixture",
          "target_function": "error_after_insert_v1",
          "request_schema": {{GenericObjectSchema}},
          "response_schema": {{GenericObjectSchema}},
          "outcomes": ["UNUSED"],
          "required_policy": [],
          "idempotency_mode": "none",
          "idempotency_scope": "none",
          "timeout_ms": 5000,
          "enabled": true,
          "is_default": true
        }
        """)!;

    // idempotency_mode "required" so a replay with the same Idempotency-Key can
    // prove the claimed key never survived the rollback: it comes back through
    // the same 500 path instead of a stored envelope or a 409 conflict.
    public static JsonNode UnknownOutcome() => JsonNode.Parse($$"""
        {
          "contract_version": "course-1",
          "module": "testfixture",
          "action": "unknown_outcome",
          "version": 1,
          "http_method": "POST",
          "target_schema": "testfixture",
          "target_function": "unknown_outcome_v1",
          "request_schema": {{GenericObjectSchema}},
          "response_schema": {{GenericObjectSchema}},
          "outcomes": ["DECLARED_ONLY"],
          "required_policy": [],
          "idempotency_mode": "required",
          "idempotency_scope": "principal_action",
          "timeout_ms": 5000,
          "enabled": true,
          "is_default": true
        }
        """)!;

    // Published mid-test by CatalogTests itself (not by PostgresFixture's setup)
    // to prove a brand-new action is callable without rebuilding Course.Api.
    public static JsonNode Echo() => JsonNode.Parse($$"""
        {
          "contract_version": "course-1",
          "module": "testfixture",
          "action": "echo",
          "version": 1,
          "http_method": "POST",
          "target_schema": "testfixture",
          "target_function": "echo_v1",
          "request_schema": {{GenericObjectSchema}},
          "response_schema": {{GenericObjectSchema}},
          "outcomes": ["ECHOED"],
          "required_policy": [],
          "idempotency_mode": "none",
          "idempotency_scope": "none",
          "timeout_ms": 5000,
          "enabled": true,
          "is_default": true
        }
        """)!;

    // A disposable route per test, so exercising publish/activate/disable
    // transitions cannot disturb the default version any other test calls
    // through. Points at the same echo_v1 target: what is under test is the
    // catalog transition, not anything the target does.
    public static JsonNode Probe(string action, int version, bool isDefault) => JsonNode.Parse($$"""
        {
          "contract_version": "course-1",
          "module": "testfixture",
          "action": "{{action}}",
          "version": {{version}},
          "http_method": "POST",
          "target_schema": "testfixture",
          "target_function": "echo_v1",
          "request_schema": {{GenericObjectSchema}},
          "response_schema": {{GenericObjectSchema}},
          "outcomes": ["ECHOED"],
          "required_policy": [],
          "idempotency_mode": "none",
          "idempotency_scope": "none",
          "timeout_ms": 5000,
          "enabled": true,
          "is_default": {{(isDefault ? "true" : "false")}}
        }
        """)!;
}
