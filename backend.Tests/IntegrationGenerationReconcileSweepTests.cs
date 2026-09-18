using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2871: nine cards sat in <c>5-human-review</c> for days with a
/// <c>pending</c> / <c>partial</c> badge although their final delivery was on
/// <c>develop</c>. The reconcile pass is what clears them without an operator
/// move: it pays the one git check the board cannot pay for
/// (<c>merge-tree --write-tree</c>), records the verdict on the card, and lets
/// the acceptance rail complete it on its next interval.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class IntegrationGenerationReconcileSweepTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public IntegrationGenerationReconcileSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "integration-reconcile-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Integration Reconcile Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    /// <summary>
    /// The AGT-2810 shape the operator verified by hand: the first round's
    /// salvage commit is not an ancestor of develop, but merging it into develop
    /// yields develop's own tree, so it adds nothing. The pass records that as
    /// <c>content-equal</c> and the card flips to integrated.
    /// </summary>
    [Fact]
    public void Run_SalvageWhoseMergeAddsNothing_IsRecordedAsContentEqualAndReadsIntegrated()
    {
        var stack = Build();
        var (salvage, delivered) = SeedSupersededSalvage("agt-2810");
        var folder = SeedHumanReviewCard(
            stack,
            "agt-2810",
            salvage,
            delivered,
            newerAttempt: null,
            olderFiles: ["feature.txt"],
            newerFiles: ["other.txt"]);

        Assert.Equal(
            IntegrationStatuses.Partial,
            stack.Integration.BuildLookup([Job(stack, "agt-2810")])[TaskKey(stack, "agt-2810")].Status);

        var report = stack.Sweep.Run();

        Assert.Equal(1, report.Examined);
        Assert.Equal(1, report.Cleared);
        Assert.Equal(0, report.Unresolved);
        Assert.Equal(
            CommitIntegrationRules.ContentEqual,
            PersistedCommits(folder).Single(commit => commit.Sha == salvage).IntegrationRule);

        stack.Integration.InvalidateCache();
        var status = stack.Integration.BuildLookup([Job(stack, "agt-2810")])[TaskKey(stack, "agt-2810")];
        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Contains("integrated by content", status.Detail);
    }

    /// <summary>The whole point of the ticket: no operator move is needed afterwards.</summary>
    [Fact]
    public async Task Run_ThenTheAcceptanceRail_CompletesTheReconciledCardWithoutAnOperator()
    {
        var stack = Build();
        var (salvage, delivered) = SeedSupersededSalvage("agt-2827");
        SeedHumanReviewCard(
            stack,
            "agt-2827",
            salvage,
            delivered,
            newerAttempt: null,
            olderFiles: ["feature.txt"],
            newerFiles: ["other.txt"]);

        var beforeRail = await stack.Rail.RunOnceAsync();
        Assert.Equal(0, beforeRail.Accepted);
        Assert.Equal(TaskStates.HumanReview, Job(stack, "agt-2827").State);

        stack.Sweep.Run();
        stack.Integration.InvalidateCache();
        var snapshot = await stack.Rail.RunOnceAsync();

        Assert.Equal(1, snapshot.Accepted);
        Assert.Equal(TaskStates.Completed, stack.Scanner.FindJob("agt-2827", _watchPath)!.State);
    }

    /// <summary>
    /// The constraint: a commit of the CURRENT generation that never landed must
    /// keep blocking. The pass records nothing for it and the card stays partial.
    /// </summary>
    [Fact]
    public void Run_MissingCommitOfTheCurrentGeneration_StaysPartialAndUndecided()
    {
        var stack = Build();
        var missing = CommitOnBranch("task/agt-hole", "hole.txt", "only on the task branch\n");
        var landed = CommitOnDevelop("landed.txt", "landed\n");
        var folder = SeedHumanReviewCard(
            stack,
            "agt-hole",
            missing,
            landed,
            olderAttempt: "run_9",
            newerAttempt: "run_9",
            olderFiles: ["hole.txt"],
            newerFiles: ["landed.txt"]);

        var report = stack.Sweep.Run();

        Assert.Equal(1, report.Examined);
        Assert.Equal(0, report.Cleared);
        Assert.Null(PersistedCommits(folder).Single(commit => commit.Sha == missing).IntegrationRule);

        stack.Integration.InvalidateCache();
        var status = stack.Integration.BuildLookup([Job(stack, "agt-hole")])[TaskKey(stack, "agt-hole")];
        Assert.Equal(IntegrationStatuses.Partial, status.Status);
        Assert.Contains(missing[..7], status.Detail);
    }

    [Fact]
    public void Run_WithNothingStuck_IsANoOp()
    {
        var stack = Build();
        var delivered = CommitOnDevelop("clean.txt", "clean\n");
        SeedHumanReviewCard(stack, "agt-clean", delivered, delivered);

        var report = stack.Sweep.Run();

        Assert.Equal(0, report.Examined);
        Assert.Equal(0, report.MarkedCommits);
    }

    /// <summary>
    /// The one shape only <c>merge-tree --write-tree</c> can decide: a legacy
    /// first-round salvage whose file develop later grew with byte-identical
    /// content, while the card's own later attributed commit touched a
    /// different path. Nothing about generations, changed-file coverage, or
    /// breadth can retire it - but merging it into develop yields develop's own
    /// tree, so it adds nothing.
    /// </summary>
    private (string Salvage, string Delivered) SeedSupersededSalvage(string id)
    {
        var salvage = CommitOnBranch(
            "task/" + id + "-round-1",
            "feature.txt",
            "final content\n",
            message: "wip(runner): salvage before teardown - outcome Done");
        CommitOnDevelop("feature.txt", "final content\n");
        var delivered = CommitOnDevelop("other.txt", "reviewed delivery\n");
        return (salvage, delivered);
    }

    private string CommitOnBranch(string branch, string file, string content, string? message = null)
    {
        Git(_repo, "checkout", "-q", "-b", branch, "develop");
        File.WriteAllText(Path.Combine(_repo, file), content);
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", message ?? "feat: " + branch);
        var sha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");
        return sha;
    }

    private string CommitOnDevelop(string file, string content)
    {
        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, file), content);
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: reviewed delivery " + file);
        return Git(_repo, "rev-parse", "HEAD");
    }

    private string SeedHumanReviewCard(
        Stack stack,
        string id,
        string olderSha,
        string newerSha,
        string? olderAttempt = null,
        string? newerAttempt = "run_3",
        string[]? olderFiles = null,
        string[]? newerFiles = null)
    {
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, id);
        Directory.CreateDirectory(folder);
        var commits = olderSha == newerSha
            ? new[] { CommitJson(newerSha, newerAttempt, newerFiles ?? ["feature.txt"]) }
            :
            [
                CommitJson(olderSha, olderAttempt, olderFiles ?? ["feature.txt"]),
                CommitJson(newerSha, newerAttempt, newerFiles ?? ["feature.txt"]),
            ];
        var card = new
        {
            id,
            key = "AGT-2871",
            title = id,
            state = TaskStates.HumanReview,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
            ownerClientId = DefaultClientIdentity.Id,
            commits,
            commit = commits[^1],
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                card,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        stack.Scanner.InvalidateCache();
        Assert.NotNull(stack.Scanner.FindJob(id, _watchPath));
        return folder;
    }

    private static object CommitJson(string sha, string? runAttemptId, string[] files) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery " + sha[..7],
        runAttemptId,
        filesChanged = files.Length,
        files,
        at = DateTimeOffset.UtcNow,
        attribution = "automatic",
        confidence = 1,
    };

    private static List<TaskCommitInfo> PersistedCommits(string folder)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        return document.RootElement.GetProperty("commits")
            .EnumerateArray()
            .Select(element => JsonSerializer.Deserialize<TaskCommitInfo>(
                element.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
    }

    private TaskInfo Job(Stack stack, string id)
    {
        stack.Scanner.InvalidateCache();
        return stack.Scanner.FindJob(id, _watchPath)!;
    }

    private string TaskKey(Stack stack, string id) => Job(stack, id).TaskKey;

    private Stack Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
            ["AcceptanceRail:Enabled"] = "true",
        }).Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, timeline: timeline);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git,
            settings,
            pipeline,
            NullLogger<TaskIntegrationStatusService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            integrationStatus: integration,
            timeline: timeline,
            pipelineLog: pipeline);
        var rail = new AcceptanceRailHostedService(
            scanner,
            integration,
            transitions,
            new TaskIntegrationRecoveryService(
                scanner,
                mutations,
                states,
                timeline,
                NullLogger<TaskIntegrationRecoveryService>.Instance),
            new HumanReviewEscalation(
                states,
                transitions,
                configuration,
                NullLogger<HumanReviewEscalation>.Instance,
                scanner),
            timeline,
            configuration,
            NullLogger<AcceptanceRailHostedService>.Instance);
        var sweep = new IntegrationGenerationReconcileSweep(
            scanner,
            integration,
            mutations,
            git,
            settings,
            NullLogger<IntegrationGenerationReconcileSweep>.Instance);
        return new Stack(scanner, integration, sweep, rail);
    }

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Reconcile sweep test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        TaskIntegrationStatusService Integration,
        IntegrationGenerationReconcileSweep Sweep,
        AcceptanceRailHostedService Rail);
}
