using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProviderRenewalTests
{
    [Fact]
    public async Task Duplicate_and_timeout_preserve_generation_and_block_new_issuance()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await SeedAsync(store, now.UtcDateTime);
        var request = new BeginProviderRenewalRequest("installation", "host", "credential",
            "generation-a", "R1", "operation-one", now.AddMinutes(15).UtcDateTime);

        var first = await store.BeginProviderRenewalAsync(request, "operator", default);
        Assert.Equal(first.OperationId,
            (await store.BeginProviderRenewalAsync(request with { Deadline = request.Deadline.AddSeconds(1) },
                "operator", default)).OperationId);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.BeginProviderRenewalAsync(
            request with { IdempotencyKey = "operation-two" }, "operator", default));
        await store.AdvanceProviderRenewalAsync(first.OperationId,
            new("preflight", null, null, [], false, []), "operator", default);
        await store.AdvanceProviderRenewalAsync(first.OperationId,
            new("awaiting-human", null, null, [], false, []), "operator", default);
        var cancelled = await store.AdvanceProviderRenewalAsync(first.OperationId,
            new("recovery-required", null, null, [], false, []), "operator", default);
        Assert.Equal("recovery-required", cancelled.Step);
        Assert.Equal("generation-a", Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);

        var reopened = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await reopened.InitializeAsync();
        Assert.Equal("recovery-required", (await reopened.GetProviderRenewalAsync(first.OperationId, default))?.Step);
    }

    [Fact]
    public async Task Real_request_and_both_units_are_required_before_completion()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(now));
        await store.InitializeAsync();
        await SeedAsync(store, now.UtcDateTime);
        var receipt = await store.BeginProviderRenewalAsync(new("installation", "host", "credential",
            "generation-a", "R3", "operation-one", now.AddMinutes(15).UtcDateTime), "operator", default);
        foreach (var step in new[] { "preflight", "awaiting-human", "staged", "installed" })
            receipt = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
                new(step, null, null, [], false, []), "operator", default);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("verified", "generation-b", "native-cli-store", ["agent-runner.service"], false, []),
            "operator", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("verified", string.Concat("sk", "-ant-", "fixture"), "native-cli-store",
                ["agent-runner.service", "agent-runner-review.service"], true, []),
            "operator", default));
        receipt = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("verified", "generation-b", "native-cli-store",
                ["agent-runner.service", "agent-runner-review.service"], true, []), "operator", default);
        Assert.Equal("verified", receipt.Step);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(receipt));
    }

    [Fact]
    public async Task Expired_installed_operation_requires_explicit_recovery_proof()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await SeedAsync(store, now.UtcDateTime);
        var request = new BeginProviderRenewalRequest("installation", "host", "credential",
            "generation-a", "R1", "operation-one", now.AddMinutes(15).UtcDateTime);
        var receipt = await store.BeginProviderRenewalAsync(request, "operator", default);
        foreach (var step in new[] { "preflight", "awaiting-human", "staged", "installed" })
            await store.AdvanceProviderRenewalAsync(receipt.OperationId,
                new(step, null, null, [], false, []), "operator", default);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("recovery-required",
            (await store.GetProviderRenewalAsync(receipt.OperationId, default))?.Step);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.BeginProviderRenewalAsync(
            request with { IdempotencyKey = "operation-two", Deadline = now.AddMinutes(30).UtcDateTime },
            "operator", default));
        var recovered = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("cancelled", "generation-a", "environment-file",
                ["agent-runner.service", "agent-runner-review.service"], true, []),
            "operator", default);
        Assert.Equal("cancelled", recovered.Step);
        Assert.Equal("generation-a", Assert.Single(await store.ListCredentialRegistryAsync(default)).Generation);
    }

    [Fact]
    public async Task Expired_installed_operation_can_complete_after_new_generation_proof()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await SeedAsync(store, now.UtcDateTime);
        var receipt = await store.BeginProviderRenewalAsync(new("installation", "host", "credential",
            "generation-a", "R1", "operation-expired", now.AddMinutes(15).UtcDateTime), "operator", default);
        foreach (var step in new[] { "preflight", "awaiting-human", "staged", "installed" })
            receipt = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
                new(step, null, null, [], false, []), "operator", default);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("recovery-required", (await store.GetProviderRenewalAsync(receipt.OperationId, default))?.Step);
        receipt = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("verified", "generation-b", "environment-file",
                ["agent-runner.service", "agent-runner-review.service"], true, []), "operator", default);
        receipt = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("retired", null, null, [], false, []), "operator", default);
        receipt = await store.AdvanceProviderRenewalAsync(receipt.OperationId,
            new("complete", null, null, [], false, []), "operator", default);
        Assert.Equal("complete", receipt.Step);
    }

    [Fact]
    public async Task Completion_advances_registry_generation_and_fences_the_next_renewal()
    {
        using var temp = new TempDirectory();
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await SeedAsync(store, now.UtcDateTime);
        var first = await store.BeginProviderRenewalAsync(new("installation", "host", "credential",
            "generation-a", "R1", "operation-one", now.AddMinutes(15).UtcDateTime), "operator", default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.BeginProviderRenewalAsync(
            new("installation", "host", "credential", "generation-a", "R1", "operation-two",
                now.AddMinutes(15).UtcDateTime), "operator", default));
        foreach (var step in new[] { "preflight", "awaiting-human", "staged", "installed" })
            await store.AdvanceProviderRenewalAsync(first.OperationId,
                new(step, null, null, [], false, []), "operator", default);
        await store.AdvanceProviderRenewalAsync(first.OperationId,
            new("verified", "generation-b", "environment-file",
                ["agent-runner.service", "agent-runner-review.service"], true, []), "operator", default);
        await store.AdvanceProviderRenewalAsync(first.OperationId,
            new("retired", null, null, [], false, []), "operator", default);
        await store.AdvanceProviderRenewalAsync(first.OperationId,
            new("complete", null, null, [], false, []), "operator", default);

        var registry = Assert.Single(await store.ListCredentialRegistryAsync(default));
        Assert.Equal("generation-b", registry.Generation);
        Assert.Equal("generation-a", registry.Supersedes);
        var next = await store.BeginProviderRenewalAsync(new("installation", "host", "credential",
            "generation-b", "R1", "operation-two", now.AddMinutes(15).UtcDateTime), "operator", default);
        Assert.Equal("generation-b", next.ExpectedGeneration);
    }

    private static Task SeedAsync(TaskServerStore store, DateTime observedAt)
    {
        var unknown = new Dictionary<string, string>();
        foreach (var field in new[] { "createdAt", "discoveredAt", "lastVerifiedAt", "lastRenewedAt",
                     "expiresAt", "rotationDueAt", "accessTokenExpiresAt", "lastRealSuccessAt", "nextProbeAt" })
            unknown[field] = "not-observed";
        var record = new CredentialRegistryRecordDto("installation", "host", "credential", "claude",
            "owner", "recovery-owner", "generation-a", "R1", 1, "claude_oauth_token", null,
            ["scope:host"], [new("coding", "execution", "source:provider-auth")],
            new("environment-file", "host-local-reference", "active"), "unknown", unknown,
            null, null, null, null, null, "not_verified", [],
            null, null, null, null, null, null, null, null, null);
        return store.UpsertCredentialRegistryAsync(new(record, "instance-a", null, observedAt), "test", default);
    }
}
