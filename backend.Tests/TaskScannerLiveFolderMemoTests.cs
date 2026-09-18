using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the AGT-2701 live-folder memo: the per-scan cost of a live task
/// folder (task.json read+parse plus the two log tail scans) is gated behind a
/// cheap folder fingerprint, so an unchanged folder reuses its previous
/// <see cref="TaskInfo"/>. The invariants that keep the memo honest:
/// <list type="bullet">
///   <item>a fingerprinted input change (task.json edit, a new
///   session-events.jsonl line) forces a re-parse on the next scan;</item>
///   <item>marker files are deliberately NOT fingerprinted, so creating,
///   removing, or rewriting them in place is still reflected on the next scan
///   even when nothing else changed - proving they are re-read on a hit;</item>
///   <item>on a genuine fingerprint hit the gated parse is reused verbatim (an
///   out-of-band task.json edit that preserves length+mtime is intentionally
///   NOT seen) while the marker fields are refreshed.</item>
/// </list>
/// </summary>
public class TaskScannerLiveFolderMemoTests : IDisposable
{
    private readonly string _watchPath;

    public TaskScannerLiveFolderMemoTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-live-memo-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskScannerService BuildScanner(TimeSpan? timestampGranularity = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            summary,
            folderMemoTimestampGranularity: timestampGranularity);
    }

    private string SeedJob(string slug, string state, string taskJson)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"), taskJson);
        return dir;
    }

    // ownerClientId is stamped so the scanner's legacy-migration write-back does
    // not mutate task.json mid-scan and perturb the folder fingerprint.
    private static string HeaderJson(string slug, string state, string title) =>
        $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1," +
        $"\"agent\":\"claude\",\"ownerClientId\":\"test-client\"}}";

    // Same scanner instance across both scans so the memo populated by the first
    // scan is consulted by the second - a fresh scanner would start memo-empty.
    private static TaskInfo? Scan(TaskScannerService scanner, string slug)
        => scanner.ScanAllJobsRaw().FirstOrDefault(t => t.Id == slug);

    [Fact]
    public void EditingTaskJson_IsReflectedOnNextScan()
    {
        // Acceptance (a): the fingerprint carries task.json length+mtime, so an
        // edit invalidates the memo entry and the next scan re-parses the header.
        var scanner = BuildScanner();
        var dir = SeedJob("edit-me", TaskStates.Progress,
            HeaderJson("edit-me", TaskStates.Progress, "Original title"));

        Assert.Equal("Original title", Scan(scanner, "edit-me")!.Title);

        File.WriteAllText(Path.Combine(dir, "task.json"),
            HeaderJson("edit-me", TaskStates.Progress, "A brand new and longer title"));

        Assert.Equal("A brand new and longer title", Scan(scanner, "edit-me")!.Title);
    }

    [Fact]
    public void AppendingSessionEvents_ChangesCodeActivity_OnNextScan()
    {
        // Acceptance (b): session-events.jsonl length+mtime is fingerprinted, so a
        // newly appended HEAD-moving line flips DetectCodeActivity next scan.
        var scanner = BuildScanner();
        var dir = SeedJob("session-grow", TaskStates.Progress,
            HeaderJson("session-grow", TaskStates.Progress, "t"));

        Assert.False(Scan(scanner, "session-grow")!.CodeActivityDetected);

        File.WriteAllText(Path.Combine(dir, "logs", "session-events.jsonl"),
            "{\"headShaBefore\":\"aaaaaaa\",\"headShaAfter\":\"bbbbbbb\"}\n");

        Assert.True(Scan(scanner, "session-grow")!.CodeActivityDetected);
    }

    [Fact]
    public void QuotaWaitMarker_CreateAndRemove_ReflectedOnNextScan()
    {
        // Acceptance (c): the quota-wait marker is not fingerprinted, so its
        // creation and removal are reflected on the next scan.
        var scanner = BuildScanner();
        var dir = SeedJob("quota-toggle", TaskStates.Progress,
            HeaderJson("quota-toggle", TaskStates.Progress, "t"));

        Assert.Null(Scan(scanner, "quota-toggle")!.QuotaWait);

        QuotaWaitMarker.Write(dir, new QuotaWaitRecord { CliType = "claude", Reason = "reset" });
        Assert.NotNull(Scan(scanner, "quota-toggle")!.QuotaWait);

        QuotaWaitMarker.Clear(dir);
        Assert.Null(Scan(scanner, "quota-toggle")!.QuotaWait);
    }

    [Fact]
    public void FingerprintHit_ReusesParse_ButReAppliesMarkers()
    {
        // The core of the memo: with the fingerprint held stable, a gated
        // task.json edit (equal length, mtime restored) is NOT seen - proving the
        // parse is reused - while an in-place marker rewrite IS seen, proving
        // ApplyVolatileMarkers re-reads markers on the fast path.
        var scanner = BuildScanner();
        var dir = SeedJob("memo-hit", TaskStates.Progress,
            HeaderJson("memo-hit", TaskStates.Progress, "AAAAAAAAAA"));
        var taskJsonPath = Path.Combine(dir, "task.json");
        var quotaPath = Path.Combine(dir, "quota-wait.json");

        // A marker already present before the first scan, written in place so its
        // later in-place rewrite leaves the fingerprinted stats untouched.
        File.WriteAllText(quotaPath,
            "{\"version\":1,\"cliType\":\"claude\",\"resetAt\":\"2029-01-01T00:00:00Z\",\"reason\":\"r1\"}");

        // Pin the two fingerprinted stats that an in-place write can move - the
        // task.json mtime and the folder mtime - to fixed instants immediately
        // before each scan. Setting identical values each round sidesteps
        // filesystem-granularity rounding (and this container's overlay copy-up,
        // which bumps the folder mtime on any write), so the fingerprint matches
        // exactly and the second scan is a genuine fast-path hit.
        PinStats(dir, taskJsonPath);
        var first = Scan(scanner, "memo-hit");
        Assert.Equal("AAAAAAAAAA", first!.Title);
        Assert.Equal("r1", first.QuotaWait!.Reason);

        // Gated edit (equal length) plus an in-place marker rewrite; both leave the
        // pinned stats unchanged once re-pinned, so the fast path is taken.
        File.WriteAllText(taskJsonPath,
            HeaderJson("memo-hit", TaskStates.Progress, "BBBBBBBBBB"));
        File.WriteAllText(quotaPath,
            "{\"version\":1,\"cliType\":\"claude\",\"resetAt\":\"2030-02-02T00:00:00Z\",\"reason\":\"r2\"}");
        PinStats(dir, taskJsonPath);

        var second = Scan(scanner, "memo-hit");
        Assert.Equal("AAAAAAAAAA", second!.Title);   // gated parse reused
        Assert.Equal("r2", second.QuotaWait!.Reason); // marker re-applied on hit
    }

    [Fact]
    public void SameLengthRewriteInsideTheTimestampGranule_IsStillSeenOnTheNextScan()
    {
        // The counterpart of the hit above: when the previous write is still
        // inside the filesystem's timestamp granule, a second write of the same
        // length would leave the fingerprint identical, so the folder must not be
        // memoized at all. Reproduced deterministically by pinning both writes to
        // one instant and telling the scanner that instant is racily fresh -
        // which is what the kernel's coarse clock does on its own for 94% of
        // back-to-back rewrites on the review host (AGT-2867).
        var scanner = BuildScanner(timestampGranularity: TimeSpan.FromDays(3650));
        var dir = SeedJob("same-granule", TaskStates.Progress,
            HeaderJson("same-granule", TaskStates.Progress, "AAAAAAAAAA"));
        var taskJsonPath = Path.Combine(dir, "task.json");

        PinStats(dir, taskJsonPath);
        Assert.Equal("AAAAAAAAAA", Scan(scanner, "same-granule")!.Title);

        // Equal length, so only the last-write time could tell the two versions
        // apart - and it cannot, because both land on the pinned instant.
        File.WriteAllText(taskJsonPath,
            HeaderJson("same-granule", TaskStates.Progress, "BBBBBBBBBB"));
        PinStats(dir, taskJsonPath);

        Assert.Equal("BBBBBBBBBB", Scan(scanner, "same-granule")!.Title);
    }

    private static readonly DateTime PinnedInstant = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void PinStats(string dir, string taskJsonPath)
    {
        File.SetLastWriteTimeUtc(taskJsonPath, PinnedInstant);
        Directory.SetLastWriteTimeUtc(dir, PinnedInstant);
    }
}
