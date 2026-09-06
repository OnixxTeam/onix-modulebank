using Course.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Course.Tests.Integration;

// Ties every integration test class to the same ApiFactory instance -- one
// PostgreSQL container (PostgresFixture) and one Course.Api host for the whole
// run. Splitting this across several WebApplicationFactory instances would
// mean several JsonSchemaValidator singletons independently compiling the same
// $id-bearing contract schemas into Json.Schema.Net's process-wide registry,
// which throws on the second registration of any given $id.
[CollectionDefinition("integration")]
public sealed class IntegrationSuite : ICollectionFixture<ApiFactory>;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "course-integration-tests";
    private const string Audience = "course-integration-tests-audience";
    private const string SigningKey = "integration-test-signing-key-at-least-32-bytes";

    // Ticket 07: the generic signature boundary's HMAC key for this whole
    // test run. Exposed so SignatureBoundaryTests (and any receipt.accept
    // test from ticket 08 on) can sign a request body with the exact same
    // bytes ProviderSignatureVerifier was constructed with.
    public const string ProviderHmacSecret = "integration-test-provider-hmac-secret";

    public PostgresFixture Postgres { get; } = new();

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Postgres.InitializeAsync();

        // Program.cs reads these via Environment.GetEnvironmentVariable directly
        // (not IConfiguration), so they must be set before the host is built --
        // WebHostBuilder configuration overrides would never reach that code.
        Environment.SetEnvironmentVariable("COURSE_JWT_ISSUER", Issuer);
        Environment.SetEnvironmentVariable("COURSE_JWT_AUDIENCE", Audience);
        Environment.SetEnvironmentVariable("COURSE_JWT_SIGNING_KEY", SigningKey);
        Environment.SetEnvironmentVariable("COURSE_RUNTIME_DSN", Postgres.RuntimeConnectionString);
        Environment.SetEnvironmentVariable("PROVIDER_HMAC_SECRET", ProviderHmacSecret);

        // Forces the host to build now instead of lazily on some test's first
        // HttpClient request, so a startup failure surfaces here, not inside an
        // arbitrary test.
        _ = Server;
    }

    async Task IAsyncLifetime.DisposeAsync() => await Postgres.DisposeAsync();

    // Mints a token the way an external identity provider would, matching the
    // contract-reference personas (candidate-client/web/payment:write ..., etc.)
    // -- HS256, this factory's issuer/audience/key, sub+consumer+scope claims.
    public static string CreateToken(string subject, string consumer, string scope) =>
        JwtTestSigner.Sign(Issuer, Audience, SigningKey, $"""
            "sub": "{subject}", "consumer": "{consumer}", "scope": "{scope}"
            """);
}
