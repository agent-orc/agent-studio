using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The stale-branch sweep against a real bare remote with a seeded mixed
/// history (AGT-2794 requirement 6). A <c>post-receive</c> hook on the bare
/// remote records one line per push, which is how "report-only never deletes"
/// and "bulk deletions run in batches of at most 100 refs per push" are proven
/// against git itself rather than against a mock.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class BranchSweepServiceBareRemoteTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
    private const string Project = "Demo";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "branch-sweep-" + Guid.NewGuid().ToString("N"));

    public BranchSweepServiceBareRemoteTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void ClassifiesEveryNamespaceIncludingOrphansAndProtectedRefs()
    {
        var fixture = Seed();

        var report = fixture.Sweep.Plan(Project);

        Assert.Null(report.Error);
        var byRef = report.Candidates.ToDictionary(candidate => candidate.Ref, StringComparer.Ordinal);

        // Merged and aged: the ordinary reclaim.
        Assert.True(byRef["task/DEM-1"].Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.Delete), byRef["task/DEM-1"].Decision);
        Assert.Equal("DEM-1", byRef["task/DEM-1"].TaskKey);

        // Unmerged with a live card: never a candidate, however old.
        Assert.False(byRef["task/DEM-2"].Eligible);
        Assert.Equal(TaskStates.Progress, byRef["task/DEM-2"].TaskState);

        // Archived without integration, and a ref whose card no longer exists:
        // the two cases the event-driven path can never reach.
        Assert.True(byRef["task/DEM-3"].Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.AbandonedRefAged), byRef["task/DEM-3"].Decision);
        Assert.True(byRef["task/DEM-9"].Eligible);
        Assert.Null(byRef["task/DEM-9"].TaskState);

        // Result refs follow main containment only.
        Assert.True(byRef["agent-studio/results/attempt-1/fence-1/aaa"].Eligible);
        Assert.False(byRef["agent-studio/results/attempt-2/fence-1/bbb"].Eligible);

        // Fresh quarantine ref is inside its 30-day window.
        Assert.False(byRef["agent-studio/quarantine/runner-1/DEM-2/attempt-1/fence-1/ccc"].Eligible);

        // Protected and unmanaged refs are reported but never candidates.
        foreach (var reference in new[] { "main", "develop", "release/1.0", "v1.0.0", "feature/experiment" })
        {
            Assert.False(byRef[reference].Eligible);
            Assert.Equal(nameof(BranchRetentionDecision.UnsupportedNamespace), byRef[reference].Decision);
        }
        Assert.Equal(BranchSweepClasses.Unmanaged, byRef["feature/experiment"].Class);
        Assert.DoesNotContain("HEAD", byRef.Keys);

        // Aggregate = sum of the visible children.
        Assert.Equal(report.Candidates.Count, report.Totals.Sum(totals => totals.Total));
        Assert.Equal(report.Candidates.Count, report.AgeHistogram.Sum(bucket => bucket.Refs));
    }

    [Fact]
    public void ReportOnlyModeWritesAReportAndNeverPushesADelete()
    {
        var fixture = Seed();
        var before = RemoteBranches(fixture.Bare);
        ResetPushLog(fixture.Bare);

        var report = fixture.Sweep.Run(Project);

        Assert.Equal(BranchSweepModes.ReportOnly, report.Mode);
        Assert.True(report.EligibleCount > 0, "the fixture must offer something to reclaim");
        Assert.Equal(0, report.DeletedCount);
        Assert.Empty(report.Deletions);
        Assert.Equal(report.RefsBefore, report.RefsAfter);
        // The hard assertion: git received no push at all in report-only mode.
        Assert.Empty(PushLog(fixture.Bare));
        Assert.Equal(before, RemoteBranches(fixture.Bare));

        var directory = Path.Combine(_tempDir, "workspace", "reports", "branch-sweep", Project);
        var stamp = BranchSweepReportBuilder.Stamp(report.StartedAtUtc);
        Assert.True(File.Exists(Path.Combine(directory, stamp + ".json")));
        var markdown = File.ReadAllText(Path.Combine(directory, stamp + ".md"));
        Assert.Contains("Mode: `report-only`", markdown, StringComparison.Ordinal);
        Assert.Contains("## Refs by class", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ReclaimModeDeletesExactlyWhatThePolicyAllows()
    {
        var fixture = Seed();
        fixture.Settings.SetBranchSweep(Project, new BranchSweepSettings { Mode = BranchSweepModes.Reclaim });
        var expected = fixture.Sweep.Plan(Project).Candidates
            .Where(candidate => candidate.Eligible)
            .Select(candidate => candidate.Ref)
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToList();
        ResetPushLog(fixture.Bare);

        var report = fixture.Sweep.Run(Project);

        Assert.Equal(BranchSweepModes.Reclaim, report.Mode);
        Assert.Equal(
            expected,
            report.Deletions.Where(d => d.Deleted).Select(d => d.Ref)
                .OrderBy(reference => reference, StringComparer.Ordinal).ToList());
        Assert.Equal(report.RefsBefore - expected.Count, report.RefsAfter);

        var remaining = RemoteBranches(fixture.Bare);
        Assert.DoesNotContain("task/DEM-1", remaining);
        Assert.DoesNotContain("task/DEM-3", remaining);
        Assert.Contains("task/DEM-2", remaining);
        Assert.Contains("main", remaining);
        Assert.Contains("develop", remaining);
        Assert.Contains("release/1.0", remaining);
        Assert.Contains("feature/experiment", remaining);

        // A second run finds nothing left to reclaim: the sweep is idempotent.
        var second = fixture.Sweep.Run(Project);
        Assert.Equal(0, second.DeletedCount);
    }

    [Fact]
    public void ExecuteDeletesOnlyTheConfirmedSubsetAndRejectsIneligibleRefs()
    {
        var fixture = Seed();

        var result = fixture.Sweep.Execute(Project, new BranchSweepExecuteRequest(
        [
            new BranchSweepExecutionItem("task/DEM-1", TipSha(fixture.Repo, "origin/task/DEM-1")),
            // Unmerged ref of a live card: the policy keeps it even though the
            // operator asked for it.
            new BranchSweepExecutionItem("task/DEM-2", TipSha(fixture.Repo, "origin/task/DEM-2")),
            // Protected ref, hand-crafted into the request.
            new BranchSweepExecutionItem("main", TipSha(fixture.Repo, "origin/main")),
            // Eligible ref, but the operator's tip is stale.
            new BranchSweepExecutionItem("task/DEM-3", "0000000000000000000000000000000000000000"),
        ]));

        Assert.True(result.IsRepo);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(3, result.KeptCount);
        Assert.Contains(result.Actions, action => action.Ref == "task/DEM-1" && action.Deleted);
        Assert.Contains(result.Actions, action =>
            action.Ref == "task/DEM-3" && !action.Deleted
            && action.Reason.Contains("Tip changed", StringComparison.Ordinal));

        var remaining = RemoteBranches(fixture.Bare);
        Assert.DoesNotContain("task/DEM-1", remaining);
        Assert.Contains("task/DEM-2", remaining);
        Assert.Contains("task/DEM-3", remaining);
        Assert.Contains("main", remaining);
    }

    [Fact]
    public void BulkDeletionsGoOutInBatchesOfAtMostOneHundredRefsPerPush()
    {
        var fixture = Seed();
        var head = TipSha(fixture.Repo, "origin/main");
        var refs = Enumerable.Range(1, 150)
            .Select(index => ($"task/BULK-{index}", head))
            .ToList();
        var createArgs = new List<string> { "push", "-q", "origin" };
        createArgs.AddRange(refs.Select(reference => $"{head}:refs/heads/{reference.Item1}"));
        RunGit(fixture.Repo, [.. createArgs]);
        RunGit(fixture.Repo, "fetch", "-q", "--prune", "origin");
        ResetPushLog(fixture.Bare);

        var outcomes = fixture.Git.DeleteRemoteBranchesAtTip(fixture.Repo, refs);

        Assert.Equal(150, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.True(outcome.Deleted, outcome.Error));
        var pushes = PushLog(fixture.Bare);
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, count => Assert.True(count <= GitService.MaxRefsPerDeletePush, $"{count} refs in one push"));
        Assert.Equal(150, pushes.Sum());
        Assert.DoesNotContain("task/BULK-1", RemoteBranches(fixture.Bare));
    }

    [Fact]
    public void ProjectWindowOverrideChangesWhatTheSweepOffers()
    {
        var fixture = Seed();
        const string quarantine = "agent-studio/quarantine/runner-1/DEM-2/attempt-1/fence-1/ccc";

        Assert.False(Candidate(fixture, quarantine).Eligible);

        fixture.Settings.SetBranchSweep(Project, new BranchSweepSettings
        {
            Mode = BranchSweepModes.ReportOnly,
            QuarantineRetentionDays = 1,
        });

        Assert.True(Candidate(fixture, quarantine).Eligible);
    }

    private static BranchSweepCandidate Candidate(Fixture fixture, string reference)
        => fixture.Sweep.Plan(Project).Candidates.Single(candidate => candidate.Ref == reference);

    // ------------------------------------------------------------------
    // Fixture
    // ------------------------------------------------------------------

    private sealed record Fixture(
        string Repo,
        string Bare,
        GitService Git,
        ProjectSettingsService Settings,
        BranchSweepService Sweep);

    /// <summary>
    /// A bare remote plus a working clone carrying one ref per interesting
    /// class, and a task workspace whose cards make three of the task refs
    /// live, archived, and orphaned respectively.
    /// </summary>
    private Fixture Seed()
    {
        var bare = Path.Combine(_tempDir, "remote.git");
        var repo = Path.Combine(_tempDir, "repo");
        var workspace = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(repo);

        RunGit(_tempDir, "init", "-q", "--bare", bare);
        InstallPushLogHook(bare);
        RunGit(_tempDir, "clone", "-q", bare, repo);
        RunGit(repo, "config", "user.email", "sweep@test.local");
        RunGit(repo, "config", "user.name", "Sweep Test");
        RunGit(repo, "checkout", "-q", "-b", "main");
        Commit(repo, "README.md", "seed", "seed", Now.AddDays(-500));
        RunGit(repo, "push", "-q", "-u", "origin", "main");

        // Merged and aged task ref: the ordinary reclaim case.
        Branch(repo, "task/DEM-1", "one.txt", Now.AddDays(-400));
        // Unmerged, live card.
        Branch(repo, "task/DEM-2", "two.txt", Now.AddDays(-400));
        // Unmerged, archived card - archived without integration.
        Branch(repo, "task/DEM-3", "three.txt", Now.AddDays(-400));
        // Unmerged, no card at all - a ref from a run whose task is gone.
        Branch(repo, "task/DEM-9", "nine.txt", Now.AddDays(-400));
        // Delivery proof already in main, and one that never landed.
        Branch(repo, "agent-studio/results/attempt-1/fence-1/aaa", "proof-a.txt", Now.AddDays(-400));
        Branch(repo, "agent-studio/results/attempt-2/fence-1/bbb", "proof-b.txt", Now.AddDays(-400));
        // Quarantined delivery, still inside its window.
        Branch(repo, "agent-studio/quarantine/runner-1/DEM-2/attempt-1/fence-1/ccc", "quarantine.txt", Now.AddDays(-2));
        // Never-candidate refs.
        Branch(repo, "release/1.0", "release.txt", Now.AddDays(-400));
        Branch(repo, "v1.0.0", "tagbranch.txt", Now.AddDays(-400));
        Branch(repo, "feature/experiment", "experiment.txt", Now.AddDays(-400));

        RunGit(repo, "checkout", "-q", "main");
        foreach (var merged in new[] { "task/DEM-1", "agent-studio/results/attempt-1/fence-1/aaa" })
            Merge(repo, merged, Now.AddDays(-380));
        RunGit(repo, "checkout", "-q", "-b", "develop");
        RunGit(repo, "push", "-q", "--all", "origin");
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "fetch", "-q", "--prune", "origin");

        SeedCard(workspace, TaskStates.Progress, "two", "DEM-2");
        SeedCard(workspace, TaskStates.Archive, "three", "DEM-3");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(workspace, "projects", Project),
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance, configuration);
        var sweep = new BranchSweepService(
            git,
            new AgentStudio.Registry.ProjectRegistry(
                configuration, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
            settings,
            scanner,
            new AttemptAuthorityService(configuration, NullLogger<AttemptAuthorityService>.Instance),
            new BranchSweepReportStore(configuration, NullLogger<BranchSweepReportStore>.Instance),
            NullLogger<BranchSweepService>.Instance,
            new FixedTimeProvider(Now));
        return new Fixture(repo, bare, git, settings, sweep);
    }

    private static void SeedCard(string workspace, string state, string slug, string key)
    {
        var dir = Path.Combine(workspace, "projects", Project, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1," +
            $"\"agent\":\"claude\",\"key\":\"{key}\",\"createdAt\":\"2026-01-01T00:00:00Z\"}}");
    }

    private static void Branch(string repo, string branch, string file, DateTimeOffset at)
    {
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "checkout", "-q", "-b", branch);
        Commit(repo, file, branch, $"work on {branch}", at);
        RunGit(repo, "checkout", "-q", "main");
        // Local heads are irrelevant to a remote sweep; keep only the remote copy.
        RunGit(repo, "push", "-q", "origin", branch);
        RunGit(repo, "branch", "-q", "-D", branch);
    }

    private static void Merge(string repo, string branch, DateTimeOffset at)
        => Run(repo, ["merge", "-q", "--no-ff", "--no-edit", $"origin/{branch}"], Dates(at));

    private static void Commit(string repo, string path, string content, string message, DateTimeOffset at)
    {
        File.WriteAllText(Path.Combine(repo, path), content);
        RunGit(repo, "add", path);
        Run(repo, ["commit", "-q", "-m", message], Dates(at));
    }

    private static Dictionary<string, string> Dates(DateTimeOffset at) => new()
    {
        ["GIT_AUTHOR_DATE"] = at.ToString("o"),
        ["GIT_COMMITTER_DATE"] = at.ToString("o"),
    };

    private static string TipSha(string repo, string reference)
        => Run(repo, ["rev-parse", reference]).Out.Trim();

    private static IReadOnlyList<string> RemoteBranches(string bare)
        => Run(bare, ["for-each-ref", "--format=%(refname:short)", "refs/heads"]).Out
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    // ---- push accounting on the bare remote --------------------------------

    private static string PushLogPath(string bare) => Path.Combine(bare, "push-log.txt");

    private static void InstallPushLogHook(string bare)
    {
        var hook = Path.Combine(bare, "hooks", "post-receive");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook,
            "#!/bin/sh\n"
            + "count=0\n"
            + "while read old new ref; do count=$((count+1)); done\n"
            + $"echo \"$count\" >> \"{PushLogPath(bare)}\"\n");
        File.SetUnixFileMode(hook,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void ResetPushLog(string bare)
    {
        try { File.Delete(PushLogPath(bare)); }
        catch { /* best-effort */ }
    }

    /// <summary>Ref count of every push the bare remote accepted since the last reset.</summary>
    private static IReadOnlyList<int> PushLog(string bare)
    {
        var path = PushLogPath(bare);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => int.Parse(line.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    // ---- git plumbing ------------------------------------------------------

    private static void RunGit(string cwd, params string[] args)
    {
        var result = Run(cwd, args);
        if (result.Code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Err}");
    }

    private static (string Out, string Err, int Code) Run(
        string cwd,
        string[] args,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (output, error, process.ExitCode);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
