using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2007 before/after measurement. Not an assertion gate - it exists to
/// produce the wall-time numbers that back the perf claim, on the real host,
/// against a real repo. The "before" figure is an inline reproduction of the
/// OLD serial algorithm using the (unchanged, one-spawn-each) GitService
/// primitives in the exact order BuildView / ReadStatusAtRoot used to call
/// them, so the comparison is apples-to-apples on the same machine and repo.
/// The "after" figure calls the optimized production code path.
///
/// <para>
/// Gated by <c>RUN_GIT_INFO_PERF=1</c> so it never adds git-heavy wall time to
/// a normal <c>dotnet test</c>. Run with:
/// <code>RUN_GIT_INFO_PERF=1 dotnet test backend.Tests --filter "FullyQualifiedName~GitInfoPerfMeasurementTests"</code>
/// </para>
/// </summary>
public sealed class GitInfoPerfMeasurementTests : IDisposable
{
    private const string Integration = "develop";
    private const string Release = "main";

    private readonly ITestOutputHelper _out;
    private readonly string _tempDir;

    public GitInfoPerfMeasurementTests(ITestOutputHelper output)
    {
        _out = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "git-info-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Measure_GitInfo_BeforeAfter()
    {
        if (Environment.GetEnvironmentVariable("RUN_GIT_INFO_PERF") != "1")
        {
            _out.WriteLine("Skipped: set RUN_GIT_INFO_PERF=1 to run.");
            return;
        }

        _out.WriteLine($"host cores={Environment.ProcessorCount} os={Environment.OSVersion}");
        _out.WriteLine("");
        var referenceRepo = Environment.GetEnvironmentVariable("GIT_INFO_PERF_REPO");
        if (!string.IsNullOrWhiteSpace(referenceRepo))
        {
            MeasureReferenceStatus(Path.GetFullPath(referenceRepo));
            _out.WriteLine("");
            if (Environment.GetEnvironmentVariable("GIT_INFO_PERF_REFERENCE_ONLY") == "1")
                return;
        }
        MeasureStatus();
        _out.WriteLine("");
        foreach (var n in new[] { 1, 5, 10 }) MeasureProvenance(n);
    }

    private void MeasureReferenceStatus(string repoRoot)
    {
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"GIT_INFO_PERF_REPO does not exist: {repoRoot}");

        // Point a disposable task record at the supplied repository. The status
        // endpoint is read-only; all fixture files remain under _tempDir.
        var watchPath = Path.Combine(_tempDir, "reference-jobs");
        Directory.CreateDirectory(watchPath);
        const string jobId = "perf-reference";
        WriteJob(watchPath, jobId);
        var git = BuildGitService(repoRoot, watchPath);

        git.GetStatus(jobId, watchPath, preferRunLocation: true);
        var warm = Measure(15, () => git.GetStatus(jobId, watchPath, preferRunLocation: true));
        var cold = Measure(15, () =>
        {
            GitService.InvalidateToplevelCache(repoRoot);
            git.InvalidateStatusCache();
            git.GetStatus(jobId, watchPath, preferRunLocation: true);
        });
        var before = Measure(15, () => OldStatusSerial(repoRoot));

        _out.WriteLine($"== reference tasks/git/status repo={repoRoot} ==");
        _out.WriteLine($"  BEFORE (6 serial spawns)       p50={before.P50:F0}ms  min={before.Min:F0}ms");
        _out.WriteLine($"  AFTER  cold (2 serial + 4 par) p50={cold.P50:F0}ms  min={cold.Min:F0}ms");
        _out.WriteLine($"  AFTER  warm (toplevel cached)  p50={warm.P50:F0}ms  min={warm.Min:F0}ms");
    }

    private void MeasureStatus()
    {
        var (repoRoot, watchPath, jobId) = SeedStatusRepo("status");
        var git = BuildGitService(repoRoot, watchPath);

        // Warm up JIT + git object caches.
        git.GetStatus(jobId, watchPath, preferRunLocation: true);

        var newWarm = Measure(15, () => { git.GetStatus(jobId, watchPath, preferRunLocation: true); });
        var newCold = Measure(15, () =>
        {
            GitService.InvalidateToplevelCache(repoRoot);
            git.InvalidateStatusCache();
            git.GetStatus(jobId, watchPath, preferRunLocation: true);
        });
        var old = Measure(15, () => OldStatusSerial(repoRoot));

        _out.WriteLine("== tasks/git/status (task-detail live status) ==");
        _out.WriteLine($"  BEFORE (6 serial spawns)      p50={old.P50:F0}ms  min={old.Min:F0}ms");
        _out.WriteLine($"  AFTER  cold (2 serial + 4 par) p50={newCold.P50:F0}ms  min={newCold.Min:F0}ms");
        _out.WriteLine($"  AFTER  warm (toplevel cached)  p50={newWarm.P50:F0}ms  min={newWarm.Min:F0}ms");
    }

