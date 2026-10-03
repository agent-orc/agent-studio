using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class FailureFingerprintStoreTests
{
    [Fact]
    public async Task Events_are_append_only_idempotent_and_queryable_across_cards()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            TimeProvider.System);
        await store.InitializeAsync();

        var first = new RecordFailureFingerprintRequest("test:guard", "AGT-1", "host-a", "review", "report-1");
        await store.RecordFailureFingerprintAsync(first);
        await store.RecordFailureFingerprintAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordFailureFingerprintAsync(first with { Fingerprint = "test:other" }));
        await store.RecordFailureFingerprintAsync(first with
        {
            CardKey = "AGT-2", Executor = "host-b", ReportKey = "report-2",
        });

        var history = Assert.Single(await store.ReadFailureFingerprintsAsync("test:guard"));
        Assert.Equal(2, history.Count);
        Assert.Equal(["AGT-1", "AGT-2"], history.CardKeys);
        Assert.Equal(["host-a", "host-b"], history.Executors);
        Assert.True(history.LastSeen >= history.FirstSeen);

        var reopened = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            TimeProvider.System);
        await reopened.InitializeAsync();
        Assert.Equal(2, Assert.Single(await reopened.ReadFailureFingerprintsAsync("test:guard")).Count);
    }

    [Fact]
    public async Task History_window_excludes_old_events_without_rewriting_them()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        await store.RecordFailureFingerprintAsync(new(
            "test:guard", "AGT-old", "host-a", "gate", "old-report"));
        clock.Now = clock.Now.AddDays(2);
        await store.RecordFailureFingerprintAsync(new(
            "test:guard", "AGT-current", "host-b", "review", "current-report"));

        var recent = Assert.Single(await store.ReadFailureFingerprintsAsync(
            "test:guard", clock.Now.AddHours(-24).UtcDateTime));
        Assert.Equal(1, recent.Count);
        Assert.Equal(["AGT-current"], recent.CardKeys);
        Assert.Equal(2, Assert.Single(await store.ReadFailureFingerprintsAsync("test:guard")).Count);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
