using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tags;
using AgentStudio.Tasks;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class WatcherContingentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-contingent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ZeroProposalBudget_BlocksProposalsButNotModelCalls()
    {
        var svc = new WatcherContingentService(NullLogger<WatcherContingentService>.Instance);
        var budgets = new WatcherContingentBudgets { DailyProposalBudget = 0, WeeklyProposalBudget = 0 };
        Assert.False(svc.CanCreateProposal(_root, budgets, DateTime.UtcNow));
        Assert.True(svc.CanCallModel(_root, budgets, DateTime.UtcNow));
    }

    [Fact]
    public void RecordProposal_ConsumesDailyAndWeeklyBudget()
    {
        var svc = new WatcherContingentService(NullLogger<WatcherContingentService>.Instance);
        var budgets = new WatcherContingentBudgets { DailyProposalBudget = 1, WeeklyProposalBudget = 5 };
        var now = DateTime.UtcNow;
        Assert.True(svc.CanCreateProposal(_root, budgets, now));
        svc.RecordProposal(_root, now);
        Assert.False(svc.CanCreateProposal(_root, budgets, now));
    }

    [Fact]
    public void RecordModelCall_ConsumesTokenBudget()
    {
        var svc = new WatcherContingentService(NullLogger<WatcherContingentService>.Instance);
        var budgets = new WatcherContingentBudgets { DailyTokenBudget = 100, WeeklyTokenBudget = 1000, DailyModelCallBudget = 10, WeeklyModelCallBudget = 10 };
        var now = DateTime.UtcNow;
        Assert.True(svc.CanCallModel(_root, budgets, now));
        svc.RecordModelCall(_root, 150, now);
        Assert.False(svc.CanCallModel(_root, budgets, now));
    }

    [Fact]
    public void Snapshot_SurvivesReload()
    {
        var svc = new WatcherContingentService(NullLogger<WatcherContingentService>.Instance);
        var budgets = new WatcherContingentBudgets();
        var now = DateTime.UtcNow;
        svc.RecordProposal(_root, now);
        svc.RecordComment(_root, now);

        var reloaded = new WatcherContingentService(NullLogger<WatcherContingentService>.Instance);
        var snapshot = reloaded.GetSnapshot(_root, budgets, now);
        Assert.Equal(1, snapshot.Day.ProposalsCreated);
        Assert.Equal(1, snapshot.Day.CommentsAppended);
    }
}

/// <summary>
/// Acceptance criterion: "With the contingent set to zero the same replay
/// produces cases but no proposals and no model calls, and the UI shows the
/// backlog."
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class WatcherContingentExhaustionSweepTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watcher-contingent-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public WatcherContingentExhaustionSweepTests()
    {
        _watchPath = Path.Combine(_root, "project-store");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ZeroContingent_StillProducesCases_ButNoProposalsOrModelCalls()
    {
        var values = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["TaskRepository"] = _root,
            ["Watcher:Enabled"] = "true",
            ["Watcher:PersistenceSweepsBeforeProposal"] = "2",
            ["Watcher:Contingent:DailyProposalBudget"] = "0",
            ["Watcher:Contingent:WeeklyProposalBudget"] = "0",
            ["Watcher:Contingent:DailyModelCallBudget"] = "0",
            ["Watcher:Contingent:WeeklyModelCallBudget"] = "0",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new AgentStudio.Registry.ProjectRegistry(configuration, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: timeline);
        var caseStore = new WatcherCaseStore(NullLogger<WatcherCaseStore>.Instance);
        var proposalStore = new WatcherProposalStore(NullLogger<WatcherProposalStore>.Instance);
        var suppressions = new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance);
        var contingent = new WatcherContingentService(NullLogger<WatcherContingentService>.Instance);
        var engine = new WatcherCaseEngine(caseStore, suppressions);
        var tags = new TagRegistryService(NullLogger<TagRegistryService>.Instance, configuration);
        var drafting = new WatcherProposalDraftingService(
            mutations, scanner, timeline, proposalStore, contingent, tags, new ModelRoutingPolicyRegistry(),
            NullLogger<WatcherProposalDraftingService>.Instance);
        var analysis = new WatcherAnalysisService(
            configuration,
            new AgentStudio.Prompts.RuntimePromptService(configuration, NullLogger<AgentStudio.Prompts.RuntimePromptService>.Instance),
            NullLogger<WatcherAnalysisService>.Instance);
        var activity = new WatcherActivityProjector(
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance), scanner, configuration, NullLogger<WatcherActivityProjector>.Instance);

        var fixture = new SingleObservationProbe(new WatcherSignalObservation
        {
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = Project,
            FingerprintKey = "integration-failure:dirty-checkout",
            Summary = "Nine reviewed deliveries blocked by one dirty integration checkout.",
            AffectedCards = ["QS-102"],
        });

        var sweep = new WatcherHostedService(
            [fixture], engine, caseStore, new WatcherEvidencePackBuilder(), analysis, drafting, activity, contingent,
            configuration, NullLogger<WatcherHostedService>.Instance);

        await sweep.RunOnceAsync(WatcherOptions.FromConfiguration(configuration), CancellationToken.None);
        var second = await sweep.RunOnceAsync(WatcherOptions.FromConfiguration(configuration), CancellationToken.None);

        Assert.Equal(1, second.CasesUpdated);
        Assert.Equal(0, second.ProposalsCreated);
        Assert.Equal(0, second.AnalysisCalls);
        Assert.Equal(1, second.ContingentBlocked);

        var cases = caseStore.All(_root);
        var only = Assert.Single(cases);
        // Visible backlog: the case is not silently dropped, and it is not a
        // false "resolved"/"gave-up" terminal - it is still open, waiting for
        // the contingent to reset.
        Assert.Equal(WatcherCaseStates.Open, only.State);
        Assert.Null(only.ProposalId);
        Assert.Empty(proposalStore.All(_root));
    }

    private sealed class SingleObservationProbe(WatcherSignalObservation observation) : IWatcherSignalProbe
    {
        public string Name => "fixture-single";
        public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc) =>
            [observation with { ObservedAtUtc = nowUtc }];
    }
}