    private void MeasureProvenance(int commitCount)
    {
        var (repoRoot, watchPath, jobId) = SeedProvenanceRepo($"prov{commitCount}", commitCount);
        var git = BuildGitService(repoRoot, watchPath);
        var prov = BuildProvenanceService(repoRoot, watchPath);

        var info = ScanJob(repoRoot, watchPath, jobId)!;
        // Populate provenance.Base (fork point) so the batch membership walks the
        // real base..branch range - the same state a live task carries.
        prov.RecordTransition(info, TaskStates.AutoReview);
        info = ScanJob(repoRoot, watchPath, jobId)!;

        var branch = "task/" + jobId;
        var root = repoRoot;
        var baseSha = git.GetMergeBase(root, branch, Integration) ?? "";
        var anchor = git.GetBranchTip(root, branch) ?? "";

        // Warm up.
        prov.BuildView(info);

        var iters = commitCount >= 10 ? 5 : 8;
        var after = Measure(iters, () => { prov.BuildView(info); });
        var before = Measure(iters, () => OldProvenanceSerial(git, root, branch, baseSha, anchor));

        _out.WriteLine($"== tasks/provenance (N={commitCount} commits) ==");
        _out.WriteLine($"  BEFORE (fixed reads + 2*N serial merge-base) p50={before.P50:F0}ms  min={before.Min:F0}ms");
        _out.WriteLine($"  AFTER  (parallel reads + rev-list sets)      p50={after.P50:F0}ms  min={after.Min:F0}ms");
    }

    // --- OLD serial reproductions (same primitives, same order) ------------

    private static void OldStatusSerial(string root)
    {
        RunGitRaw(root, "rev-parse", "--show-toplevel");
        RunGitRaw(root, "worktree", "list", "--porcelain");
        RunGitRaw(root, "status", "--porcelain=v1");
        RunGitRaw(root, "rev-parse", "--abbrev-ref", "HEAD");
        RunGitRaw(root, "diff", "--numstat", "HEAD");
        RunGitRaw(root, "diff", "--numstat");
    }

    private static void OldProvenanceSerial(GitService git, string root, string branch, string baseSha, string anchor)
    {
        string? HeadOf(string b) => git.GetBranchTip(root, b) ?? git.GetBranchTip(root, "origin/" + b);
        bool Contained(string sha, string b) => git.IsAncestor(root, sha, b) || git.IsAncestor(root, sha, "origin/" + b);

        git.GetBranchTip(root, branch);
        HeadOf(Integration);
        HeadOf(Release);
        if (anchor.Length > 0) { Contained(anchor, Integration); Contained(anchor, Release); }

        var range = git.GetCommitsInRangeAtRoot(root, baseSha, branch);
        foreach (var c in range)
        {
            Contained(c.Sha, Integration);
            Contained(c.Sha, Release);
        }
    }

    // --- Measurement + seeding ---------------------------------------------

    private static (double Min, double P50) Measure(int iterations, Action body)
    {
        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (samples[0], samples[samples.Count / 2]);
    }

    private (string RepoRoot, string WatchPath, string JobId) SeedStatusRepo(string name)
    {
        var repoRoot = Path.Combine(_tempDir, name, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);
        RunGitRaw(Path.GetDirectoryName(repoRoot)!, "init", "-q", "-b", "main", "repo");
        ConfigRepo(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGitRaw(repoRoot, "add", "-A");
        RunGitRaw(repoRoot, "commit", "-q", "-m", "seed");
        // Dirty tree so status has real work to render.
        for (var i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(repoRoot, $"file{i}.txt"), $"change {i}\nline\n");
        var jobId = "perf-status";
        WriteJob(watchPath, jobId);
        return (repoRoot, watchPath, jobId);
    }

    private (string RepoRoot, string WatchPath, string JobId) SeedProvenanceRepo(string name, int commitCount)
    {
        var repoRoot = Path.Combine(_tempDir, name, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);
        RunGitRaw(Path.GetDirectoryName(repoRoot)!, "init", "-q", "-b", "main", "repo");
        ConfigRepo(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGitRaw(repoRoot, "add", "-A");
        RunGitRaw(repoRoot, "commit", "-q", "-m", "seed");
        RunGitRaw(repoRoot, "checkout", "-q", "-b", Integration);

        var jobId = name;
        RunGitRaw(repoRoot, "checkout", "-q", "-b", "task/" + jobId);
        for (var i = 0; i < commitCount; i++)
        {
            File.WriteAllText(Path.Combine(repoRoot, $"work{i}.txt"), $"task work {i}");
            RunGitRaw(repoRoot, "add", "-A");
            RunGitRaw(repoRoot, "commit", "-q", "-m", $"feat: work {i}");
        }
        WriteJob(watchPath, jobId);
        return (repoRoot, watchPath, jobId);
    }

    private static void WriteJob(string watchPath, string jobId)
    {
        var jobFolder = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(jobFolder);
        var jobJson = new { id = jobId, title = jobId, state = "3-progress", order = 1, agent = "claude" };
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ConfigRepo(string repoRoot)
    {
        RunGitRaw(repoRoot, "config", "user.email", "test@example.com");
        RunGitRaw(repoRoot, "config", "user.name", "test");
        RunGitRaw(repoRoot, "config", "commit.gpgsign", "false");
    }

    private static GitService BuildGitService(string repoRoot, string watchPath)
    {
        var config = WatchConfig(repoRoot, watchPath);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static TaskProvenanceService BuildProvenanceService(string repoRoot, string watchPath)
    {
        var config = WatchConfig(repoRoot, watchPath);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new TaskProvenanceService(git, settings, mutations, NullLogger<TaskProvenanceService>.Instance);
    }

    private static TaskInfo? ScanJob(string repoRoot, string watchPath, string jobId)
    {
        var config = WatchConfig(repoRoot, watchPath);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.FindJob(jobId, watchPath);
    }

    private static IConfiguration WatchConfig(string repoRoot, string watchPath)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["TaskRepository"] = watchPath,
        }).Build();

    private static void RunGitRaw(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
    }
}
