using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Records every task-state effect the Watcher asks for instead of performing
/// it. A test can then assert the exact set of mutations, which is how the
/// "no task or Git mutation outside proposal creation and comments" rule is
/// checked rather than assumed.
/// </summary>
public sealed class FakeWatcherTaskGateway : IWatcherTaskGateway
{
    private int _created;

    /// <summary>Cards the workspace already has open, keyed by task key.</summary>
    public HashSet<string> OpenCardKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make card creation fail, so the refusal path is exercised.</summary>
    public bool RefuseCreate { get; set; }

    public List<(string Project, WatcherCardDraft Draft, string CaseId)> Created { get; } = [];
    public List<(string Project, string TaskKey, string CaseId, string Summary)> Comments { get; } = [];
    public List<(string Project, string TaskId, string DecidedBy)> Promoted { get; } = [];

    public IReadOnlyList<string> OpenCards(string? project, IReadOnlyCollection<string> taskKeys)
        => taskKeys.Where(OpenCardKeys.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public WatcherCardCreation? CreateProposalCard(
        string project,
        WatcherCardDraft draft,
        WatcherModelRecommendation recommendation,
        string caseId)
    {
        if (RefuseCreate) return null;
        Created.Add((project, draft, caseId));
        _created++;
        var key = $"WPX-{_created:D3}";
        OpenCardKeys.Add(key);
        return new WatcherCardCreation($"task-{key}", key);
    }

    public bool AppendComment(string project, string taskKey, string caseId, string summary, string body)
    {
        Comments.Add((project, taskKey, caseId, summary));
        return true;
    }

    public Task<bool> PromoteToReadyAsync(
        string project,
        string taskId,
        WatcherModelRecommendation recommendation,
        string decidedBy,
        CancellationToken ct)
    {
        Promoted.Add((project, taskId, decidedBy));
        return Task.FromResult(true);
    }
}

/// <summary>
/// Counts calls and answers with a fixed receipt. Used so "no model calls" is
/// an assertion about an analyst that would have answered, not about one that
/// could not.
/// </summary>
public sealed class CountingWatcherAnalyst : IWatcherAnalyst
{
    public int Calls { get; private set; }
    public List<WatcherAnalysisPlan> Plans { get; } = [];

    public Task<WatcherAnalysis?> AnalyseAsync(
        WatcherCase watcherCase,
        WatcherAnalysisPlan plan,
        CancellationToken ct)
    {
        Calls++;
        Plans.Add(plan);
        var receipt = new WatcherAnalysisReceipt
        {
            Route = plan.Route.ToString(),
            Calls =
            [
                new WatcherModelCall
                {
                    Purpose = plan.Route.ToString(),
                    Model = "gpt-5.6-sol",
                    ThinkingLevel = "medium",
                    InputTokens = 40_000,
                    OutputTokens = 4_000,
                    Dollars = 0.25,
                    AtUtc = watcherCase.LastSeenAtUtc,
                },
            ],
        };
        return Task.FromResult<WatcherAnalysis?>(
            new WatcherAnalysis($"Analysis for {watcherCase.CaseId}: {plan.Reason}.", receipt));
    }
}

/// <summary>Captures the Activity and bus projections without touching disk.</summary>
public sealed class RecordingWatcherActivityPublisher : IWatcherActivityPublisher
{
    public List<WatcherCase> Raised { get; } = [];
    public List<WatcherProposal> Proposed { get; } = [];
    public List<WatcherProposal> Decided { get; } = [];
    public List<(string CaseId, string Reason)> Exhausted { get; } = [];
    public List<string> Analysed { get; } = [];

    public void FindingRaised(WatcherCase watcherCase) => Raised.Add(watcherCase);

    public void AnalysisComplete(WatcherCase watcherCase, WatcherAnalysisReceipt receipt)
        => Analysed.Add(watcherCase.CaseId);

    public void ProposalCreated(WatcherCase watcherCase, WatcherProposal proposal) => Proposed.Add(proposal);

    public void ProposalDecided(WatcherCase watcherCase, WatcherProposal proposal) => Decided.Add(proposal);

    public void ContingentExhausted(WatcherCase watcherCase, string reason)
        => Exhausted.Add((watcherCase.CaseId, reason));
}

/// <summary>
/// One Watcher wired to a temp-file store, so restart behaviour can be tested
/// by building a second harness over the same path.
/// </summary>
public sealed class WatcherHarness : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// Only a harness that minted its own folder cleans it up. A restart test
    /// hands the same root to two harnesses in sequence, and the first one
    /// disposing must not delete the state the second one is meant to resume.
    /// </summary>
    private readonly bool _ownsRoot;

    public WatcherHarness(string? root = null, WatcherContingentOptions? contingent = null)
    {
        _ownsRoot = root is null;
        _root = root ?? Path.Combine(Path.GetTempPath(), "watcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Store = new WatcherStore(
            new ConfigurationBuilder().Build(),
            NullLogger<WatcherStore>.Instance)
        {
            StorePathOverride = Path.Combine(_root, WatcherStore.FileName),
        };

        Options = new WatcherOptions(
            Enabled: true,
            SweepInterval: TimeSpan.FromMinutes(5),
            PersistenceSweeps: WatcherDefaults.PersistenceSweeps,
            SuppressionDuration: TimeSpan.FromDays(WatcherDefaults.SuppressionDays),
            Detectors: WatcherDetectorOptions.Default,
            Contingent: contingent ?? WatcherContingentOptions.Default);

        Coordinator = new WatcherSweepCoordinator(
            Store,
            Gateway,
            Analyst,
            new ModelRoutingPolicyRegistry(),
            Activity,
            NullLogger<WatcherSweepCoordinator>.Instance);

        Review = new WatcherReviewService(
            Store,
            Gateway,
            Activity,
            new ConfigurationBuilder().Build(),
            NullLogger<WatcherReviewService>.Instance);
    }

    public string Root => _root;
    public WatcherStore Store { get; }
    public WatcherOptions Options { get; }
    public FakeWatcherTaskGateway Gateway { get; } = new();
    public CountingWatcherAnalyst Analyst { get; } = new();
    public RecordingWatcherActivityPublisher Activity { get; } = new();
    public WatcherSweepCoordinator Coordinator { get; }
    public WatcherReviewService Review { get; }

    public Task<WatcherSweepResult> SweepAsync(WatcherSweepInput input, WatcherOptions? options = null)
        => Coordinator.SweepAsync(input, options ?? Options);

    /// <summary>
    /// Replay the whole 2026-09-06 matrix twice, which is what the persistence
    /// check of section 10.3 requires before any proposal may be written.
    /// </summary>
    public async Task<WatcherSweepResult> ReplayUntilProposalsAsync(WatcherOptions? options = null)
    {
        await SweepAsync(WatcherFixtureMatrix.MergedSweep(), options);
        return await SweepAsync(
            WatcherFixtureMatrix.MergedSweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5)), options);
    }

    public void Dispose()
    {
        try
        {
            if (_ownsRoot && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            SilentCatch.Note(ex, "WatcherHarness: temp folder cleanup is best effort.");
        }
    }
}
