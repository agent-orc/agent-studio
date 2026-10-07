using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class CredentialRegistryTests
{
    [Fact]
    public async Task Deploy_key_registration_metadata_round_trips_without_secret_material()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        var record = Fixture("github_deploy_key") with {
            GitHubKeyId = 12345, ProvisioningCredentialId = "workstation-oauth-record",
            RepositoryPurpose = "workspace", RepositoryWriteGrant = true
        };
        await store.UpsertCredentialRegistryAsync(new(record, "instance-a", null,
            now.UtcDateTime), "test", default);

        var stored = Assert.Single(await store.ListCredentialRegistryAsync(default));
        Assert.Equal(12345, stored.GitHubKeyId);
        Assert.Equal("workstation-oauth-record", stored.ProvisioningCredentialId);
        Assert.Equal("workspace", stored.RepositoryPurpose);
        Assert.True(stored.RepositoryWriteGrant);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(stored));
    }

    [Fact]
    public async Task GitHub_relationship_cannot_be_attached_to_an_unrelated_credential_kind()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        var invalid = Fixture("claude_oauth_token") with { GitHubKeyId = 12345 };

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCredentialRegistryAsync(
            new(invalid, "instance-a", null, now.UtcDateTime), "test", default));
        Assert.Empty(await store.ListCredentialRegistryAsync(default));
    }

    [Fact]
    public async Task Every_catalogued_kind_round_trips_unknown_dates_and_bindings_without_values()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();

        foreach (var kind in CredentialRegistryProtocol.Kinds)
            await store.UpsertCredentialRegistryAsync(new CredentialRegistryObservationRequest(
                Fixture(kind), "host-instance-a", null, now.UtcDateTime), "test", default);

        var records = await store.ListCredentialRegistryAsync(default);
        Assert.Equal(10, records.Count);
        Assert.All(records, record =>
        {
            Assert.Null(record.CreatedAt);
            Assert.Null(record.ExpiresAt);
            Assert.Equal("owner", record.Owner);
            Assert.Single(record.Bindings);
            Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(record));
        });
    }

    [Fact]
    public async Task Old_generation_and_old_instance_cannot_replace_new_observation()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        await RegisterSourceAsync(store, "instance-a");
        var first = Fixture("claude_oauth_token");
        await store.UpsertCredentialRegistryAsync(new(first, "instance-a", null, now.UtcDateTime), "test", default);
        await RegisterSourceAsync(store, "instance-b");
        var second = first with { Generation = "generation-b", Supersedes = first.Generation };
        await store.UpsertCredentialRegistryAsync(new(second, "instance-b", first.Generation,
            now.AddMinutes(1).UtcDateTime), "test", default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(first, "instance-a", null, now.AddMinutes(2).UtcDateTime), "test", default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(second, "instance-a", second.Generation, now.AddMinutes(2).UtcDateTime), "test", default));
        await store.RegisterRunnerAsync("other-service", new RegisterRunnerRequest(
            "other-service", "host", "instance-a", "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.ReviewExecutor]), "test", default);
        var third = second with { Generation = "generation-c", Supersedes = second.Generation };
        var staleInstance = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(third, "instance-a", second.Generation, now.AddMinutes(2).UtcDateTime), "test", default));
        Assert.Equal("stale-credential-instance", staleInstance.Code);
        await RegisterSourceAsync(store, "instance-a");
        var reopened = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await reopened.InitializeAsync();
        var reRegisteredStaleInstance = await Assert.ThrowsAsync<TaskServerConflictException>(() => reopened.UpsertCredentialRegistryAsync(
            new(third, "instance-a", second.Generation, now.AddMinutes(2).UtcDateTime), "test", default));
        Assert.Equal("stale-credential-instance", reRegisteredStaleInstance.Code);
        Assert.Equal("generation-b", Assert.Single(await reopened.ListCredentialRegistryAsync(default)).Generation);
    }

    [Fact]
    public async Task New_generation_with_older_observation_cannot_replace_current_metadata()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        var first = Fixture("claude_oauth_token");
        await store.UpsertCredentialRegistryAsync(new(first, "instance-a", null, now.UtcDateTime), "test", default);
        var second = first with { Generation = "generation-b", Supersedes = first.Generation };
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(second, "instance-a", first.Generation, now.AddMinutes(-1).UtcDateTime), "test", default));
        Assert.Equal("stale-credential-observation", conflict.Code);
        Assert.Equal(first.Generation, Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
    }

    [Fact]
    public async Task Restarted_instance_refreshes_an_unchanged_generation_after_handoff()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        await RegisterSourceAsync(store, "instance-a");
        var record = Fixture("claude_native_login");
        await store.UpsertCredentialRegistryAsync(new(record, "instance-a", null, now.UtcDateTime), "test", default);

        // A daemon restart keeps the native store, so the generation is unchanged.
        await RegisterSourceAsync(store, "instance-b");
        await store.UpsertCredentialRegistryAsync(new(record, "instance-b", record.Generation,
            now.AddSeconds(30).UtcDateTime), "test", default);
        await store.UpsertCredentialRegistryAsync(new(record, "instance-b", record.Generation,
            now.AddSeconds(60).UtcDateTime), "test", default);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(record, "instance-a", record.Generation, now.AddSeconds(90).UtcDateTime), "test", default));
        Assert.Equal("stale-credential-instance", stale.Code);
        Assert.Equal(record.Generation, Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
    }

    [Fact]
    public async Task Unregistered_instance_cannot_take_over_a_credential_generation()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        var first = Fixture("claude_oauth_token");
        await store.UpsertCredentialRegistryAsync(new(first, "instance-a", null, now.UtcDateTime), "test", default);
        var second = first with { Generation = "generation-b", Supersedes = first.Generation };
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(second, "instance-b", first.Generation, now.AddMinutes(1).UtcDateTime), "test", default));
        Assert.Equal("stale-credential-instance", conflict.Code);
        Assert.Equal(first.Generation, Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
    }

    [Fact]
    public async Task Credential_bearing_locator_is_rejected_before_storage()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        var record = Fixture("github_https_token") with
        {
            Locator = new CredentialLocatorDto("credential-helper", "https://[redacted]@example.invalid/repo", "active"),
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCredentialRegistryAsync(
            new(record, "instance-a", null, now.UtcDateTime), "test", default));
        Assert.Empty(await store.ListCredentialRegistryAsync(default));
    }

    [Fact]
    public void Unknown_secret_field_is_rejected_by_the_registry_wire_contract()
    {
        var json = JsonSerializer.Serialize(Fixture("provider_api_key"));
        json = json.Insert(json.Length - 1, ",\"secretValue\":\"[redacted]\"");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CredentialRegistryRecordDto>(json));
    }

    private static CredentialRegistryRecordDto Fixture(string kind) => new(
        "installation", "host", kind, "provider", "owner", "recovery-owner",
        "generation-a", CredentialRegistryProtocol.RenewalMethods[kind], 1, kind, null, ["scope:host"],
        [new("coding", "execution", "source:provider-auth")],
        new(kind switch
        {
            "claude_native_login" or "codex_chatgpt_login" => "native-cli-store",
            "github_https_token" or "github_provisioning_oauth" => "credential-helper",
            "github_deploy_key" or "administration_ssh_key" => "ssh-private-key",
            "task_server_principal" => "service-secret",
            "wireguard_peer" => "network-key",
            _ => "environment-file",
        }, "host-local-reference", "active"), "unknown",
        new Dictionary<string, string> {
            ["createdAt"] = "not-observed", ["discoveredAt"] = "not-surveyed",
            ["lastVerifiedAt"] = "not-verified", ["lastRenewedAt"] = "not-observed",
            ["expiresAt"] = "issuer-unknown", ["rotationDueAt"] = "not-set",
            ["accessTokenExpiresAt"] = "not-observed", ["lastRealSuccessAt"] = "not-verified",
            ["nextProbeAt"] = "not-scheduled",
        },
        null, null, null, null, null, "not_verified", [],
        null, null, null, null, null, null, null, null, null);

    private static Task RegisterSourceAsync(TaskServerStore store, string instanceId) =>
        store.RegisterRunnerAsync("credential-source", new RegisterRunnerRequest(
            "credential-source", "host", instanceId, "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "test", default);
}
