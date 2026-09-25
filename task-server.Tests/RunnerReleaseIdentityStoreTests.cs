using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RunnerReleaseIdentityStoreTests
{
    private static readonly RunnerReleaseIdentityDto Old = new(
        "agt-host-20260823T060000Z-bbbbbbb",
        "0.2.7",
        "bbbbbbb2222",
        new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc));

    private static readonly RunnerReleaseIdentityDto New = new(
        "agt-host-20260911T080000Z-aaaaaaa",
        "0.3.0",
        "aaaaaaa1111",
        new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Same_instance_registration_without_release_keeps_last_known_identity()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp.Path);
        await RegisterAsync(store, "instance-1", Old);

        await RegisterAsync(store, "instance-1", release: null);

        Assert.Equal(Old, Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Release);
    }

    [Fact]
    public async Task Replacement_instance_without_release_clears_previous_identity()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp.Path);
        await RegisterAsync(store, "instance-1", Old);

        await RegisterAsync(store, "instance-2", release: null);

        Assert.Null(Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Release);
    }

    [Fact]
    public async Task Replacement_instance_uses_the_release_it_reports()
    {
        using var temp = new TempDirectory();
        var store = await CreateStoreAsync(temp.Path);
        await RegisterAsync(store, "instance-1", Old);

        await RegisterAsync(store, "instance-2", New);

        Assert.Equal(New, Assert.Single(await store.ListRunnerCapabilitySnapshotsAsync(default)).Release);
    }

    private static async Task<TaskServerStore> CreateStoreAsync(string dataDirectory)
    {
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            TimeProvider.System);
        await store.InitializeAsync();
        return store;
    }

    private static Task RegisterAsync(
        TaskServerStore store,
        string instanceId,
        RunnerReleaseIdentityDto? release)
        => store.RegisterRunnerAsync(
            "runner-1",
            new RegisterRunnerRequest(
                "runner-1",
                "host-1",
                instanceId,
                release?.ReleaseId ?? "unknown",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor],
                Release: release),
            "test",
            default);
}
