using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class WatcherSuppressionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-suppression-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SuppressedFingerprint_IsSuppressedUntilItExpires()
    {
        var store = new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance);
        var now = DateTime.UtcNow;
        store.Suppress(_root, "rep-abc123", "Known noise: planned maintenance window.", TimeSpan.FromDays(14), now);

        Assert.True(store.IsSuppressed(_root, "rep-abc123", now));
        Assert.True(store.IsSuppressed(_root, "rep-abc123", now.AddDays(13)));
        Assert.False(store.IsSuppressed(_root, "rep-abc123", now.AddDays(15)));
    }

    [Fact]
    public void ActiveList_ExcludesExpiredEntries()
    {
        var store = new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance);
        var now = DateTime.UtcNow;
        store.Suppress(_root, "rep-old", "stale", TimeSpan.FromDays(1), now.AddDays(-5));
        store.Suppress(_root, "rep-fresh", "recent", TimeSpan.FromDays(14), now);

        var active = store.Active(_root, now);
        var fp = Assert.Single(active);
        Assert.Equal("rep-fresh", fp.Fingerprint);
    }

    [Fact]
    public void SuppressedCase_DoesNotAdvanceTowardProposal()
    {
        var caseStore = new WatcherCaseStore(NullLogger<WatcherCaseStore>.Instance);
        var suppressions = new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance);
        var engine = new WatcherCaseEngine(caseStore, suppressions);
        var options = new WatcherOptions { PersistenceSweepsBeforeProposal = 1 };
        var now = DateTime.UtcNow;

        var observation = new WatcherSignalObservation
        {
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = "Fixture",
            FingerprintKey = "same-thing-again",
            Summary = "Recurs.",
        };
        var fingerprint = WatcherFingerprint.Compute(observation);
        suppressions.Suppress(_root, fingerprint, "operator rejected it", TimeSpan.FromDays(1), now);

        var result = engine.Ingest(_root, [observation], "sweep-1", options, now);
        Assert.Empty(result.ReadyForProposal);
        Assert.Equal(WatcherCaseStates.Suppressed, result.UpdatedCases.Single().State);
    }
}
