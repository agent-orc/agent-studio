using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using System.Text;
using Xunit;

namespace TaskServer.Tests;

public sealed class PrincipalRotationStoreTests
{
    [Fact]
    public async Task Shared_principal_replays_undelivered_consumer_after_restart_without_reissuing()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        await store.CreatePrincipalAsync(new("studio-edge", TaskServerPrincipalKinds.Studio,
            [TaskServerScopes.Management]), "setup", default);
        var request = new RotatePrincipalRequest(60, "shared-delivery-restart",
            [new("edge-a", TaskServerScopes.Management),
             new("edge-b", TaskServerScopes.Management)], "edge-a");
        var first = await store.RotatePrincipalAsync("studio-edge", request, "manager", default);
        var second = await store.RotatePrincipalAsync("studio-edge",
            request with { DeliveryConsumerId = "edge-b" }, "manager", default);
        var firstActor = await store.AuthenticatePrincipalAsync(first.Credential!, default);
        await store.MarkPrincipalRotationDeliveredAsync(request.OperationId!, firstActor!, default);

        var reopened = new TaskServerStore(options, clock);
        await reopened.InitializeAsync();
        var firstRetry = await reopened.RotatePrincipalAsync("studio-edge", request, "manager", default);
        var secondRetry = await reopened.RotatePrincipalAsync("studio-edge",
            request with { DeliveryConsumerId = "edge-b" }, "manager", default);
        Assert.Null(firstRetry.Credential);
        Assert.Equal(second.Credential, secondRetry.Credential);
        Assert.Equal(first.Rotation!.CredentialGeneration, secondRetry.Rotation!.CredentialGeneration);
        Assert.Equal(2, secondRetry.Rotation.ConsumerCredentialGenerations!.Count);
        var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "task-server.db")));
        Assert.DoesNotContain(first.Credential!, bytes, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Credential!, bytes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lost_issue_response_replays_only_until_delivery_from_private_host_key()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        await store.CreatePrincipalAsync(new("engine", TaskServerPrincipalKinds.Engine,
            [TaskServerScopes.TasksRead]), "setup", default);
        var request = new RotatePrincipalRequest(60, "issue-lost",
            [new PrincipalRotationConsumer("engine", TaskServerScopes.TasksRead)]);
        var first = await store.RotatePrincipalAsync("engine", request, "studio", default);
        var reopened = new TaskServerStore(options, clock);
        await reopened.InitializeAsync();
        var replay = await reopened.RotatePrincipalAsync("engine", request, "studio", default);
        Assert.Equal(first.Credential, replay.Credential);
        Assert.Equal(first.Rotation!.CredentialGeneration, replay.Rotation!.CredentialGeneration);
        var keyPath = Path.Combine(temp.Path, "principal-rotation-delivery.key");
        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
        Assert.DoesNotContain(first.Credential!, Encoding.UTF8.GetString(
            await File.ReadAllBytesAsync(Path.Combine(temp.Path, "task-server.db"))), StringComparison.Ordinal);
        var actor = await reopened.AuthenticatePrincipalAsync(first.Credential!, default);
        await reopened.MarkPrincipalRotationDeliveredAsync(request.OperationId!, actor!, default);
        Assert.Null((await reopened.RotatePrincipalAsync("engine", request, "studio", default)).Credential);
    }

    [Fact]
    public async Task Expired_ambiguous_delivery_requires_separate_recovery_identity_and_never_reissues()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, clock);
        await store.InitializeAsync();
        var original = await store.CreatePrincipalAsync(
            new("engine-a", TaskServerPrincipalKinds.Engine,
                [TaskServerScopes.TasksRead]), "setup", default);
        var recovery = await store.CreatePrincipalAsync(
            new("recovery:operator", TaskServerPrincipalKinds.Studio,
                [TaskServerScopes.Management]), "setup", default);
        Assert.Equal([TaskServerScopes.Management], recovery.Principal.Scopes);
        var request = new RotatePrincipalRequest(60, "engine-a-operation-1",
            [new PrincipalRotationConsumer("engine-a", TaskServerScopes.TasksRead)]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RotatePrincipalAsync(
            "engine-a", request with { OverlapSeconds = 0 }, "studio-a", default));
        var first = await store.RotatePrincipalAsync("engine-a", request, "studio-a", default);
        Assert.NotNull(first.Credential);

        var reopened = new TaskServerStore(options, clock);
        await reopened.InitializeAsync();
        var retry = await reopened.RotatePrincipalAsync("engine-a", request, "studio-a", default);
        Assert.Equal(first.Credential, retry.Credential);
        Assert.Equal(first.Rotation!.CredentialGeneration, retry.Rotation!.CredentialGeneration);
        var actor = await reopened.AuthenticatePrincipalAsync(first.Credential!, default);
        await reopened.MarkPrincipalRotationDeliveredAsync(request.OperationId!, actor!, default);
        var afterDelivery = await reopened.RotatePrincipalAsync("engine-a", request, "studio-a", default);
        Assert.Null(afterDelivery.Credential);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("recovery-required",
            (await reopened.GetPrincipalRotationAsync("engine-a", request.OperationId!, default))!.State);
        Assert.Null(await reopened.AuthenticatePrincipalAsync(original.Credential!, default));
        await Assert.ThrowsAsync<TaskServerConflictException>(() => reopened.RotatePrincipalAsync(
            "engine-a", request with { OperationId = "engine-a-operation-2" }, "studio-a", default));
        var recovered = await reopened.RotatePrincipalAsync("engine-a",
            request with { OperationId = "engine-a-operation-2" }, "recovery:operator", default);
        Assert.NotNull(recovered.Credential);
        Assert.Equal("superseded-in-recovery",
            (await reopened.GetPrincipalRotationAsync("engine-a", request.OperationId!, default))!.State);
    }
}
