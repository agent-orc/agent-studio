using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2849: the restart harness for a pre-develop build gate that never reached
/// a verdict.
///
/// <para>
/// The incident these tests reproduce: three Task Server restarts each hit a
/// running gate, the gate process died with the backend, no rollback ran, and the
/// un-gated merge stayed on the integration branch. The next integrations were
/// merged on top of it, so one later gate would have tested - and on success
/// published - a stack of merges nobody ever gated.
/// </para>
/// <para>
/// Every test drives the real merge runner and real git against a throwaway repo
/// with a real origin, kills the gate mid-run, and then builds a FRESH recovery
/// service, which is what a restarted process has: durable files and a branch,
/// and no memory of the run that died.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class InterruptedIntegrationGateRecoveryTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;
    private readonly string _origin;

    public InterruptedIntegrationGateRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "interrupted-gate-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        _origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "--bare", "-b", "develop", _origin);
        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Interrupted Gate Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        Git(_repo, "remote", "add", "origin", _origin);
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");
    }

    /// <summary>
    /// The exact incident shape for one card: the gate dies without a verdict,
    /// the merge is left on develop, and the next process must return develop to
    /// its pre-merge tip and hand the card back for another integration.
    /// </summary>
    [Fact]
    public async Task InterruptedGate_LeavesAnUnGatedMerge_AndRestartRecoveryRollsItBack()
    {
        var stack = Build(gateDies: true);
        var folder = SeedDelivery(stack, "one");
        var preMergeTip = Head("develop");

        var merge = await stack.Runner.RunAsync(
            Project, "one", folder, _repo, "develop", CancellationToken.None);

        // The gate never answered, so the merge sits on develop unverified and
        // the durable journal is the only thing that knows about it.
        Assert.Equal(MergeIntoIntegrationOutcome.Error, merge.Outcome);
        Assert.NotEqual(preMergeTip, Head("develop"));
        var journal = IntegrationGateJournal.Read(folder);
        Assert.NotNull(journal);
        Assert.Equal(preMergeTip, journal!.PreMergeTip);
        Assert.Equal(Head("develop"), journal.GatedSha);

        var report = BuildRecovery(stack).RunOnce();

        Assert.Equal(1, report.Branches);
        Assert.Equal(1, report.RolledBack);
        Assert.Equal(1, report.Requeued);
        Assert.Equal(0, report.Escalated);
        Assert.Equal(preMergeTip, Head("develop"));
        Assert.Null(IntegrationGateJournal.Read(folder));

        var step = MergeStep(stack, folder);
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Equal("gate-interrupted", step.Verdict);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateInterrupted, step.FailureCode);

        var timeline = stack.Timeline.ReadAll(folder)
            .Where(entry => entry.Kind == TimelineEventKinds.IntegrationGateInterrupted)
            .ToList();
        var recorded = Assert.Single(timeline);
        Assert.Equal(nameof(InterruptedGateRecoveryAction.RollBack), recorded.Details!["action"]);
        Assert.Equal(preMergeTip, recorded.Details["rollbackSha"]);
    }

    /// <summary>
    /// The card's own symptom: the un-gated merge is in the local graph, so plain
    /// ancestry reads it as integrated. The verdict must be pending, because
    /// nothing was published - and after the rollback it must name the interrupted
    /// gate rather than a conflict.
    /// </summary>
    [Fact]
    public async Task InterruptedGate_NeverProjectsTheCardAsIntegrated()
    {
        var stack = Build(gateDies: true);
        var folder = SeedDelivery(stack, "projection");

        await stack.Runner.RunAsync(
            Project, "projection", folder, _repo, "develop", CancellationToken.None);

        // Merged locally, never pushed: presence in the graph is not integration.
        var duringInterruption = Status(stack, "projection");
        Assert.Equal(IntegrationStatuses.MergedLocally, duringInterruption.Status);

        BuildRecovery(stack).RunOnce();

        var afterRecovery = Status(stack, "projection");
        Assert.Equal(IntegrationStatuses.Pending, afterRecovery.Status);
        Assert.Contains("gate interrupted", afterRecovery.Detail);
        Assert.Equal(
            AcceptedIntegrationFailureCodes.GateInterrupted,
            afterRecovery.Failure?.Code);
        // The retry ladder owns the replay and never starts a new review round.
        Assert.True(GateEnvironmentRetryPolicy.IsGateEnvironmentFailure(afterRecovery));
    }

    /// <summary>
    /// Three restarts in one afternoon stacked three un-gated merges on develop.
    /// Recovery must not roll back only the newest one: the branch goes back to
    /// the OLDEST unverified pre-merge tip, and every affected card is integrated
    /// again on top of the repaired branch.
    /// </summary>
    [Fact]
    public async Task StackedUnGatedMerges_RollBackToTheOldestPreMergeTip()
    {
        var stack = Build(gateDies: true);
        var first = SeedDelivery(stack, "first");
        var second = SeedDelivery(stack, "second");
        var preMergeTip = Head("develop");

        await stack.Runner.RunAsync(Project, "first", first, _repo, "develop", CancellationToken.None);
        var afterFirst = Head("develop");
        await stack.Runner.RunAsync(Project, "second", second, _repo, "develop", CancellationToken.None);

        Assert.NotEqual(preMergeTip, afterFirst);
        Assert.NotEqual(afterFirst, Head("develop"));

        var report = BuildRecovery(stack).RunOnce();

        Assert.Equal(1, report.Branches);
        Assert.Equal(1, report.RolledBack);
        Assert.Equal(2, report.Requeued);
        Assert.Equal(preMergeTip, Head("develop"));
        Assert.Null(IntegrationGateJournal.Read(first));
        Assert.Null(IntegrationGateJournal.Read(second));
        Assert.Equal("gate-interrupted", MergeStep(stack, first).Verdict);
        Assert.Equal("gate-interrupted", MergeStep(stack, second).Verdict);
    }

    /// <summary>
    /// The resume half of the contract. A durable green receipt for exactly this
    /// merge result is a verdict: the process died after the gate answered but
    /// before the record was closed. That merge is verified, so it stays, and only
    /// the integration that never finished publishing is replayed.
    /// </summary>
    [Fact]
    public async Task InterruptedAfterAGreenVerdict_KeepsTheMergeAndOnlyRequeues()
    {
        var stack = Build(gateDies: false);
        var folder = SeedDelivery(stack, "green");

        var merge = await stack.Runner.RunAsync(
            Project, "green", folder, _repo, "develop", CancellationToken.None);
        Assert.True(merge.Outcome.IsSuccessfulIntegration());
        var gatedSha = Head("develop");

        // A crash between "receipt written" and "record closed" leaves exactly
        // this state: the verdict is durable, the record is still open.
        IntegrationGateJournal.Open(folder, new IntegrationGateJournalEntry
        {
            Project = Project,
            JobId = "green",
            RepoRoot = _repo,
            IntegrationBranch = "develop",
            PreMergeTip = Git(_repo, "rev-parse", gatedSha + "^1"),
            GatedSha = gatedSha,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        var report = BuildRecovery(stack).RunOnce();

        Assert.Equal(0, report.RolledBack);
        Assert.Equal(1, report.Requeued);
        Assert.Equal(gatedSha, Head("develop"));
        Assert.Null(IntegrationGateJournal.Read(folder));
    }

    /// <summary>
    /// The refusal. Rolling back below the published tip would un-publish commits
    /// that are already on origin, so recovery escalates instead of rewriting a
    /// branch other machines have seen.
    /// </summary>
    [Fact]
    public async Task AnchorBehindThePublishedTip_EscalatesInsteadOfRewritingOrigin()
    {
        var stack = Build(gateDies: true);
        var folder = SeedDelivery(stack, "published");

        await stack.Runner.RunAsync(
            Project, "published", folder, _repo, "develop", CancellationToken.None);
        var unGated = Head("develop");
        // Somebody published the branch as it stands, un-gated merge included.
        Git(_repo, "push", "-q", "origin", "develop");
        Git(_repo, "fetch", "-q", "origin");

        var report = BuildRecovery(stack).RunOnce();

        Assert.Equal(1, report.Escalated);
        Assert.Equal(0, report.RolledBack);
        Assert.Equal(unGated, Head("develop"));
        var step = MergeStep(stack, folder);
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Equal("error", step.Verdict);
        Assert.Contains("Manual repair is required", step.Reason);
        Assert.Contains("un-publish commits that are already on origin", step.Reason);
        var escalation = Assert.Single(
            stack.Timeline.ReadAll(folder),
            entry => entry.Kind == TimelineEventKinds.IntegrationGateInterrupted);
        Assert.Equal(
            InterruptedIntegrationGatePolicy.Reasons.AnchorBehindPublished,
            escalation.Details!["policyReason"]);
    }

    /// <summary>
    /// The runner refuses to roll back history it did not create, so a gate that
    /// re-verifies an already merged delivery must leave nothing behind that
    /// would invite restart recovery to rewrite that branch either.
    /// </summary>
    [Fact]
    public async Task InterruptedGateOverHistoryThisRunDidNotCreate_LeavesNoRecordToRollBack()
    {
        var stack = Build(gateDies: true);
        var folder = SeedDelivery(stack, "already");
        Git(_repo, "merge", "-q", "--no-ff", "--no-edit", "task/already");
        var alreadyMerged = Head("develop");

        var merge = await stack.Runner.RunAsync(
            Project, "already", folder, _repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, merge.Outcome);
        Assert.Null(IntegrationGateJournal.Read(folder));

        var report = BuildRecovery(stack).RunOnce();

        Assert.Equal(0, report.Branches);
        Assert.Equal(alreadyMerged, Head("develop"));
    }

    // ---- helpers ------------------------------------------------------------

    private TaskIntegrationStatus Status(Stack stack, string id)
    {
        stack.Integration.InvalidateCache();
        var job = stack.Scanner.FindJob(id, _watchPath)!;
        return stack.Integration.BuildLookup([job])[job.TaskKey];
    }

    private static PipelineStepExecution MergeStep(Stack stack, string folder)
        => stack.Pipeline.Read(folder)!.Steps
            .Last(step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);

    /// <summary>
    /// A delivered card in Human Review whose work sits on its own task branch,
    /// exactly as an integrate-on-delivery run leaves it.
    /// </summary>
    private string SeedDelivery(Stack stack, string id)
    {
        Git(_repo, "checkout", "-q", "-b", "task/" + id, "develop");
        File.WriteAllText(Path.Combine(_repo, id + ".txt"), id + "\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "feat: " + id);
        var deliverySha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "develop");

        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id,
                    key = "AGT-2849-" + id,
                    title = id,
                    state = TaskStates.HumanReview,
                    order = 1,
                    agent = "codex",
                    cliType = "codex",
                    mode = TaskModes.Coding,
                    projectName = Project,
                    ownerClientId = DefaultClientIdentity.Id,
                    commit = CommitRecord(deliverySha),
                    commits = new[] { CommitRecord(deliverySha) },
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "- Result: Awaiting acceptance.\n");
        stack.Pipeline.Begin(folder, PipelineCatalogue.Standard, Project, id);
        return folder;
    }

    private string Head(string rev) => Git(_repo, "rev-parse", rev);

    private Stack Build(bool gateDies)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
        }).Build();

        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetIntegrationBranch(Project, "develop");
        settings.SetAutoPushStrategy(Project, AutoPushStrategies.Never);
        settings.SetBuildProfile(Project, new BuildProfile { BuildCmds = ["cd ."] });
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var integration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var worktrees = new IntegrationWorktreeProvider(git);
        var runner = new MergeIntoDevelopRunner(
            git,
            pipeline,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(new DyingBuildTestGateRunner(gateDies)),
            integrationWorktrees: worktrees);
        return new Stack(scanner, git, settings, pipeline, timeline, integration, worktrees, runner);
    }

    /// <summary>
    /// What a restarted process has: the same durable files and the same branch,
    /// and none of the in-memory state of the run that died.
    /// </summary>
    private static InterruptedIntegrationGateRecoveryService BuildRecovery(Stack stack)
        => new(
            stack.Scanner,
            stack.Git,
            stack.Worktrees,
            stack.Pipeline,
            stack.Timeline,
            NullLogger<InterruptedIntegrationGateRecoveryService>.Instance);

    private static object CommitRecord(string sha) => new
    {
        sha,
        shortSha = sha[..8],
        message = "delivery",
        filesChanged = 1,
        files = Array.Empty<object>(),
        at = DateTimeOffset.UtcNow,
        attribution = "automatic",
        confidence = 1,
    };

    /// <summary>
    /// The gate that dies mid-run. Throwing is the in-process equivalent of the
    /// process disappearing: the merge exists, no verdict is produced, and no
    /// rollback runs.
    /// </summary>
    private sealed class DyingBuildTestGateRunner : IBuildTestGateRunner
    {
        private readonly bool _dies;

        public DyingBuildTestGateRunner(bool dies) => _dies = dies;

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            if (_dies) throw new IOException("the gate host disappeared mid-run");
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, 10, string.Empty, "build ok", true, false)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
            });
        }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        GitService Git,
        ProjectSettingsService Settings,
        PipelineExecutionLog Pipeline,
        TimelineLog Timeline,
        TaskIntegrationStatusService Integration,
        IntegrationWorktreeProvider Worktrees,
        MergeIntoDevelopRunner Runner);

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
        catch (Exception ex) { SilentCatch.Note(ex, "Interrupted gate test cleanup is best-effort."); }
    }
}
