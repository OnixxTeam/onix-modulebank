using System.Text.Json.Nodes;
using Course.Cli;
using Course.Cli.Commands;
using Course.Core.Actions;
using Course.Core.Cli;
using Course.Core.Validation;
using Course.Infrastructure.Validation;
using Course.Tests.Support;

namespace Course.Tests.Integration;

// Ticket 03 (GitHub issue #2) acceptance: BootstrapCommand no longer calls
// activate for any manifest -- publish alone (migrations 016/017) already
// makes a first install callable, and a repeat run must leave whatever an
// operator did afterwards (disable, activate a different version) untouched.
// Runs the real BootstrapCommand.ExecuteAsync against the shared test
// database, pointed at a disposable temp manifests directory per test so
// exercising catalog transitions here cannot disturb the built-in actions
// PostgresFixture's own setup published.
[Collection("integration")]
public sealed class BootstrapCommandTests(ApiFactory factory)
{
    // A schema that declares $id registers into Json.Schema.Net's process-wide
    // SchemaRegistry on first compile; a second JsonSchemaValidator instance
    // compiling the same action-manifest schema text would collide with that
    // registration (see JsonSchemaValidator's own comment). One instance
    // shared by every test in this class avoids that entirely.
    private static readonly ISchemaValidator ManifestValidator = new JsonSchemaValidator();

    [Fact]
    public async Task RepeatBootstrap_AfterDisableWithReplacement_LeavesTheDisabledVersionDisabled()
    {
        var action = $"bootstrap_fix_{Guid.NewGuid():N}";
        var manifestsDirectory = WriteManifests(
            TestManifests.Probe(action, version: 1, isDefault: true),
            TestManifests.Probe(action, version: 2, isDefault: false));

        var first = await RunBootstrapAsync(manifestsDirectory);
        Assert.Equal("ok", first.Status);

        var disableResult = await factory.Postgres.Publisher.DisableAsync(
            "testfixture", action, version: 1, replacementVersion: 2, CancellationToken.None);
        Assert.Equal("ok", disableResult["status"]!.GetValue<string>());

        // Same manifests directory, unchanged -- v1's own file still declares
        // enabled: true, is_default: true. Before this fix, bootstrap's
        // unconditional activate would have moved the default straight back.
        var second = await RunBootstrapAsync(manifestsDirectory);
        Assert.Equal("ok", second.Status);

        var v1 = await GetFlagsAsync(action, 1);
        Assert.False(v1.Enabled);
        Assert.False(v1.IsDefault);

        var v2 = await GetFlagsAsync(action, 2);
        Assert.True(v2.Enabled);
        Assert.True(v2.IsDefault);
    }

    [Fact]
    public async Task RepeatBootstrap_AfterActivatingAnotherVersion_LeavesTheDefaultOnThatVersion()
    {
        var action = $"bootstrap_fix_{Guid.NewGuid():N}";
        var manifestsDirectory = WriteManifests(TestManifests.Probe(action, version: 1, isDefault: true));

        var first = await RunBootstrapAsync(manifestsDirectory);
        Assert.Equal("ok", first.Status);

        var publishV2 = await factory.Postgres.Publisher.PublishAsync(
            ActionManifest.FromDocument(TestManifests.Probe(action, version: 2, isDefault: false)), CancellationToken.None);
        Assert.Equal("ok", publishV2["status"]!.GetValue<string>());

        var activateV2 = await factory.Postgres.Publisher.ActivateAsync("testfixture", action, 2, CancellationToken.None);
        Assert.Equal("ok", activateV2["status"]!.GetValue<string>());

        // The manifests directory only ever declared v1, with is_default: true
        // in its own file -- before this fix, repeating bootstrap would
        // unconditionally activate v1 and undo the operator's move to v2.
        var second = await RunBootstrapAsync(manifestsDirectory);
        Assert.Equal("ok", second.Status);

        var v1 = await GetFlagsAsync(action, 1);
        Assert.True(v1.Enabled);
        Assert.False(v1.IsDefault);

        var v2 = await GetFlagsAsync(action, 2);
        Assert.True(v2.Enabled);
        Assert.True(v2.IsDefault);
    }

    private Task<CliEnvelope> RunBootstrapAsync(string manifestsDirectory) =>
        BootstrapCommand.ExecuteAsync(
            factory.Postgres.MigrationRunner,
            factory.Postgres.Publisher,
            ManifestValidator,
            ManifestSchema.Load(),
            RepoPaths.Combine("db", "migrations"),
            manifestsDirectory,
            CancellationToken.None);

    private async Task<(bool Enabled, bool IsDefault)> GetFlagsAsync(string action, int version) =>
        await factory.Postgres.QueryAsOwnerAsync(async connection =>
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "select enabled, is_default from api.action_catalog where module = @module and action = @action and version = @version",
                connection);
            command.Parameters.AddWithValue("module", "testfixture");
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("version", version);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetBoolean(0), reader.GetBoolean(1));
        });

    // One temp directory per call, holding exactly the manifests passed in --
    // BootstrapCommand.ExecuteAsync reads every *.json file in this directory,
    // so no other test's fixtures can leak in and none of this test's leak out.
    private static string WriteManifests(params JsonNode[] manifests)
    {
        var directory = Directory.CreateTempSubdirectory("bootstrap-command-tests-").FullName;
        for (var i = 0; i < manifests.Length; i++)
        {
            File.WriteAllText(Path.Combine(directory, $"manifest-{i}.json"), manifests[i].ToJsonString());
        }

        return directory;
    }
}
