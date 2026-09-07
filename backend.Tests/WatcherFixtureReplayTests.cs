using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tags;
using AgentStudio.Tasks;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Replays the eight 06.09.2026 findings from orchestrator-waechter §10.1 as
/// the W1 fixture matrix and asserts the W2 acceptance criterion: eight
/// cases, eight proposals in the proposal state, none in Ready, none
/// mutating anything outside proposal creation.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class WatcherFixtureReplayTests : IDisposable
{
    private const string Project = "Fixture";
    private readonly string _root;
    private readonly string _watchPath;

    public WatcherFixtureReplayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "watcher-fixtures-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ReplayingEveningFixtures_ProducesEightCasesAndEightProposals_NoneInReady()
    {
        var stack = Build();
        var fixture = new FixtureProbe(EveningFixtures());
        var sweep = new WatcherHostedService(
            [fixture],
            stack.Engine,
            stack.CaseStore,
            new WatcherEvidencePackBuilder(),
            stack.Analysis,
            stack.Drafting,
            stack.Activity,
            stack.Contingent,
            stack.Configuration,
            NullLogger<WatcherHostedService>.Instance);

        // §10.3: a case must persist across two sweeps before W2 drafts a
        // proposal. Replaying the same evidence twice models "the same thing
        // was still true five minutes later".
        await sweep.RunOnceAsync(stack.Options, CancellationToken.None);
        var second = await sweep.RunOnceAsync(stack.Options, CancellationToken.None);

        Assert.Equal(8, second.CasesUpdated);
        Assert.Equal(8, second.ProposalsCreated);
        Assert.Equal(0, second.ContingentBlocked);

        var cases = stack.CaseStore.All(_root);
        Assert.Equal(8, cases.Count);
        Assert.All(cases, c => Assert.Equal(WatcherCaseStates.DecisionRequired, c.State));
        Assert.All(cases, c => Assert.NotNull(c.ProposalId));
        Assert.All(cases, c => Assert.NotNull(c.EvidenceDigest));

        // All five detector classes appear (§10.2's five classes over the eight findings).
        Assert.Equal(
            WatcherDetectorClasses.All.OrderBy(x => x),
            cases.Select(c => c.DetectorClass).Distinct().OrderBy(x => x));

        var proposals = stack.ProposalStore.All(_root);
        Assert.Equal(8, proposals.Count);
        Assert.All(proposals, p => Assert.False(p.IsComment));
        Assert.All(proposals, p => Assert.NotNull(p.JobId));
        Assert.All(proposals, p => Assert.Contains("watcher-proposal", p.Tags));

        foreach (var proposal in proposals)
        {
            var job = stack.Scanner.FindJob(proposal.JobId!, _watchPath);
            Assert.NotNull(job);
            Assert.Equal(TaskStates.Preparation, job!.State);
            Assert.NotEqual(TaskStates.Ready, job.State);
            Assert.Contains("watcher-proposal", job.Tags);
            Assert.Contains($"watcher-{proposal.DetectorClass}", job.Tags);
        }
    }

    [Fact]
    public async Task SingleSweep_DoesNotYetProposeAndRaisesFindingOnce()
    {
        var stack = Build();
        var fixture = new FixtureProbe(EveningFixtures().Take(1).ToList());
        var sweep = new WatcherHostedService(
            [fixture], stack.Engine, stack.CaseStore, new WatcherEvidencePackBuilder(), stack.Analysis,
            stack.Drafting, stack.Activity, stack.Contingent, stack.Configuration, NullLogger<WatcherHostedService>.Instance);

        var first = await sweep.RunOnceAsync(stack.Options, CancellationToken.None);
        Assert.Equal(1, first.CasesUpdated);
        Assert.Equal(0, first.ProposalsCreated);

        var cases = stack.CaseStore.All(_root);
        var only = Assert.Single(cases);
        Assert.Equal(WatcherCaseStates.Open, only.State);
        Assert.Equal(1, only.SweepCount);
    }

    [Fact]
    public async Task RestartingCaseStore_PreservesFingerprint()
    {
        var stack = Build();
        var fixture = new FixtureProbe(EveningFixtures().Take(1).ToList());
        var sweep1 = new WatcherHostedService(
            [fixture], stack.Engine, stack.CaseStore, new WatcherEvidencePackBuilder(), stack.Analysis,
            stack.Drafting, stack.Activity, stack.Contingent, stack.Configuration, NullLogger<WatcherHostedService>.Instance);
        await sweep1.RunOnceAsync(stack.Options, CancellationToken.None);

        // Simulate a process restart: a fresh store instance re-reads from disk.
        var restartedStore = new WatcherCaseStore(NullLogger<WatcherCaseStore>.Instance);
        var restartedEngine = new WatcherCaseEngine(restartedStore, stack.Suppressions);
        var sweep2 = new WatcherHostedService(
            [fixture], restartedEngine, restartedStore, new WatcherEvidencePackBuilder(), stack.Analysis,
            stack.Drafting, stack.Activity, stack.Contingent, stack.Configuration, NullLogger<WatcherHostedService>.Instance);
        var second = await sweep2.RunOnceAsync(stack.Options, CancellationToken.None);

        // Same fingerprint, second sweep after "restart": exactly one durable
        // case (deduplicated), now at sweep count 2, ready for a proposal.
        Assert.Equal(1, second.CasesUpdated);
        Assert.Single(restartedStore.All(_root));
        Assert.Equal(2, restartedStore.All(_root).Single().SweepCount);
    }

    private Stack Build()
    {
        var values = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["TaskRepository"] = _root,
            ["Watcher:Enabled"] = "true",
            ["Watcher:PersistenceSweepsBeforeProposal"] = "2",
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
        var modelRouting = new ModelRoutingPolicyRegistry();
        var drafting = new WatcherProposalDraftingService(
            mutations, scanner, timeline, proposalStore, contingent, tags, modelRouting,
            NullLogger<WatcherProposalDraftingService>.Instance);
        var analysis = new WatcherAnalysisService(
            configuration,
            new AgentStudio.Prompts.RuntimePromptService(configuration, NullLogger<AgentStudio.Prompts.RuntimePromptService>.Instance),
            NullLogger<WatcherAnalysisService>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var activity = new WatcherActivityProjector(orchestratorLog, scanner, configuration, NullLogger<WatcherActivityProjector>.Instance);

        return new Stack(configuration, scanner, mutations, timeline, caseStore, proposalStore, suppressions, contingent, engine, drafting, analysis, activity, WatcherOptions.FromConfiguration(configuration));
    }

    private sealed record Stack(
        IConfiguration Configuration,
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        TimelineLog Timeline,
        WatcherCaseStore CaseStore,
        WatcherProposalStore ProposalStore,
        WatcherSuppressionStore Suppressions,
        WatcherContingentService Contingent,
        WatcherCaseEngine Engine,
        WatcherProposalDraftingService Drafting,
        WatcherAnalysisService Analysis,
        WatcherActivityProjector Activity,
        WatcherOptions Options);

    private sealed class FixtureProbe(IReadOnlyList<WatcherSignalObservation> observations) : IWatcherSignalProbe
    {
        public string Name => "fixture";
        public IReadOnlyList<WatcherSignalObservation> Collect(string workspaceRoot, DateTime nowUtc) =>
            observations.Select(o => o with { ObservedAtUtc = nowUtc }).ToList();
    }

    /// <summary>
    /// The eight findings of orchestrator-waechter §10.1, as the exact
    /// signals a live probe would have collected that evening.
    /// </summary>
    private static List<WatcherSignalObservation> EveningFixtures() =>
    [
        new()
        {
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = QuotaProbeSignalProbe.WorkspaceProject,
            FingerprintKey = "quota-probe:claude",
            Summary = "Claude launcher stub quota probe has failed on every cycle for 8h.",
            AffectedCards = ["AGT-2705", "AGT-2706"],
            Details = new() { ["probeFailedAt"] = "2026-09-06T10:00:00Z", ["cliVersion"] = "1.2.3" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Silence,
            Project = Project,
            FingerprintKey = "runner-capability-snapshot-missing",
            Summary = "No runner capability snapshot for over 5 minutes while Ready cards target the runner.",
            AffectedCards = ["AGT-2711", "AGT-2712"],
            Details = new() { ["lastSnapshotAgeMinutes"] = "5760" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Contradiction,
            Project = Project,
            FingerprintKey = "AGT-2713:crash-empty-completion",
            Summary = "AGT-2713 recorded a CliCrash outcome followed by an external completion whose result SHA equals the base SHA.",
            AffectedCards = ["AGT-2713"],
            Details = new() { ["typedOutcome"] = "CliCrash", ["resultSha"] = "abc123", ["baseSha"] = "abc123" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = Project,
            FingerprintKey = "review-attempts-no-state-change",
            Summary = "412 remote reviews on one card (284 in one day), all Pass, integration status unchanged.",
            AffectedCards = ["AGT-2717", "AGT-2720"],
            Details = new() { ["reviewAttempts"] = "412", ["outcome"] = "Pass" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Repetition,
            Project = Project,
            FingerprintKey = "integration-failure:dirty-checkout",
            Summary = "Nine reviewed deliveries blocked by one dirty integration checkout for six days.",
            AffectedCards = ["QS-102"],
            Details = new() { ["failureReason"] = "refusing to fast-forward: dirty files" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Drift,
            Project = Project,
            FingerprintKey = "gate-toolchain-cache-corrupted",
            Summary = "Pre-main gate fails before the first test after a cache hit; corrupted dependency cache.",
            AffectedCards = ["AGT-2720"],
            Details = new() { ["gateTranscript"] = "cache hit; toolchain startup error" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Contradiction,
            Project = Project,
            FingerprintKey = "escalation-banner-vs-review-artifacts",
            Summary = "Escalation banner shows '0 rounds, grade not recorded' on a card with seven review reports.",
            AffectedCards = ["AGT-2714", "AGT-2717"],
            Details = new() { ["artifactCount"] = "7", ["projectedCount"] = "0" },
        },
        new()
        {
            DetectorClass = WatcherDetectorClasses.Hygiene,
            Project = Project,
            FingerprintKey = "dossier-descriptors-invalid",
            Summary = "Fifteen invalid dossier descriptors have stood unrepaired for over the grace period.",
            AffectedCards = [],
            Details = new() { ["invalidCount"] = "15" },
        },
    ];
}
