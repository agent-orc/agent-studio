using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class CredentialRegistryTests
{
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
        var first = Fixture("claude_oauth_token");
        await store.UpsertCredentialRegistryAsync(new(first, "instance-a", null, now.UtcDateTime), "test", default);
        var second = first with { Generation = "generation-b", Supersedes = first.Generation };
        await store.UpsertCredentialRegistryAsync(new(second, "instance-b", first.Generation,
            now.AddMinutes(1).UtcDateTime), "test", default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(first, "instance-a", null, now.AddMinutes(2).UtcDateTime), "test", default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.UpsertCredentialRegistryAsync(
            new(second, "instance-a", second.Generation, now.AddMinutes(2).UtcDateTime), "test", default));
        Assert.Equal("generation-b", Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
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
}
