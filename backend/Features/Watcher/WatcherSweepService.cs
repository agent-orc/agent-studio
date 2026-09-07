using System.Diagnostics;

namespace AgentStudio.Watcher;

/// <summary>What one sweep did, returned so a test can assert it without a host.</summary>
public sealed record WatcherSweepResult
{
    public required DateTime AtUtc { get; init; }
    public int SignalsCollected { get; init; }
    public int FindingsDetected { get; init; }
    public int CasesOpened { get; init; }
    public int CasesTouched { get; init; }
    public int CasesClosed { get; init; }
    public int ProposalsCreated { get; init; }
    public int CommentsAppended { get; init; }
    public int ModelCalls { get; init; }
    /// <summary>Persistent cases the contingent refused this sweep.</summary>
    public int Backlogged { get; init; }
    public long ElapsedMs { get; init; }
    public IReadOnlyList<WatcherCase> Cases { get; init; } = [];
    public IReadOnlyList<WatcherProposal> Proposals { get; init; } = [];
    /// <summary>Set when the contingent closed a dimension during this sweep.</summary>
    public string? ContingentBlockedReason { get; init; }
}

/// <summary>
/// One Watcher cycle: collect, detect, fold into cases, and propose for the
/// cases that survived two sweeps and fit inside the contingent.
/// </summary>
/// <remarks>
/// <para>
/// The sweep performs no task or Git mutation of its own. The only writes it
/// causes are the proposal cards and comments the proposal service makes through
/// the task API, and those are gated by the contingent.
/// </para>
/// <para>
/// It is safe to call directly, which is how the fixture replay and the tests
/// drive it without waiting for the hosted cadence.
/// </para>
/// </remarks>
public sealed class WatcherSweepService
{
    private readonly WatcherSignalCollector _collector;
    private readonly WatcherStore _store;
    private readonly WatcherProposalService _proposals;
    private readonly WatcherBusPublisher _publisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherSweepService> _logger;

    public WatcherSweepService(
        WatcherSignalCollector collector,
        WatcherStore store,
        WatcherProposalService proposals,
        WatcherBusPublisher publisher,
        IConfiguration configuration,
        ILogger<WatcherSweepService> logger)
    {
        _collector = collector;
        _store = store;
        _proposals = proposals;
        _publisher = publisher;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Collect from the live sources and run one sweep.</summary>
    public async Task<WatcherSweepResult> SweepAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var input = await _collector.CollectAsync(nowUtc, ct);
        return await SweepAsync(input, ct);
    }

    /// <summary>
    /// Run one sweep over a supplied input. This is the entry point the dossier
    /// fixture replay uses, so a replay and a live cycle take the same path.
    /// </summary>
    public async Task<WatcherSweepResult> SweepAsync(WatcherSweepInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stopwatch = Stopwatch.StartNew();
        var options = WatcherOptions.FromConfiguration(_configuration);
        var nowUtc = input.NowUtc;

        var findings = WatcherDetectors.Detect(input, options.Detectors);
        var fold = WatcherCasePolicy.Fold(
            _store.Cases(),
            findings,
            _store.Suppressions(),
            nowUtc,
            sweepHadSignals: input.SignalCount > 0);

        // Persist only what this sweep changed. The case file is append-only, so
        // rewriting every settled terminal on every five-minute cycle would grow
        // it without adding a single fact.
        foreach (var item in fold.Opened.Concat(fold.Touched).Concat(fold.Closed))
            _store.Upsert(item);

        // Noise rule: a repeat sweep that finds nothing new updates counters
        // only. Feed rows appear for a new case and for a terminal.
        foreach (var item in fold.Opened) await _publisher.FindingRaisedAsync(item, ct);
        foreach (var item in fold.Closed) await _publisher.CaseResolvedAsync(item, ct);

        var eligible = WatcherCasePolicy.EligibleForProposal(fold.Cases);
        var cases = fold.Cases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var proposals = new List<WatcherProposal>();
        var created = 0;
        var commented = 0;
        var modelCalls = 0;
        var backlogged = 0;
        string? blockedReason = null;

        // One workspace scan for the whole sweep. Creating a card invalidates
        // the task index, so looking the fingerprint up per proposal would
        // rebuild the scan once per proposal.
        var openCards = eligible.Count > 0
            ? _proposals.OpenCards()
            : WatcherOpenCardIndex.Empty;

        foreach (var item in eligible.Take(options.MaxProposalsPerSweep))
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _proposals.ProposeAsync(item, options, nowUtc, openCards, ct);
            cases[outcome.Case.Id] = outcome.Case;
            modelCalls += outcome.ModelCalls;

            if (outcome.Proposal is null)
            {
                backlogged++;
                blockedReason ??= outcome.BlockedReason;
                continue;
            }

            proposals.Add(outcome.Proposal);
            if (outcome.Proposal.Kind == WatcherProposalKinds.Comment) commented++;
            else created++;
        }

