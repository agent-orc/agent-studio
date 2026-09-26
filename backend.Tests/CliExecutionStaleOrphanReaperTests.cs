using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for the periodic stale-orphan reaper added to fix the
/// "folder move fails — file in use by another process" wedge. The startup
/// reaper alone let a long-lived backend accumulate orphan codex/node trees
/// from finished runs; <see cref="GenericCliExecutionService.ReapStaleOrphans"/>
/// is the timer-safe sweep that reaps them without touching a live run.
/// </summary>
public sealed class CliExecutionStaleOrphanReaperTests : IDisposable
{
    private readonly string _repo;
    private readonly List<Process> _spawned = new();

    public CliExecutionStaleOrphanReaperTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "atp-orphan-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repo, ".runtime"));
    }

    [Fact]
    public void ReapStaleOrphans_KillsSurvivingProcessAndPrunesEntry()
    {
        using var proc = SpawnSleeper();
        WriteActiveJobs(new[] { EntryFor("task-a", proc) });

        var svc = NewService();
        svc.ReapStaleOrphans();

        Assert.True(WaitForExit(proc), "stale orphan process should have been killed");
        Assert.Empty(ReadActiveJobs());
    }

    [Fact]
    public void ReapStaleOrphans_LeavesLiveTrackedRunAlone()
    {
        using var proc = SpawnSleeper();
        var svc = NewService();
        // Mark this run as genuinely in-flight: the backend still tracks a
        // live ProcInfo for it. The reaper must not kill it.
        svc.TrackLive("task-live", proc);
        WriteActiveJobs(new[] { EntryFor("task-live", proc) });

        svc.ReapStaleOrphans();

        Assert.False(proc.HasExited, "a live, tracked run must never be reaped by the timer sweep");
        // The entry stays because the run is still live.
        Assert.Single(ReadActiveJobs());

        KillQuietly(proc);
    }

    [Fact]
    public void ReapStaleOrphans_SkipsKillWhenRecordedIdentityMismatches()
    {
        using var proc = SpawnSleeper();
        // Record the real PID but a bogus process name: the PID-recycling
        // guard must refuse to kill a process that is not the one we recorded.
        var bogus = EntryFor("task-recycled", proc) with { ProcessName = "definitely-not-this-process" };
        WriteActiveJobs(new[] { bogus });

        var svc = NewService();
        svc.ReapStaleOrphans();

        Assert.False(proc.HasExited, "must not kill a process whose recorded identity does not match (recycled PID)");
        // Entry is still dropped: the run is no longer ours to track.
        Assert.Empty(ReadActiveJobs());

        KillQuietly(proc);
    }

    [Fact]
    public void ReapStaleOrphans_SkipsKillWhenRecordedIdentityIsUnverifiable()
    {
        using var proc = SpawnSleeper();
        // A CLI that exited before its identity could be read leaves an entry
        // with neither a process name nor a start time. The PID may since have
        // been recycled by an unrelated process - on Linux, any process on the
        // box, up to and including the host that spawned the backend - so the
        // reaper must refuse to blind-kill it rather than take a stranger down.
        var unverifiable = EntryFor("task-unverifiable", proc) with
        {
            ProcessName = null,
            ProcessStartTimeUtc = null,
        };
        WriteActiveJobs(new[] { unverifiable });

        var svc = NewService();
        svc.ReapStaleOrphans();

        Assert.False(proc.HasExited,
            "must not kill a PID whose recorded identity is unverifiable (possible recycled PID)");
        // The entry is still dropped: without a verifiable identity it is not
        // ours to keep tracking.
        Assert.Empty(ReadActiveJobs());

        KillQuietly(proc);
    }

    [Fact]
    public void ReapStaleOrphans_PrunesEntryWhenProcessAlreadyGone()
    {
        var proc = SpawnSleeper();
        var pid = proc.Id;
        var entry = EntryFor("task-gone", proc);
        KillQuietly(proc);
        WaitForExit(proc);
        WriteActiveJobs(new[] { entry });

        var svc = NewService();
        svc.ReapStaleOrphans(); // PID no longer alive -> just prune

        Assert.Empty(ReadActiveJobs());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private TestCliService NewService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _repo })
            .Build();
        return new TestCliService(config);
    }

    private Process SpawnSleeper()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("sleep", "60");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var p = Process.Start(psi)!;
        _spawned.Add(p);
        return p;
    }

    private static bool WaitForExit(Process p)
    {
        try { return p.WaitForExit(10_000); }
        catch { return true; }
    }

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    private string ActiveJobsPath() => Path.Combine(_repo, ".runtime", $"active-jobs-{TestCliService.Type}.json");

    private void WriteActiveJobs(IEnumerable<object> entries)
        => File.WriteAllText(ActiveJobsPath(), JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions { WriteIndented = true }));

    private List<JsonElement> ReadActiveJobs()
    {
        var path = ActiveJobsPath();
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path)) ?? new();
    }

    private static ActiveJobDto EntryFor(string taskKey, Process proc) => new ActiveJobDto
    {
        TaskKey = taskKey,
        JobId = taskKey,
        ProcessId = proc.Id,
        ProcessName = SafeName(proc),
        ProcessStartTimeUtc = SafeStart(proc),
        StartedAt = DateTime.UtcNow
    };

    private static string? SafeName(Process p) { try { return p.ProcessName; } catch { return null; } }
    private static DateTime? SafeStart(Process p) { try { return p.StartTime.ToUniversalTime(); } catch { return null; } }

    public void Dispose()
    {
        foreach (var p in _spawned) KillQuietly(p);
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    /// <summary>Mirrors the private active-jobs record's wire shape (PascalCase).</summary>
    private sealed record ActiveJobDto
    {
        public string TaskKey { get; init; } = "";
        public string JobId { get; init; } = "";
        public int ProcessId { get; init; }
        public string? ProcessName { get; init; }
        public DateTime? ProcessStartTimeUtc { get; init; }
        public DateTime StartedAt { get; init; }
    }

    /// <summary>
    /// Minimal concrete <see cref="GenericCliExecutionService"/> for exercising
    /// the shared active-jobs reaper. Spawn/argument hooks throw because the
    /// reaper path never calls them.
    /// </summary>
    private sealed class TestCliService : GenericCliExecutionService
    {
        public const string Type = "claude";
        public TestCliService(IConfiguration config) : base(Behavior, NullLogger.Instance, config) { }

        private static readonly CliBehavior Behavior = new()
        {
            CliType = Type,
            GetCliPath = _ => "claude",
        };

        /// <summary>Seed the in-memory live-process map so the reaper treats this run as in-flight.</summary>
        public void TrackLive(string jobKey, Process proc)
        {
            var exec = new CliExecution { JobId = jobKey, TaskKey = jobKey, ProcessId = proc.Id, StartedAt = DateTime.UtcNow, Status = "running" };
            _processes[jobKey] = new ProcInfo(proc, exec, _repoCwd);
        }

        private const string _repoCwd = ".";
    }
}
