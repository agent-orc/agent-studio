using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2046: the board merge signal must read the same worktree -> develop -> main
/// ground truth the detail landed-state uses, but batched per repository so a big
/// board never pays a per-card <c>merge-base --is-ancestor</c> fan-out. Every test
/// drives real git against a throwaway repo so the states the card renders are
/// exercised end to end:
///   - on the task branch only (neither develop nor main),
///   - merged into develop but not main,
///   - released to main (both),
///   - a sequential commit landed directly on develop.
/// AGT-2063: the anchor is the task's OWN commit; a card with a branch tip but no
/// task commit carries no signal (its branch base is trivially in develop/main).
/// </summary>
public sealed class BoardMergeStatusServiceTests : IDisposable
{
    private readonly string _tempDir;

    public BoardMergeStatusServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "board-merge-status-" + Guid.NewGuid().ToString("N"));
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
    public void AnchorFor_IsTheLatestTaskCommit_AndNothingElse()
    {
        // AGT-2063: the anchor is the task's own commit. The latest attributed
        // commit wins (newest of the chain).
        var commitOnly = Job("c", commits: new[] { Commit("oldc"), Commit("newc") });
        Assert.Equal("newc", BoardMergeStatusService.AnchorFor(commitOnly));

        // A recorded merge fact without any task commit does not manufacture an
        // anchor or prove integration.
        var mergeButNoCommit = Job("m", prov: Prov(merge: "mergesha"));
        Assert.Null(BoardMergeStatusService.AnchorFor(mergeButNoCommit));

        // A recorded branch tip WITHOUT a task commit is the exact bug source: for
        // a task that produced no commit the tip is the branch base, trivially in
        // develop/main. It must NOT anchor the signal.
        var branchTipButNoCommit = Job("b", prov: Prov(transitions: new[]
        {
            new TaskProvenanceTransition { Lane = "3-progress", BranchTip = "tip1" },
            new TaskProvenanceTransition { Lane = "4-auto-review", BranchTip = "tip2" },
        }));
        Assert.Null(BoardMergeStatusService.AnchorFor(branchTipButNoCommit));

        // Nothing committed -> no anchor, so no signal is produced.
        Assert.Null(BoardMergeStatusService.AnchorFor(Job("empty")));
    }

    [Fact]
    public void BuildLookup_SkipsCardsWithABranchTipButNoTaskCommit()
    {
        // AGT-2063 regression: a card whose task/<id> branch was cut at develop's
        // tip but produced no commit must carry NO merge signal - the branch base
        // is an ancestor of develop, which used to light the develop segment on a
        // card that changed nothing.
        var repo = SeedDevelopMainRepo(out _, out var developTip);
        var svc = BuildService(repo, out var project);
        var job = Job("basetip", project: project, repo: repo,
            prov: Prov(branch: "task/basetip", transitions: new[]
            {
                new TaskProvenanceTransition { Lane = "3-progress", BranchTip = developTip },
            }));

        var lookup = svc.BuildLookup(new[] { job });

        Assert.False(lookup.ContainsKey(job.TaskKey));
    }

    [Fact]
    public void BuildLookup_OnBranchOnly_ReportsNeitherIntegrationNorRelease()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        // A task branch with its own commit, never merged anywhere.
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/onbranch");
        File.WriteAllText(Path.Combine(repo, "onbranch.txt"), "wip");
        Commit(repo, "feat: wip");
        var tip = RunGit(repo, "rev-parse task/onbranch").Out.Trim();

        var svc = BuildService(repo, out var project);
        var job = Job("onbranch", project: project, repo: repo,
            commits: new[] { Commit(tip) },
            prov: Prov(branch: "task/onbranch", transitions: new[]
            {
                new TaskProvenanceTransition { Lane = "3-progress", BranchTip = tip },
            }));

        var signal = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.False(signal.InIntegration);
        Assert.False(signal.InRelease);
        Assert.Equal("task/onbranch", signal.Branch);
        Assert.Null(signal.IntegrationSha);
        Assert.Null(signal.ReleaseSha);
    }

    [Fact]
    public void BuildLookup_RecordedMergeAttemptWithoutCommitPresence_DoesNotLightDevelop()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/stale-attempt");
        File.WriteAllText(Path.Combine(repo, "stale.txt"), "not merged");
        Commit(repo, "feat: stale attempt");
        var tip = RunGit(repo, "rev-parse task/stale-attempt").Out.Trim();
        var rememberedMerge = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project);
        var job = Job(
            "stale-attempt",
            project: project,
            repo: repo,
            commits: [Commit(tip)],
            prov: Prov(branch: "task/stale-attempt", merge: rememberedMerge));

        var signal = svc.BuildLookup([job])[job.TaskKey];

        Assert.False(signal.InIntegration);
        Assert.Null(signal.IntegrationSha);
    }

    [Fact]
    public void BuildLookup_MergedToDevelopNotMain_LightsDevelopOnly()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        // task/dev is merged into develop with a --no-ff merge commit; main stays put.
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/dev");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var tip = RunGit(repo, "rev-parse task/dev").Out.Trim();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/dev");
        var mergeSha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project);
        var job = Job("dev", project: project, repo: repo,
            commits: new[] { Commit(tip) },
            prov: Prov(branch: "task/dev", merge: mergeSha, transitions: new[]
            {
                new TaskProvenanceTransition { Lane = "3-progress", BranchTip = tip },
            }));

        var signal = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.True(signal.InIntegration);
        Assert.False(signal.InRelease);
        // The attributed commit itself proves membership. The remembered merge
        // commit is not a status input.
        Assert.Equal(tip[..7], signal.IntegrationSha);
    }

    [Fact]
    public void BuildLookup_ReleasedToMain_LightsBothSegments()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/rel");
        File.WriteAllText(Path.Combine(repo, "rel.txt"), "release work");
        Commit(repo, "feat: release work");
        var tip = RunGit(repo, "rev-parse task/rel").Out.Trim();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/rel");
        var mergeSha = RunGit(repo, "rev-parse develop").Out.Trim();
        // Ship develop to main.
        RunGit(repo, "checkout -q main");
        RunGit(repo, "merge --no-ff --no-edit develop");

        var svc = BuildService(repo, out var project);
        var job = Job("rel", project: project, repo: repo,
            commits: new[] { Commit(tip) },
            prov: Prov(branch: "task/rel", merge: mergeSha, transitions: new[]
            {
                new TaskProvenanceTransition { Lane = "3-progress", BranchTip = tip },
            }));

        var signal = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.True(signal.InIntegration);
        Assert.True(signal.InRelease);
        Assert.NotNull(signal.ReleaseSha);
    }

    [Fact]
    public void BuildCommitPresence_ReusesDevelopAndMainReachabilityTruth()
    {
        var repo = SeedDevelopMainRepo(out var mainTip, out _);
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "graph.txt"), "graph work");
        Commit(repo, "feat: graph work");
        var developOnly = RunGit(repo, "rev-parse develop").Out.Trim();

        var service = BuildService(repo, out var project);
        var presence = service.BuildCommitPresence(project, repo, [developOnly, mainTip]);

        Assert.True(presence[developOnly].InIntegration);
        Assert.False(presence[developOnly].InRelease);
        Assert.True(presence[mainTip].InIntegration);
        Assert.True(presence[mainTip].InRelease);
        Assert.Equal(1, service.ComputationCount);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void BuildCommitPresence_SecondHistoryPageWithinTtl_StartsNoAdditionalGitProcess()
    {
        var repo = SeedDevelopMainRepo(out var mainTip, out _);
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "second-page.txt"), "second page");
        Commit(repo, "feat: second history page");
        var developOnly = RunGit(repo, "rev-parse develop").Out.Trim();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T12:00:00Z"));
        var telemetry = new CapturingLogger<BoardMergeStatusService>();
        var service = BuildService(repo, out var project, time, telemetry);

        var firstPage = service.BuildCommitPresence(project, repo, [developOnly]);
        time.Advance(TimeSpan.FromMinutes(1));
        var secondPage = service.BuildCommitPresence(project, repo, [mainTip]);
        var rollups = Rollups(telemetry, "git/commit-presence");

        Assert.True(firstPage[developOnly].InIntegration);
        Assert.True(secondPage[mainTip].InIntegration);
        Assert.True(secondPage[mainTip].InRelease);
        Assert.Equal(2, rollups.Count);
        Assert.True(rollups[0].Spawns > 0);
        Assert.Equal(0, rollups[1].Spawns);
        Assert.Equal(1, service.ComputationCount);
    }

    [Fact]
    public void BuildLookup_SequentialCommitOnDevelop_LightsDevelopWithoutMergeFact()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        // A sequential run with no task branch: the commit lands directly on develop.
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "seq.txt"), "sequential");
        Commit(repo, "feat: sequential work");
        var sha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project);
        // No provenance branch/merge; only the attributed commit SHA.
        var job = Job("seq", project: project, repo: repo, commits: new[] { Commit(sha) });

        var signal = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.True(signal.InIntegration);
        Assert.False(signal.InRelease);
    }

    [Fact]
    public void BuildLookup_MultiRepositoryDirectDelivery_CollapsesEveryRepository()
    {
        var studio = SeedDevelopMainRepo(out _, out _);
        RunGit(studio, "checkout -q develop");
        File.WriteAllText(Path.Combine(studio, "studio.txt"), "studio");
        Commit(studio, "feat: studio direct delivery");
        var studioSha = RunGit(studio, "rev-parse develop").Out.Trim();
        RunGit(studio, "checkout -q main");
        RunGit(studio, "merge -q --ff-only develop");

        var runner = SeedDevelopMainRepo(out _, out _);
        RunGit(runner, "checkout -q main");
        File.WriteAllText(Path.Combine(runner, "runner.txt"), "runner");
        Commit(runner, "feat: runner direct delivery");
        var runnerSha = RunGit(runner, "rev-parse main").Out.Trim();

        var service = BuildMultiRepositoryService(studio, runner, out var project);
        var job = Job("multi-direct", project: project, repo: studio, commits:
        [
            Commit(studioSha) with { Repository = "agent-studio", Branch = "develop" },
            Commit(runnerSha) with { Repository = "runner", Branch = "main" },
        ]);

        var signal = service.BuildLookup([job])[job.TaskKey];

        Assert.True(signal.InIntegration);
        Assert.True(signal.InRelease);
        Assert.Equal("main", signal.Branch);
    }

    [Fact]
    public void BuildLookup_SkipsCardsWithoutAnchor()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        var svc = BuildService(repo, out var project);
        var job = Job("noanchor", project: project, repo: repo);

        var lookup = svc.BuildLookup(new[] { job });

        Assert.False(lookup.ContainsKey(job.TaskKey));
    }

    [Fact]
    public void BuildLookup_WarmHeartbeatBeyondFormerTtl_DoesNotRecomputeGitProjection()
    {
        var repo = SeedDevelopMainRepo(out var mainTip, out _);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z"));
        var svc = BuildService(repo, out var project, time);
        var job = Job("warm", project: project, repo: repo, commits: [Commit(mainTip)]);

        svc.BuildLookup([job]);
        Assert.Equal(1, svc.ComputationCount);

        time.Advance(TimeSpan.FromMinutes(5));
        svc.BuildLookup([job]);

        Assert.Equal(1, svc.ComputationCount);
    }

    [Fact]
    public void BuildLookup_RefMoveInvalidatesWarmProjectionImmediately()
    {
        var repo = SeedDevelopMainRepo(out _, out _);
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/ref-driven");
        File.WriteAllText(Path.Combine(repo, "ref-driven.txt"), "task");
        Commit(repo, "feat: ref driven");
        var tip = RunGit(repo, "rev-parse task/ref-driven").Out.Trim();

        var svc = BuildService(repo, out var project);
        var job = Job("ref-driven", project: project, repo: repo, commits: [Commit(tip)]);
        Assert.False(svc.BuildLookup([job])[job.TaskKey].InIntegration);
        Assert.Equal(1, svc.ComputationCount);

        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/ref-driven");

        Assert.True(svc.BuildLookup([job])[job.TaskKey].InIntegration);
        Assert.Equal(2, svc.ComputationCount);
    }

    [Fact]
    public void BuildLookup_RemoteOnlyOriginHeadFallbackUsesRemoteTrackingBranch()
    {
        var repo = SeedDevelopMainRepo(out var mainTip, out _);
        RunGit(repo, "update-ref refs/remotes/origin/main main");
        RunGit(repo, "symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/main");
        RunGit(repo, "checkout -q --detach main");
        RunGit(repo, "branch -D develop main");

        var svc = BuildService(repo, out var project, configureIntegrationBranch: false);
        var job = Job("remote-only", project: project, repo: repo, commits: [Commit(mainTip)]);

        var signal = svc.BuildLookup([job])[job.TaskKey];

        Assert.True(signal.InIntegration);
        Assert.True(signal.InRelease);
        Assert.Equal("main", signal.IntegrationBranch);
    }

    // --- helpers -----------------------------------------------------------

    private BoardMergeStatusService BuildService(
        string repo,
        out string projectName,
        TimeProvider? timeProvider = null,
        ILogger<BoardMergeStatusService>? logger = null,
        bool configureIntegrationBranch = true)
    {
        projectName = "Fixture";
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        if (configureIntegrationBranch)
            settings.SetIntegrationBranch(projectName, "develop");
        return new BoardMergeStatusService(
            git,
            settings,
            logger ?? NullLogger<BoardMergeStatusService>.Instance,
            timeProvider ?? TimeProvider.System);
    }

    private BoardMergeStatusService BuildMultiRepositoryService(
        string studio,
        string runner,
        out string projectName)
    {
        projectName = "Agent Studio";
        var taskRepository = Path.Combine(_tempDir, "task-store");
        Directory.CreateDirectory(Path.Combine(taskRepository, ".metadata"));
        var projects = new ProjectsFile
        {
            NextProjectIdSeq = 3,
            Projects =
            [
                new ProjectRecord
                {
                    Id = "PROJ-001", DisplayName = projectName, ShortCode = "AGT",
                    WorkspaceId = DefaultWorkspace.Id, StorageLocation = Path.Combine(taskRepository, "studio"),
                    RepositoryPath = studio,
                    Urls = [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = "https://github.com/example/agent-studio.git" }],
                },
                new ProjectRecord
                {
                    Id = "PROJ-002", DisplayName = "Runner", ShortCode = "RUN",
                    WorkspaceId = DefaultWorkspace.Id, StorageLocation = Path.Combine(taskRepository, "runner"),
                    RepositoryPath = runner,
                    Urls = [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = "https://github.com/example/runner.git" }],
                },
            ],
        };
        File.WriteAllText(
            Path.Combine(taskRepository, ".metadata", "projects.json"),
            JsonSerializer.Serialize(projects));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = taskRepository,
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:RootPath"] = studio,
            ["WatchPaths:0:RepositoryPath"] = studio,
            ["WatchPaths:0:Path"] = Path.Combine(studio, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetIntegrationBranch(projectName, "develop");
        settings.SetIntegrationBranch("Runner", "main");
        var integration = new TaskIntegrationStatusService(
            git,
            settings,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<TaskIntegrationStatusService>.Instance,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance));
        return new BoardMergeStatusService(
            git,
            settings,
            NullLogger<BoardMergeStatusService>.Instance,
            integration);
    }

    private static List<(int Spawns, long GitMs, long WallMs, string Breakdown)> Rollups(
        CapturingLogger<BoardMergeStatusService> logger,
        string label)
    {
        return logger.Entries
            .Where(entry => string.Equals(Field(entry, "Label")?.ToString(), label, StringComparison.Ordinal))
            .Select(entry => (
                Convert.ToInt32(Field(entry, "Spawns")),
                Convert.ToInt64(Field(entry, "GitMs")),
                Convert.ToInt64(Field(entry, "WallMs")),
                Field(entry, "Breakdown")?.ToString() ?? ""))
            .ToList();
    }

    private static object? Field(IReadOnlyList<KeyValuePair<string, object?>> state, string key)
    {
        foreach (var field in state)
            if (field.Key == key) return field.Value;
        return null;
    }

    /// <summary>main + a develop branched off it, both with one seed commit.</summary>
    private string SeedDevelopMainRepo(out string mainTip, out string developTip)
    {
        var repo = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        mainTip = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q -b develop");
        developTip = RunGit(repo, "rev-parse develop").Out.Trim();
        RunGit(repo, "checkout -q main");
        return repo;
    }

    private static TaskProvenance Prov(
        string branch = "task/x",
        string? merge = null,
        string? @base = "base000",
        TaskProvenanceTransition[]? transitions = null)
        => new()
        {
            Branch = branch,
            Base = @base,
            Transitions = (transitions ?? Array.Empty<TaskProvenanceTransition>()).ToList(),
            Merge = merge is null ? null : new TaskProvenanceMerge { MergeCommit = merge },
        };

    private static TaskCommitInfo Commit(string sha)
        => new() { Sha = sha, ShortSha = sha.Length > 7 ? sha[..7] : sha, Message = "commit " + sha };

    private static TaskInfo Job(
        string id,
        string project = "Fixture",
        string? repo = null,
        TaskProvenance? prov = null,
        TaskCommitInfo[]? commits = null)
        => new()
        {
            Id = id,
            TaskKey = (repo ?? "watch") + "::" + id,
            State = "6-completed",
            ProjectName = project,
            WatchPath = repo ?? "watch",
            Provenance = prov,
            Commits = (commits ?? Array.Empty<TaskCommitInfo>()).ToList(),
        };

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }
}
