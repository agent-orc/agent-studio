using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The W1 plus W2 acceptance rail: replaying the eight findings of
/// 6 September 2026 must produce eight cases and eight proposals in the
/// proposal state, a zero contingent must produce cases without proposals or
/// model calls, and nothing outside proposal creation and comments may move.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class WatcherSweepAcceptanceTests : IDisposable
{
    private const string Project = "Agent Studio";
    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public WatcherSweepAcceptanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "watcher-sweep-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        RunGit(_root, "init", "-q", "-b", "develop", _repo);
        RunGit(_repo, "config", "user.email", "test@example.com");
        RunGit(_repo, "config", "user.name", "Watcher Test");
        File.WriteAllText(Path.Combine(_repo, "base.txt"), "base\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-q", "-m", "seed");
    }

    // ---- Acceptance 1: the fixture replay ------------------------------------

    [Fact]
    public async Task Replay_ProducesEightCasesAndEightProposalsInTheProposalState()
    {
        var stack = Build();

        var result = await ReplayTwiceAsync(stack);

        Assert.Equal(8, result.Cases.Count);
        Assert.Equal(8, stack.Store.Proposals().Count);
        Assert.All(stack.Store.Proposals(), proposal =>
        {
            Assert.Equal(WatcherProposalKinds.NewCard, proposal.Kind);
            Assert.NotNull(proposal.CreatedTaskKey);
            Assert.NotEmpty(proposal.Evidence);
            Assert.NotEmpty(proposal.Recommendation.Model);
            Assert.NotEmpty(proposal.Recommendation.Tier);
            Assert.Equal(WatcherProposalDecisions.Pending, proposal.Decision.State);
        });
    }

    [Fact]
    public async Task Replay_LeavesEveryProposedCardInPreparationAndNoneInReady()
    {
        var stack = Build();

        await ReplayTwiceAsync(stack);

        var cards = ProposalCards(stack);
        Assert.Equal(8, cards.Count);
        Assert.All(cards, card =>
        {
            Assert.Equal(TaskStates.Preparation, card.State);
            Assert.Contains(WatcherTags.Proposal, card.Tags, StringComparer.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(
            stack.Scanner.ScanAllAutomationJobs(),
            card => card.State == TaskStates.Ready);
    }

    [Fact]
    public async Task Replay_TagsEveryCardWithItsDetectorClassAndFingerprint()
    {
        var stack = Build();

        await ReplayTwiceAsync(stack);

        foreach (var proposal in stack.Store.Proposals())
        {
            var card = Assert.Single(
                ProposalCards(stack),
                item => item.TaskKey == proposal.CreatedTaskKey);
            Assert.Contains(
                WatcherTags.DetectorClass(proposal.DetectorClass), card.Tags, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(
                WatcherTags.Fingerprint(proposal.FingerprintDigest), card.Tags, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Replay_WritesTheHousePromptSectionsAndTheEvidenceIntoEveryCard()
    {
        var stack = Build();

        await ReplayTwiceAsync(stack);

        foreach (var card in ProposalCards(stack))
        {
            var prompt = File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md"));
            Assert.Contains("## Context", prompt, StringComparison.Ordinal);
            Assert.Contains("## Changes", prompt, StringComparison.Ordinal);
            Assert.Contains("## Acceptance", prompt, StringComparison.Ordinal);
            Assert.Contains("Detector rule:", prompt, StringComparison.Ordinal);
            Assert.Contains("Evidence pack digest:", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Replay_LeavesAnAuditLineOnEveryProposedCard()
    {
        var stack = Build();

        await ReplayTwiceAsync(stack);

        foreach (var card in ProposalCards(stack))
        {
            var audit = Assert.Single(
                stack.Timeline.ReadAll(card.FolderPath),
                entry => entry.Kind == TimelineEventKinds.WatcherProposed);
            Assert.NotNull(audit.Details);
            Assert.False(string.IsNullOrWhiteSpace(audit.Details!["caseId"]));
            Assert.False(string.IsNullOrWhiteSpace(audit.Details["fingerprint"]));
            Assert.False(string.IsNullOrWhiteSpace(audit.Details["evidenceDigest"]));
            Assert.False(string.IsNullOrWhiteSpace(audit.Details["detectorRule"]));
        }
    }

    [Fact]
    public async Task FirstSweep_OpensCasesButProposesNothingBeforeThePersistenceCheck()
    {
        var stack = Build();

        var first = await stack.Sweep.SweepAsync(WatcherFixtureMatrix.Combined());

        Assert.Equal(8, first.Cases.Count);
        Assert.Equal(0, first.ProposalsCreated);
        Assert.Empty(stack.Store.Proposals());
        Assert.All(first.Cases, item => Assert.Equal(1, item.SweepCount));
    }

    // ---- Acceptance 2: the zero contingent -----------------------------------

    [Fact]
    public async Task ZeroContingent_ProducesCasesButNoProposalsAndNoModelCalls()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);

        var result = await ReplayTwiceAsync(stack);

        Assert.Equal(8, result.Cases.Count);
        Assert.Empty(stack.Store.Proposals());
        Assert.Equal(0, stack.Analyst.Calls);
        Assert.Empty(ProposalCards(stack));
        Assert.NotNull(result.ContingentBlockedReason);
    }

    [Fact]
    public async Task ZeroContingent_ShowsTheUnanalysedBacklog()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);

        await ReplayTwiceAsync(stack);
        var contingent = stack.Sweep.Contingent(WatcherFixtureMatrix.NowUtc);

        Assert.True(contingent.ProposalsBlocked);
        Assert.True(contingent.ModelCallsBlocked);
        Assert.Equal(8, contingent.BacklogCases);
        Assert.NotEmpty(contingent.ExhaustedDimensions);
        Assert.All(
            stack.Store.Cases(),
            item => Assert.Equal(WatcherCaseStates.Backlogged, item.State));
    }

    [Fact]
    public async Task ZeroContingent_KeepsCountingSoTheCasesStayComplete()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);

        await ReplayTwiceAsync(stack);

        // Counting must not stop when spending does: an operator who raises the
        // budget tomorrow needs the same evidence, not a restarted count.
        Assert.All(stack.Store.Cases(), item =>
        {
            Assert.Equal(2, item.SweepCount);
            Assert.NotEmpty(item.Evidence);
            Assert.NotEmpty(item.EvidenceDigest);
        });
    }

    [Fact]
    public async Task ProposalBudgetOfOne_ProposesOnceAndBacklogsTheRest()
    {
        var stack = Build(contingent: WatcherContingentLimits.Default with
        {
            ProposalsPerDay = 1,
            ProposalsPerWeek = 1,
        });

        await ReplayTwiceAsync(stack);

        Assert.Single(stack.Store.Proposals());
        Assert.Single(ProposalCards(stack));
        Assert.Equal(7, stack.Store.Cases().Count(item => item.State == WatcherCaseStates.Backlogged));
    }

    // ---- Model economy -------------------------------------------------------

    [Fact]
    public async Task Analysis_RunsOnlyForCasesThatDeclaredAnUncertainCause()
    {
        var stack = Build();

        await ReplayTwiceAsync(stack);

        // Seven of the eight findings declare an uncertain cause. The hygiene
        // case does not, because its validator already named the cause.
        Assert.Equal(7, stack.Analyst.Calls);
        var hygiene = Assert.Single(
            stack.Store.Proposals(),
            proposal => proposal.DetectorClass == WatcherDetectorClasses.Hygiene);
        Assert.Empty(hygiene.ModelCalls);
    }

    [Fact]
    public async Task Analysis_IsSkippedEntirelyWhenTheOperatorDisabledIt()
    {
        var stack = Build(analysisEnabled: false);

        await ReplayTwiceAsync(stack);

        Assert.Equal(0, stack.Analyst.Calls);
        Assert.Equal(8, stack.Store.Proposals().Count);
        Assert.All(stack.Store.Proposals(), proposal => Assert.Empty(proposal.ModelCalls));
    }

    [Fact]
    public async Task Analysis_BooksEveryModelCallIntoTheLedger()
    {
        var stack = Build();

        await ReplayTwiceAsync(stack);

        var calls = stack.Store.Ledger()
            .Where(entry => entry.Kind == WatcherContingentKinds.ModelCall)
            .ToList();
        Assert.Equal(7, calls.Count);
        Assert.All(calls, entry => Assert.True(entry.Tokens > 0));
    }

    [Fact]
    public async Task Analysis_StoppedByTheTokenCeilingLeavesTheProposalOnEvidenceAlone()
    {
        var stack = Build(contingent: WatcherContingentLimits.Default with
        {
            TokensPerDay = 0,
            TokensPerWeek = 0,
        });

        await ReplayTwiceAsync(stack);

        Assert.Equal(0, stack.Analyst.Calls);
        // Proposals still land: detection alone already justifies the ticket.
        Assert.Equal(5, stack.Store.Proposals().Count);
    }

    // ---- Deduplication, restart, terminals -----------------------------------

    [Fact]
    public async Task RepeatedSweeps_DeduplicateOntoOneCasePerFingerprint()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);

        for (var sweep = 0; sweep < 5; sweep++)
            await stack.Sweep.SweepAsync(WatcherFixtureMatrix.Combined());

        Assert.Equal(8, stack.Store.Cases().Count);
        Assert.Equal(
            8,
            stack.Store.Cases().Select(item => item.Fingerprint).Distinct(StringComparer.Ordinal).Count());
        Assert.All(stack.Store.Cases(), item => Assert.Equal(5, item.SweepCount));
    }

    [Fact]
    public async Task IdleSweeps_DoNotGrowTheAppendOnlyCaseFile()
    {
        // The sweep runs every five minutes forever. Rewriting settled cases on
        // every cycle would grow the case file without adding one fact.
        var stack = Build(contingent: WatcherContingentLimits.Zero);
        var fixture = WatcherFixtureMatrix.ById("dossier-descriptor-hygiene").Input;
        await stack.Sweep.SweepAsync(fixture);
        await stack.Sweep.SweepAsync(fixture);
        await stack.Sweep.SweepAsync(new WatcherSweepInput { NowUtc = WatcherFixtureMatrix.NowUtc });
        var settled = Lines(WatcherStore.CasesFileName);

        for (var sweep = 0; sweep < 5; sweep++)
            await stack.Sweep.SweepAsync(new WatcherSweepInput { NowUtc = WatcherFixtureMatrix.NowUtc });

        Assert.Equal(settled, Lines(WatcherStore.CasesFileName));
    }

    [Fact]
    public async Task ARepeatSweep_WritesOnlyTheCasesItChanged()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);
        var fixture = WatcherFixtureMatrix.Combined();

        await stack.Sweep.SweepAsync(fixture);

        // Eight cases opened, so exactly eight lines were appended.
        Assert.Equal(8, Lines(WatcherStore.CasesFileName));
    }

    [Fact]
    public async Task TheCaseFile_IsCompactedInsteadOfGrowingForever()
    {
        // The sweep advances a case counter every five minutes forever. Without
        // compaction the file would grow without holding one additional fact.
        var stack = Build(contingent: WatcherContingentLimits.Zero);
        var fixture = WatcherFixtureMatrix.Combined();

        for (var sweep = 0; sweep < 80; sweep++)
            await stack.Sweep.SweepAsync(fixture);

        var lines = Lines(WatcherStore.CasesFileName);
        Assert.True(lines < WatcherStore.CompactionThreshold + 8, $"case file held {lines} lines");
        // Compaction must not lose a case or its counters.
        Assert.Equal(8, stack.Store.Cases().Count);
        Assert.All(stack.Store.Cases(), item => Assert.Equal(80, item.SweepCount));
    }

    [Fact]
    public async Task ACompactedCaseFile_StillSurvivesARestart()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);
        var fixture = WatcherFixtureMatrix.Combined();
        for (var sweep = 0; sweep < 80; sweep++)
            await stack.Sweep.SweepAsync(fixture);

        var restarted = new WatcherStore(stack.Configuration, NullLogger<WatcherStore>.Instance);

        Assert.Equal(
            stack.Store.Cases().Select(item => (item.Id, item.SweepCount, item.State)),
            restarted.Cases().Select(item => (item.Id, item.SweepCount, item.State)));
    }

    [Fact]
    public async Task Cases_SurviveARestartOfTheStore()
    {
        var stack = Build();
        await ReplayTwiceAsync(stack);
        var before = stack.Store.Cases();
        var proposalsBefore = stack.Store.Proposals();

        // A fresh store over the same workspace is what a restart looks like.
        var restarted = new WatcherStore(stack.Configuration, NullLogger<WatcherStore>.Instance);

        Assert.Equal(
            before.Select(item => (item.Id, item.State, item.SweepCount, item.ProposalId)),
            restarted.Cases().Select(item => (item.Id, item.State, item.SweepCount, item.ProposalId)));
        Assert.Equal(
            proposalsBefore.Select(item => item.Id),
            restarted.Proposals().Select(item => item.Id));
        Assert.Equal(stack.Store.Ledger().Count, restarted.Ledger().Count);
    }

    [Fact]
    public async Task Cases_ReachAnExplicitTerminalWhenTheirSignalStops()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);
        await stack.Sweep.SweepAsync(WatcherFixtureMatrix.Combined());

        // A later sweep that still collects signals, but no longer this one.
        var only = WatcherFixtureMatrix.ById("dossier-descriptor-hygiene").Input;
        await stack.Sweep.SweepAsync(only);

        var cases = stack.Store.Cases();
        var stopped = cases.Where(item => item.DetectorClass != WatcherDetectorClasses.Hygiene).ToList();
        Assert.Equal(7, stopped.Count);
        Assert.All(stopped, item =>
        {
            Assert.Equal(WatcherCaseStates.Resolved, item.State);
            Assert.Equal(WatcherCasePolicy.TerminalReasons.SignalStopped, item.TerminalReason);
        });
    }

    [Fact]
    public async Task ABlindSweep_ClosesNothing()
    {
        var stack = Build(contingent: WatcherContingentLimits.Zero);
        await stack.Sweep.SweepAsync(WatcherFixtureMatrix.Combined());

        // Collection produced nothing at all. That is not evidence of health.
        await stack.Sweep.SweepAsync(new WatcherSweepInput { NowUtc = WatcherFixtureMatrix.NowUtc });

        Assert.All(
            stack.Store.Cases(),
            item => Assert.False(WatcherCaseStates.IsTerminal(item.State)));
    }

    // ---- Comment instead of a duplicate card ---------------------------------

    [Fact]
    public async Task AFingerprintWithAnOpenCard_GetsACommentAndNotASecondCard()
    {
        var stack = Build();
        var fixture = WatcherFixtureMatrix.ById("dossier-descriptor-hygiene");
        await stack.Sweep.SweepAsync(fixture.Input);
        await stack.Sweep.SweepAsync(fixture.Input);
        var firstProposal = Assert.Single(stack.Store.Proposals());
        var cardsAfterFirst = ProposalCards(stack).Count;

        // Force the same fingerprint back into an eligible state, as a reopened
        // case would after the operator rejected and the suppression expired.
        var reopened = stack.Store.Case(firstProposal.CaseId)! with
        {
            State = WatcherCaseStates.Open,
            ProposalId = null,
        };
        stack.Store.Upsert(reopened);
        await stack.Sweep.SweepAsync(fixture.Input);

        Assert.Equal(cardsAfterFirst, ProposalCards(stack).Count);
        var comment = Assert.Single(
            stack.Store.Proposals(),
            proposal => proposal.Kind == WatcherProposalKinds.Comment);
        Assert.Equal(firstProposal.CreatedTaskKey, comment.CommentedOnTaskKey);

        var card = Assert.Single(ProposalCards(stack));
        var prompt = File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md"));
        Assert.Contains("The Watcher saw fingerprint", prompt, StringComparison.Ordinal);
    }

    // ---- Acceptance 3: no mutation outside proposals and comments -------------

    [Fact]
    public async Task Replay_TouchesNoExistingCardAndNoGitState()
    {
        var stack = Build();
        var existing = SeedCard(stack, "pre-existing", TaskStates.Ready);
        var headBefore = Git(_repo, "rev-parse", "HEAD");
        var branchesBefore = Git(_repo, "branch", "--all");

        await ReplayTwiceAsync(stack);

        var after = stack.Scanner.FindJob(existing, _watchPath)!;
        Assert.Equal(TaskStates.Ready, after.State);
        Assert.Empty(stack.Timeline.ReadAll(after.FolderPath));
        Assert.Equal(headBefore, Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(branchesBefore, Git(_repo, "branch", "--all"));
        Assert.Equal("", Git(_repo, "status", "--porcelain"));
    }

    // ---- Helpers -------------------------------------------------------------

    private async Task<WatcherSweepResult> ReplayTwiceAsync(Stack stack)
    {
        await stack.Sweep.SweepAsync(WatcherFixtureMatrix.Combined());
        return await stack.Sweep.SweepAsync(WatcherFixtureMatrix.Combined());
    }

    private int Lines(string fileName)
    {
        var path = Path.Combine(WatcherStore.Folder(_root), fileName);
        return File.Exists(path)
            ? File.ReadAllLines(path).Count(line => !string.IsNullOrWhiteSpace(line))
            : 0;
    }

    private static List<TaskInfo> ProposalCards(Stack stack) => stack.Scanner
        .ScanAllAutomationJobs()
        .Where(card => card.Tags.Contains(WatcherTags.Proposal, StringComparer.OrdinalIgnoreCase))
        .OrderBy(card => card.TaskKey, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private string SeedCard(Stack stack, string id, string state)
    {
        var created = stack.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = id,
            Title = "Pre-existing card",
            WatchPath = _watchPath,
            TargetState = state,
            PromptMarkdown = "Untouched.",
        });
        Assert.NotNull(created);
        return created!;
    }

    internal Stack Build(
        WatcherContingentLimits? contingent = null,
        bool analysisEnabled = true,
        int maxProposalsPerSweep = 50)
    {
        var limits = contingent ?? WatcherContingentLimits.Default with
        {
            ProposalsPerDay = 50,
            ProposalsPerWeek = 50,
            CommentsPerDay = 50,
            CommentsPerWeek = 50,
        };
        var values = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["TaskRepository"] = _root,
            ["Watcher:Enabled"] = "true",
            ["Watcher:AnalysisEnabled"] = analysisEnabled ? "true" : "false",
            ["Watcher:MaxProposalsPerSweep"] = Text(maxProposalsPerSweep),
            ["Watcher:ProposalProject"] = Project,
            ["Watcher:Contingent:ModelCallsPerDay"] = Text(limits.ModelCallsPerDay),
            ["Watcher:Contingent:ModelCallsPerWeek"] = Text(limits.ModelCallsPerWeek),
            ["Watcher:Contingent:TokensPerDay"] = Text(limits.TokensPerDay),
            ["Watcher:Contingent:TokensPerWeek"] = Text(limits.TokensPerWeek),
            ["Watcher:Contingent:ProposalsPerDay"] = Text(limits.ProposalsPerDay),
            ["Watcher:Contingent:ProposalsPerWeek"] = Text(limits.ProposalsPerWeek),
            ["Watcher:Contingent:CommentsPerDay"] = Text(limits.CommentsPerDay),
            ["Watcher:Contingent:CommentsPerWeek"] = Text(limits.CommentsPerWeek),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

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
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance,
            integrationStatus: integration,
            timeline: timeline,
            pipelineLog: pipeline);

        var busStore = new AgentMessageBusStore();
        var store = new WatcherStore(configuration, NullLogger<WatcherStore>.Instance);
        var bridge = new AgentMessageBusBridge(
            busStore, configuration, NullLogger<AgentMessageBusBridge>.Instance);
        var publisher = new WatcherBusPublisher(
            busStore, store, NullLogger<WatcherBusPublisher>.Instance, bridge);
        var routing = new ModelRoutingPolicyRegistry();
        var analyst = new CountingAnalyst();
        var proposals = new WatcherProposalService(
            scanner, mutations, timeline, store, publisher, routing,
            new FixedRoutingMode(false), analyst,
            NullLogger<WatcherProposalService>.Instance);
        var review = new WatcherReviewService(
            store, scanner, mutations, transitions, timeline, publisher, configuration,
            NullLogger<WatcherReviewService>.Instance);
        var collector = new WatcherSignalCollector([], NullLogger<WatcherSignalCollector>.Instance);
        var sweep = new WatcherSweepService(
            collector, store, proposals, publisher, configuration,
            NullLogger<WatcherSweepService>.Instance);
        var projection = new WatcherActivityProjection(
            busStore, store, NullLogger<WatcherActivityProjection>.Instance);

        return new Stack(
            configuration, scanner, mutations, timeline, store, sweep, review, analyst, projection, busStore);
    }

    internal sealed record Stack(
        IConfiguration Configuration,
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        TimelineLog Timeline,
        WatcherStore Store,
        WatcherSweepService Sweep,
        WatcherReviewService Review,
        CountingAnalyst Analyst,
        WatcherActivityProjection Projection,
        AgentMessageBusStore BusStore);

    /// <summary>Counts calls so "no model call" is provable, and never spawns a CLI.</summary>
    internal sealed class CountingAnalyst : IWatcherAnalyst
    {
        private int _calls;

        public int Calls => _calls;

        public Task<WatcherAnalysis?> AnalyseAsync(WatcherEvidencePack pack, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<WatcherAnalysis?>(new WatcherAnalysis
            {
                RootCause = "unknown",
                Confidence = "low",
                Unknowns = ["The fixture analyst does not inspect the running system."],
                Calls =
                [
                    new WatcherModelCall
                    {
                        Purpose = WatcherModelCallPurposes.Analysis,
                        Model = "gpt-5.6-sol",
                        ThinkingLevel = "medium",
                        InputTokens = 4_000,
                        OutputTokens = 400,
                        CostUsd = null,
                        PriceKnown = false,
                        AtUtc = pack.LastSeenAtUtc,
                    },
                ],
            });
        }
    }

    internal sealed class FixedRoutingMode(bool economyMode) : IModelRoutingModeProvider
    {
        public bool EconomyMode { get; } = economyMode;
    }

    private static string Text(long? value) =>
        (value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void RunGit(string cwd, params string[] args) => Git(cwd, args);

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp folder is not a test failure.
        }
    }
}
