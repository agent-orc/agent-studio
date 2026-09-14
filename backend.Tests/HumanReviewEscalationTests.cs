using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the contract behind the bug
/// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
/// a SYSTEM-initiated move into <c>5e-escalated</c> through
/// <see cref="HumanReviewEscalation"/> ALWAYS leaves the card with an
/// orchestrator verdict (an <see cref="ReviewDecisionKind.Escalate"/> record in
/// the decision journal, which <see cref="TaskEndpointHelpers.BuildOrchestratorVerdictLookup"/>
/// maps to <c>escalate</c>) AND a non-empty <c>status.md</c>. The watchdog-kill
/// case is the acceptance scenario: simulate a kill on a job in 3-progress, run
/// it through the funnel, expect it parked WITH verdict AND status.
/// </summary>
public sealed class HumanReviewEscalationTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;

    public HumanReviewEscalationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-hre-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _states = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(
            _scanner, clients, new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(_config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config, prompts);
        _transitions = new TaskTransitionService(_scanner, _states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var indexCache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(indexCache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    private HumanReviewEscalation BuildFunnel() =>
        new(_states, _transitions, _workspaceRoot, NullLogger<HumanReviewEscalation>.Instance);

    [Fact]
    public async Task EscalateAsync_WatchdogKill_MovesAndRecordsVerdictAndStatus()
    {
        const string jobId = "bug-card-delete-button";
        WriteJob(TaskStates.Progress, jobId);

        var funnel = BuildFunnel();
        var outcome = await funnel.EscalateAsync(
            jobId, _watchPath, ProjectName,
            HumanReviewEscalationCategories.WatchdogKill,
            "CLI exceeded the watchdog deadline");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // (a) folder physically left 3-progress and landed in 5e-escalated.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, jobId)),
            "the card must have left 3-progress");
        var parked = Path.Combine(_watchPath, TaskStates.Escalated, jobId);
        Assert.True(Directory.Exists(parked), "the card must have landed in 5e-escalated");

        // (b) verdict: the decision journal carries an Escalate record whose
        // reason encodes the category, and the endpoint-derived verdict reads
        // it back as "escalate" (not null).
        var records = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName);
        var latest = records.LastOrDefault(r => r.JobId == jobId);
        Assert.NotNull(latest);
        Assert.Equal(ReviewDecisionKind.Escalate, latest!.Kind);
        Assert.Contains(HumanReviewEscalationCategories.WatchdogKill, latest.Reason);

        var jobs = _scanner.ScanAllJobs();
        var moved = jobs.Single(j => j.Id == jobId);
        var verdicts = TaskEndpointHelpers.BuildOrchestratorVerdictLookup(jobs, _config);
        Assert.True(verdicts.TryGetValue(moved.TaskKey, out var verdict));
        Assert.Equal("escalate", verdict);

        // (c) status.md is present and non-empty, with the category + a logs pointer.
        var statusPath = Path.Combine(parked, "status.md");
        Assert.True(File.Exists(statusPath), "status.md stub must be written");
        var status = File.ReadAllText(statusPath);
        Assert.False(string.IsNullOrWhiteSpace(status));
        Assert.Contains(HumanReviewEscalationCategories.WatchdogKill, status);
        Assert.Contains("logs/decisions", status);
    }

    [Fact]
    public async Task EscalateAsync_WithFilesInResults_StatusSaysPartialResultsPresent()
    {
        // A run that died AFTER writing partial deliverables into results/ (the
        // AGT-1917 shape). The escalation stub must surface that instead of the
        // misleading "no agent-written summary" line
        // (docs/concepts/out-of-band-task-completion.md §3, last paragraph).
        const string jobId = "died-with-drafts";
        WriteJob(TaskStates.Progress, jobId);
        var resultsDir = Path.Combine(_watchPath, TaskStates.Progress, jobId, "results");
        Directory.CreateDirectory(resultsDir);
        File.WriteAllText(Path.Combine(resultsDir, "draft.md"), "partial work");

        var funnel = BuildFunnel();
        var outcome = await funnel.EscalateAsync(
            jobId, _watchPath, ProjectName,
            HumanReviewEscalationCategories.WatchdogKill,
            "CLI exceeded the watchdog deadline");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(File.Exists(Path.Combine(_watchPath, TaskStates.Escalated, jobId, "results", "draft.md")),
            $"results/draft.md should have moved with the folder; NewFolderPath={outcome.NewFolderPath}");
        var status = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Escalated, jobId, "status.md"));
        Assert.Contains("partial results are present", status);
    }

    [Fact]
    public void BuildStatusStub_WithoutPartialResults_KeepsNoSummaryLine()
    {
        var stub = HumanReviewEscalation.BuildStatusStub(
            HumanReviewEscalationCategories.WatchdogKill, "deadline", partialResultsPresent: false);
        Assert.Contains("no agent-written summary", stub);
        Assert.DoesNotContain("partial results present", stub);
    }

    /// <summary>
    /// AGT-2220: a park summary that only fits on one line drops the attempt
    /// history. The stub therefore carries an optional detail block, and it must
    /// keep the board's parse contract intact: <c>parseStatusStubEscalation</c>
    /// lifts exactly one <c>- Category:</c> and one <c>- Reason:</c> line back
    /// out, so the block must not introduce a second line of either shape.
    /// </summary>
    [Fact]
    public void BuildStatusStub_WithDetail_KeepsTheSingleCategoryAndReasonContract()
    {
        var chain = ReviewAttemptChainSummary.Build(
        [
            new(
                "review_old", new DateTime(2026, 7, 28, 18, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 28, 18, 20, 0, DateTimeKind.Utc),
                ReviewTerminalOutcome.InfrastructureFailure, "BaselineUnavailable", "Baseline ref missing."),
            new(
                "review_new", new DateTime(2026, 7, 28, 21, 45, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 28, 22, 8, 0, DateTimeKind.Utc),
                ReviewTerminalOutcome.InfrastructureFailure, "ShaMismatch", "Materialized HEAD differs."),
        ]);

        var stub = HumanReviewEscalation.BuildStatusStub(
            HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
            chain.Headline,
            partialResultsPresent: false,
            detail: chain.Detail);

        var lines = stub.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        Assert.Single(lines.Where(line => line.StartsWith("- Category: ", StringComparison.Ordinal)));
        Assert.Single(lines.Where(line => line.StartsWith("- Reason: ", StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.StartsWith("- Newest attempt: ", StringComparison.Ordinal));
        Assert.Contains("ReviewInfra/ShaMismatch", stub);
        Assert.Contains("- Operator options for the newest cause ReviewInfra/ShaMismatch:", stub);
        // The logs pointer stays the last line of the header block, so the detail
        // cannot bury it. AGT-2816 appends the mandatory `## Open Items` section
        // after that block; the pointer must still close the block itself.
        var headerBlock = lines.TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal)).ToList();
        Assert.StartsWith("- See `logs/`", headerBlock.Last(line => line.Length > 0));
        Assert.Contains(ParkedOpenItems.Heading, lines);
    }

    [Fact]
    public void Escalate_Sync_PickupZombie_MovesAndRecordsVerdictAndStatus()
    {
        const string jobId = "zombie-card";
        WriteJob(TaskStates.Progress, jobId);

        var funnel = BuildFunnel();
        var outcome = funnel.Escalate(
            jobId, _watchPath, ProjectName,
            HumanReviewEscalationCategories.PickupZombie,
            "Resume budget exhausted on a session-less folder");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, jobId)));

        var latest = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName).LastOrDefault(r => r.JobId == jobId);
        Assert.NotNull(latest);
        Assert.Equal(ReviewDecisionKind.Escalate, latest!.Kind);
        Assert.Contains(HumanReviewEscalationCategories.PickupZombie, latest.Reason);

        var status = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Escalated, jobId, "status.md"));
        Assert.False(string.IsNullOrWhiteSpace(status));
    }

    [Fact]
    public void RecordVerdictAndStatus_NeverClobbersAnExistingSummary()
    {
        const string jobId = "already-summarised";
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, jobId);
        Directory.CreateDirectory(folder);
        const string realSummary = "# Status\n\n- Result: Implemented the feature and added tests.\n";
        File.WriteAllText(Path.Combine(folder, "status.md"), realSummary);

        var funnel = BuildFunnel();
        funnel.RecordVerdictAndStatus(
            ProjectName, jobId, folder,
            HumanReviewEscalationCategories.UnknownLegacy, "legacy repair");

        // The genuine summary survives; only the verdict half is added.
        Assert.Equal(realSummary, File.ReadAllText(Path.Combine(folder, "status.md")));
        var latest = ReviewDecisionLog.ReadAll(_workspaceRoot, ProjectName).LastOrDefault(r => r.JobId == jobId);
        Assert.NotNull(latest);
        Assert.Equal(ReviewDecisionKind.Escalate, latest!.Kind);
    }

    [Fact]
    public void RecordVerdictAndStatus_ReplacesPendingPlaceholderWithEscalationReason()
    {
        const string jobId = "pending-before-run";
        var folder = Path.Combine(_watchPath, TaskStates.Progress, jobId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");

        var funnel = BuildFunnel();
        funnel.RecordVerdictAndStatus(
            ProjectName, jobId, folder,
            HumanReviewEscalationCategories.RemoteClaimEnvironment,
            "clone failed: 403 agent-orc/website");

        var status = File.ReadAllText(Path.Combine(folder, "status.md"));
        Assert.Contains(HumanReviewEscalationCategories.RemoteClaimEnvironment, status);
        Assert.Contains("clone failed: 403 agent-orc/website", status);
    }

    [Fact]
    public async Task EscalateAsync_RemoteEnvironmentGap_WritesResultScaffold()
    {
        const string jobId = "environment-gap";
        const string reason = "environment preparation failed: not a git repository";
        WriteJob(TaskStates.Progress, jobId);

        var outcome = await BuildFunnel().EscalateAsync(
            jobId,
            _watchPath,
            ProjectName,
            HumanReviewEscalationCategories.RemoteClaimEnvironment,
            reason);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var parked = Path.Combine(_watchPath, TaskStates.Escalated, jobId);
        var statusPath = Path.Combine(parked, "status.md");
        Assert.True(File.Exists(statusPath), "the escalation path must always carry a Result scaffold");
        var status = File.ReadAllText(statusPath);
        Assert.Contains("- Result: Escalated to human decision", status);
        Assert.Contains(HumanReviewEscalationCategories.RemoteClaimEnvironment, status);
        Assert.Contains(reason, status);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            "\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"}");
    }
}
