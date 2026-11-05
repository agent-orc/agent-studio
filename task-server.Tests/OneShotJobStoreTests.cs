using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class OneShotJobStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static OneShotJobRequest Request(string model = "gpt-6-sol")
        => new("codex", model, "Summarize this result.", "medium", 30, 100);

    [Fact]
    public async Task Waits_until_a_matching_fresh_accepted_host_exists()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        var waiting = await store.CreateOneShotJobAsync(Request(), default);
        Assert.Equal("waiting", waiting.Status);
        Assert.Equal("waiting for a host with model-acceptance:codex:gpt-6-sol", waiting.Error);

        var advertised = await RegisterAsync(store, clock);
        Assert.Contains(advertised.ConfiguredModelRoutes ?? [], route =>
            route.CliType == "codex" && route.Model == "gpt-6-sol");
        var queued = await store.CreateOneShotJobAsync(Request(), default);
        Assert.Equal("pending", queued.Status);
        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", null!), default));
        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", ""), default));
        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", "wrong-instance"), default));
        Assert.Equal("waiting", (await store.CreateOneShotJobAsync(Request("gpt-6-luna"), default)).Status);
        var claimed = await store.ClaimOneShotJobAsync(new("runner-a", "instance-a"), default);
        Assert.NotNull(claimed);
        Assert.Equal("gpt-6-sol", claimed.Request.Model);
        Assert.False(await store.CompleteOneShotJobAsync(
            new("runner-a", "instance-a", claimed.JobId, "wrong-token", true, "done", null, null), default));
        Assert.True(await store.CompleteOneShotJobAsync(
            new("runner-a", "instance-a", claimed.JobId, claimed.ClaimToken, true, "done", null,
                new(1, 2, 0, 0, "gpt-6-sol")), default));
        var finished = await store.GetOneShotJobAsync(queued.JobId, default);
        Assert.Equal("completed", finished?.Status);
        Assert.Equal("done", finished?.Text);
        Assert.Equal(2, finished?.Usage?.OutputTokens);
    }

    [Fact]
    public async Task Old_instance_cannot_claim_or_complete_after_runner_restarts()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await RegisterAsync(store, clock);
        var first = await store.CreateOneShotJobAsync(Request(), default);
        var oldClaim = (await store.ClaimOneShotJobAsync(new("runner-a", "instance-a"), default))!;
        var second = await store.CreateOneShotJobAsync(Request(), default);

        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest("runner", "host-a",
            "instance-b", "1.0.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "test", default);
        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", "instance-a"), default));
        Assert.False(await store.CompleteOneShotJobAsync(new("runner-a", "instance-a",
            oldClaim.JobId, oldClaim.ClaimToken, true, "stale", null, null), default));
        Assert.False(await store.CompleteOneShotJobAsync(new("runner-a", "instance-b",
            oldClaim.JobId, oldClaim.ClaimToken, true, "stolen", null, null), default));
        Assert.Equal("claimed", (await store.GetOneShotJobAsync(first.JobId, default))?.Status);

        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", "instance-b"), default));
        await AdvertiseAsync(store, clock, "instance-b");
        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", "instance-a"), default));
        var freshClaim = await store.ClaimOneShotJobAsync(new("runner-a", "instance-b"), default);
        Assert.NotNull(freshClaim);
        Assert.Equal(second.JobId, freshClaim.JobId);
        Assert.True(await store.CompleteOneShotJobAsync(new("runner-a", "instance-b",
            freshClaim.JobId, freshClaim.ClaimToken, true, "fresh", null, null), default));
    }

    [Fact]
    public async Task Rejects_oversize_output_and_expires_unfinished_jobs()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = await StoreAsync(temp.Path, clock);
        await RegisterAsync(store, clock);
        var queued = await store.CreateOneShotJobAsync(Request(), default);
        var claimed = (await store.ClaimOneShotJobAsync(new("runner-a", "instance-a"), default))!;
        Assert.True(await store.CompleteOneShotJobAsync(new("runner-a", "instance-a", claimed.JobId,
            claimed.ClaimToken, true, new string('x', 101), null, null), default));
        var failed = await store.GetOneShotJobAsync(queued.JobId, default);
        Assert.Equal("failed", failed?.Status);
        Assert.Contains("byte cap", failed?.Error);
        Assert.Null(failed?.Text);

        var second = await store.CreateOneShotJobAsync(Request(), default);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("failed", (await store.GetOneShotJobAsync(second.JobId, default))?.Status);
        Assert.Null(await store.ClaimOneShotJobAsync(new("runner-a", "instance-a"), default));
        clock.Advance(TimeSpan.FromSeconds(241));
        Assert.Equal("waiting", (await store.CreateOneShotJobAsync(Request(), default)).Status);
    }

    [Fact]
    public async Task Refuses_unbounded_request_before_a_job_is_created()
    {
        using var temp = new TempDirectory();
        var store = await StoreAsync(temp.Path, new ManualTimeProvider(Start));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateOneShotJobAsync(
            Request() with { Prompt = new string('x', 220_001) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateOneShotJobAsync(
            Request() with { TimeoutSeconds = 301 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateOneShotJobAsync(
            Request() with { Images = [new OneShotJobImage("AA==", "application/octet-stream")] }, default));
    }

    private static async Task<TaskServerStore> StoreAsync(string path, ManualTimeProvider clock)
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = path }), clock);
        await store.InitializeAsync();
        return store;
    }

    private static async Task<RunnerCapabilitySnapshotDto> RegisterAsync(TaskServerStore store, ManualTimeProvider clock)
    {
        await store.RegisterRunnerAsync("runner-a", new RegisterRunnerRequest("runner", "host-a",
            "instance-a", "1.0.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "test", default);
        return await AdvertiseAsync(store, clock, "instance-a");
    }

    private static async Task<RunnerCapabilitySnapshotDto> AdvertiseAsync(
        TaskServerStore store, ManualTimeProvider clock, string instanceId)
    {
        return await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            "runner-a", instanceId, CapabilityProtocol.CurrentSchemaVersion,
            clock.GetUtcNow().UtcDateTime, 300, 1,
            [new AdvertisedCapabilityDto(CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol"),
                "model-acceptance", "ready", CredentialGeneration: "login-1",
                CredentialObservedAt: clock.GetUtcNow().UtcDateTime, ProbeSequence: 1)]), "test", default);
    }
}