        // Cases beyond the per-sweep cap are backlog too, and the operator
        // should see them as waiting rather than as not found.
        var overflow = Math.Max(0, eligible.Count - options.MaxProposalsPerSweep);
        backlogged += overflow;

        if (blockedReason is not null)
            await _publisher.ContingentExhaustedAsync(blockedReason, backlogged, ct);

        stopwatch.Stop();
        var result = new WatcherSweepResult
        {
            AtUtc = nowUtc,
            SignalsCollected = input.SignalCount,
            FindingsDetected = findings.Count,
            CasesOpened = fold.Opened.Count,
            CasesTouched = fold.Touched.Count,
            CasesClosed = fold.Closed.Count,
            ProposalsCreated = created,
            CommentsAppended = commented,
            ModelCalls = modelCalls,
            Backlogged = backlogged,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Cases = cases.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList(),
            Proposals = proposals,
            ContingentBlockedReason = blockedReason,
        };

        _logger.LogInformation(
            "watcher-run signals={Signals} findings={Findings} opened={Opened} touched={Touched} closed={Closed} proposals={Proposals} comments={Comments} modelCalls={ModelCalls} backlog={Backlog} elapsedMs={ElapsedMs} atUtc={AtUtc}",
            result.SignalsCollected,
            result.FindingsDetected,
            result.CasesOpened,
            result.CasesTouched,
            result.CasesClosed,
            result.ProposalsCreated,
            result.CommentsAppended,
            result.ModelCalls,
            result.Backlogged,
            result.ElapsedMs,
            result.AtUtc);

        return result;
    }

    /// <summary>
    /// The contingent as the Workspace CLI Management page renders it: the
    /// budget, the spend, and the size of the backlog it produced.
    /// </summary>
    public WatcherContingentSnapshot Contingent(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var options = WatcherOptions.FromConfiguration(_configuration);
        var usage = WatcherContingentPolicy.Usage(_store.Ledger(), now);

        var exhausted = WatcherContingentKinds.All
            .Select(kind => (kind, verdict: WatcherContingentPolicy.Admit(kind, options.Contingent, usage)))
            .Where(pair => !pair.verdict.Allowed)
            .Select(pair => pair.verdict.Reason!)
            .ToList();

        return new WatcherContingentSnapshot
        {
            Limits = options.Contingent,
            Usage = usage,
            DayStartUtc = WatcherContingentPolicy.DayStart(now),
            WeekStartUtc = WatcherContingentPolicy.WeekStart(now),
            ExhaustedDimensions = exhausted,
            BacklogCases = _store.Cases().Count(item =>
                item.State == WatcherCaseStates.Backlogged
                || (item.SweepCount >= WatcherCasePolicy.PersistenceSweeps
                    && item.ProposalId is null
                    && !WatcherCaseStates.IsTerminal(item.State))),
            ProposalsBlocked = !WatcherContingentPolicy
                .Admit(WatcherContingentKinds.Proposal, options.Contingent, usage).Allowed,
            ModelCallsBlocked = !options.AnalysisEnabled || !WatcherContingentPolicy
                .Admit(WatcherContingentKinds.ModelCall, options.Contingent, usage).Allowed,
        };
    }
}
