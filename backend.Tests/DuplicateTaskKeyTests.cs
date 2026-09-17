using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Root-cause coverage for duplicate display keys: two different tasks
/// sharing one key (the ASS-594 / ASS-598 incident, plus the contiguous
/// band around them). Confirmed cause: the per-project key counter
/// (<c>NextTaskKeySeq</c> on the project record) is held in memory and can
/// be rewound under the registry - e.g. a second backend sharing the
/// workspace persists a stale snapshot - so the next mint re-issues a
/// number that is already live on disk.
///
/// <list type="number">
/// <item>Mint derives its floor from the keys actually on disk, so a
/// rewound counter can never re-issue a live key.</item>
/// <item>The one-shot dedup sweep keeps the oldest task on the contested
/// key, re-keys the namesakes above the on-disk maximum, preserves ids and
/// content, and is idempotent.</item>
/// <item>The pure numeric-tail parser that both rely on.</item>
/// </list>
/// </summary>
public class DuplicateTaskKeyTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";
    private const string ShortCode = "DEM"; // ShortCodeGenerator.Derive("Demo")

    public DuplicateTaskKeyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "dup-key-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // Pure parser
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ASS-594", "ASS", true, 594)]
    [InlineData("ass-594", "ASS", true, 594)]  // case-insensitive prefix
    [InlineData("DEM-1", "DEM", true, 1)]
    [InlineData("ASS-594", "RUN", false, 0)]   // prefix mismatch
    [InlineData("ASS-12a", "ASS", false, 0)]   // non-numeric tail
    [InlineData("ASS-0", "ASS", false, 0)]     // non-positive tail
    [InlineData("ASS-", "ASS", false, 0)]      // empty tail
    [InlineData(null, "ASS", false, 0)]
    [InlineData("", "ASS", false, 0)]
    public void TaskKeyNumbers_TryParse(string? key, string shortCode, bool ok, int expected)
    {
        Assert.Equal(ok, TaskKeyNumbers.TryParse(key, shortCode, out var n));
        Assert.Equal(expected, n);
    }

    [Fact]
    public void TaskKeyNumbers_HighestNumber_IgnoresOtherPrefixesAndBlanks()
    {
        var keys = new string?[] { "ASS-1", "ASS-9", "ASS-3", "RUN-50", null, "", "ASS-x" };
        Assert.Equal(9, TaskKeyNumbers.HighestNumber(keys, "ASS"));
        Assert.Equal(0, TaskKeyNumbers.HighestNumber(keys, "PT"));
    }

    // ------------------------------------------------------------------
    // Mint guard
    // ------------------------------------------------------------------

    [Fact]
    public void Mint_WhenCounterDriftedBelowDisk_DoesNotReissueLiveKey()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // Disk already carries DEM-1..DEM-3 while the registry counter is still
        // at 1 (a rewound / never-aligned counter). Without the disk-derived
        // floor the next mint would hand out DEM-1 again.
        SeedKeyedJob(TaskStates.Archive, "alpha", "DEM-1", "2026-05-31T10:00:00Z");
        SeedKeyedJob(TaskStates.Archive, "beta", "DEM-2", "2026-05-31T10:01:00Z");
        SeedKeyedJob(TaskStates.HumanReview, "gamma", "DEM-3", "2026-05-31T10:02:00Z");

        var newId = mutations.CreateJob(NewRequest("Fresh Task", TaskStates.Ready));
        var minted = scanner.FindJob(newId, _watchPath)!.Key;

        Assert.Equal("DEM-4", minted);
        AssertAllKeysUnique(scanner);
    }

    [Fact]
    public void Mint_SequentialCreates_AreUniqueAndMonotonic()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var keys = new List<string?>();
        for (var i = 0; i < 5; i++)
        {
            var id = mutations.CreateJob(NewRequest($"Task {i}", TaskStates.Ready));
            keys.Add(scanner.FindJob(id, _watchPath)!.Key);
        }

        Assert.Equal(new[] { "DEM-1", "DEM-2", "DEM-3", "DEM-4", "DEM-5" }, keys);
        AssertAllKeysUnique(scanner);
    }

    // ------------------------------------------------------------------
    // Dedup sweep
    // ------------------------------------------------------------------

    [Fact]
    public void Dedup_KeepsOldestOnContestedKey_ReKeysNamesake_PreservesContent_AndIsIdempotent()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // Two different tasks share DEM-5; an unrelated DEM-3 also exists so the
        // replacement key must clear the on-disk maximum, not just the keeper.
        SeedKeyedJob(TaskStates.Ready, "picker", "DEM-5", "2026-05-31T17:39:32Z", title: "Create-Dialog Parent-Epic-Picker");
        SeedKeyedJob(TaskStates.Archive, "completion-loop", "DEM-5", "2026-05-31T17:54:51Z", title: "Orchestrator-Completion-Loop");
        SeedKeyedJob(TaskStates.HumanReview, "older", "DEM-3", "2026-05-30T09:00:00Z");

        var rekeyed = mutations.DeduplicateTaskKeys();
        Assert.Equal(1, rekeyed);

        scanner.InvalidateCache();
        var picker = scanner.FindJob("picker", _watchPath)!;
        var loop = scanner.FindJob("completion-loop", _watchPath)!;

        // Oldest namesake keeps the contested key; the later one is re-keyed
        // above the on-disk maximum (DEM-5 -> DEM-6).
        Assert.Equal("DEM-5", picker.Key);
        Assert.Equal("DEM-6", loop.Key);

        // Content is preserved: only the key field moved.
        Assert.Equal("completion-loop", loop.Id);
        Assert.Equal("Orchestrator-Completion-Loop", loop.Title);

        AssertAllKeysUnique(scanner);

        // Idempotent: nothing left to resolve on a second pass.
        Assert.Equal(0, mutations.DeduplicateTaskKeys());
    }

    /// <summary>
    /// A re-key the sweep could not write is not a resolved collision.
    ///
    /// <para>The whole sweep is the <c>key</c> write; its return value is the
    /// only thing any caller sees. Boot logs it, the admin endpoint reports
    /// it, and both treat a non-zero count as "the duplicates are gone". When
    /// the write itself fails - an unwritable <c>task.json</c>, a folder that
    /// moved under the sweep, a swap that ran out of retry budget while every
    /// core was busy - counting it anyway reports exactly the state the sweep
    /// exists to prevent: two live tasks on one key, declared fixed. The
    /// failure has to stay visible, so the next sweep retries it and a reader
    /// of the count is not lied to.</para>
    /// </summary>
    [Fact]
    public void Dedup_WhenTheReKeyWriteFails_IsNotCountedAsResolved()
    {
        var writer = new FailingAtomicJsonFileWriter();
        var (machine, scanner, mutations, _) = Build(writer);
        machine.EnsureStateFoldersAndMigrate();

        SeedKeyedJob(TaskStates.Ready, "picker", "DEM-5", "2026-05-31T17:39:32Z");
        SeedKeyedJob(TaskStates.Archive, "completion-loop", "DEM-5", "2026-05-31T17:54:51Z");

        Assert.Equal(0, mutations.DeduplicateTaskKeys());
        Assert.Equal(1, writer.Attempts);

        // Disk is untouched, so the collision is still there to be found.
        scanner.InvalidateCache();
        Assert.Equal("DEM-5", scanner.FindJob("picker", _watchPath)!.Key);
        Assert.Equal("DEM-5", scanner.FindJob("completion-loop", _watchPath)!.Key);
    }

    /// <summary>Fails every write the way a full disk or a lost swap does.</summary>
    private sealed class FailingAtomicJsonFileWriter : IAtomicJsonFileWriter
    {
        public int Attempts;

        public void Write(string path, string content)
        {
            Attempts++;
            throw new IOException("write refused by the test writer");
        }
    }

    [Fact]
    public void Dedup_ThreeWayCollision_ReKeysTwoNamesakes()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // The ASS-592 shape: one key minted onto three tasks.
        SeedKeyedJob(TaskStates.Archive, "one", "DEM-7", "2026-05-31T12:52:53Z");
        SeedKeyedJob(TaskStates.Archive, "two", "DEM-7", "2026-05-31T16:29:51Z");
        SeedKeyedJob(TaskStates.HumanReview, "three", "DEM-7", "2026-05-31T17:39:26Z");

        Assert.Equal(2, mutations.DeduplicateTaskKeys());

        scanner.InvalidateCache();
        Assert.Equal("DEM-7", scanner.FindJob("one", _watchPath)!.Key);
        AssertAllKeysUnique(scanner);
        Assert.Equal(0, mutations.DeduplicateTaskKeys());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void AssertAllKeysUnique(TaskScannerService scanner)
    {
        var dupes = scanner.ScanAllJobs()
            .Where(j => !string.IsNullOrWhiteSpace(j.Key))
            .GroupBy(j => j.Key!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} x{g.Count()}")
            .ToList();
        Assert.True(dupes.Count == 0, "duplicate keys present: " + string.Join(", ", dupes));
    }

    private CreateTaskRequest NewRequest(string title, string? targetState = null) => new()
    {
        Title = title,
        WatchPath = _watchPath,
        Agent = "claude",
        TargetState = targetState
    };

    private void SeedKeyedJob(string state, string slug, string key, string createdAtIso, string? title = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title ?? slug}\",\"state\":\"{state}\"," +
            $"\"order\":1,\"agent\":\"claude\",\"key\":\"{key}\",\"createdAt\":\"{createdAtIso}\"}}");
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations, ProjectRegistry registry) Build(
        IAtomicJsonFileWriter? keyFileWriter = null)
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var laneMutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, laneMutex);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        // Register the project so MintTaskKey / dedup can resolve a short code.
        registry.EnsureProjectForStorage(_watchPath, "Demo", DefaultWorkspace.Id);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: null,
            laneMutex: laneMutex,
            fileWriter: keyFileWriter);
        return (machine, scanner, mutations, registry);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
