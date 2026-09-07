namespace AgentStudio.Watcher;

/// <summary>Structured summary of one sweep, mirroring <c>AcceptanceRailSnapshot</c> for the status endpoint and the <c>watcher-run</c> log line.</summary>
public sealed record WatcherRunSnapshot
{
    public DateTime? LastRunAtUtc { get; init; }
    public string SweepId { get; init; } = "";
    public bool Enabled { get; init; }
    public int ObservationsCollected { get; init; }
    public int CasesUpdated { get; init; }
    public int ProposalsCreated { get; init; }
    public int CommentsAppended { get; init; }
    public int ContingentBlocked { get; init; }
    public int AnalysisCalls { get; init; }
    public string? Error { get; init; }

    public static WatcherRunSnapshot Idle(string reason) => new() { Error = reason };
}

/// <summary>
/// The W1+W2 hosted sweep (§7 operating model): runs every registered
/// detector probe on a fixed cadence, folds observations into durable cases,
/// and for cases that persist across
/// <see cref="WatcherOptions.PersistenceSweepsBeforeProposal"/> sweeps, drafts
/// a ticket proposal (§10.3) subject to the contingent (§10.4). Shadow mode
/// by construction: the only mutation authority this service ever exercises
/// is <see cref="WatcherProposalDraftingService"/> creating a proposal card
/// or appending a comment - never a lane move, a merge, or a Git operation.
/// Kill switch: <c>Watcher:Enabled</c>, re-read every tick like
/// <c>AcceptanceRail:Enabled</c>.
/// </summary>
public sealed class WatcherHostedService : BackgroundService
{
    private readonly IEnumerable<IWatcherSignalProbe> _probes;
    private readonly WatcherCaseEngine _engine;
    private readonly WatcherCaseStore _caseStore;
    private readonly WatcherEvidencePackBuilder _packs;
    private readonly WatcherAnalysisService _analysis;
    private readonly WatcherProposalDraftingService _drafting;
    private readonly WatcherActivityProjector _activity;
    private readonly WatcherContingentService _contingent;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherHostedService> _logger;

    public WatcherRunSnapshot Current { get; private set; } = WatcherRunSnapshot.Idle("Not run yet.");

    public WatcherHostedService(
        IEnumerable<IWatcherSignalProbe> probes,
        WatcherCaseEngine engine,
        WatcherCaseStore caseStore,
        WatcherEvidencePackBuilder packs,
        WatcherAnalysisService analysis,
        WatcherProposalDraftingService drafting,
        WatcherActivityProjector activity,
        WatcherContingentService contingent,
        IConfiguration configuration,
        ILogger<WatcherHostedService> logger)
    {
        _probes = probes;
        _engine = engine;
        _caseStore = caseStore;
        _packs = packs;
        _analysis = analysis;
        _drafting = drafting;
        _activity = activity;
        _contingent = contingent;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = WatcherOptions.FromConfiguration(_configuration);
            try
            {
                if (options.Enabled) await RunOnceAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Watcher sweep failed"); }

            options = WatcherOptions.FromConfiguration(_configuration);
            try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Pure-enough, testable single sweep. Same shape as <c>AcceptanceRailHostedService.RunOnceAsync</c>.</summary>
    public async Task<WatcherRunSnapshot> RunOnceAsync(WatcherOptions? optionsOverride = null, CancellationToken ct = default)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            Current = WatcherRunSnapshot.Idle("TaskRepository not configured.");
            return Current;
        }

        var options = optionsOverride ?? WatcherOptions.FromConfiguration(_configuration);
        var budgets = WatcherContingentBudgets.FromConfiguration(_configuration);
        var nowUtc = DateTime.UtcNow;
        // Wall-clock plus a random suffix, not an in-memory counter: a
        // counter resets to zero on every process restart and would collide
        // with the prior process's last sweep id, silently losing the "two
        // distinct sweeps" signal the durable case store is supposed to
        // survive a restart for.
        var sweepId = $"{nowUtc:O}-{Guid.NewGuid():N}";

        var observations = new List<WatcherSignalObservation>();
        foreach (var probe in _probes)
        {
            try
            {
                observations.AddRange(probe.Collect(workspaceRoot, nowUtc));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "watcher-probe-failed probe={Probe}", probe.Name);
            }
        }

        var ingest = _engine.Ingest(workspaceRoot, observations, sweepId, options, nowUtc);

        // Noise rule (§4): a brand-new fingerprint raises one Problem entry.
        // A recurring one only updates the durable case counters.
        foreach (var freshCase in ingest.UpdatedCases.Where(c => c.OccurrenceCount == 1))
            _activity.ProjectFindingRaised(freshCase, _packs.Build(freshCase));

        var proposalsCreated = 0;
        var commentsAppended = 0;
        var contingentBlocked = 0;
        var analysisCalls = 0;

        foreach (var readyCase in ingest.ReadyForProposal)
        {
            var pack = _packs.Build(readyCase);
            WatcherAnalysisReceipt? analysis = null;

            if (_analysis.NeedsAnalysis(readyCase.DetectorClass) && _contingent.CanCallModel(workspaceRoot, budgets, nowUtc))
            {
                analysis = await _analysis.AnalyzeAsync(readyCase, pack, ct);
                _contingent.RecordModelCall(workspaceRoot, analysis.InputTokens + analysis.OutputTokens, nowUtc);
                analysisCalls++;
                _activity.ProjectAnalysisComplete(readyCase, analysis);
            }

            var draft = _drafting.Draft(workspaceRoot, readyCase, pack, analysis, budgets, nowUtc);
            switch (draft.Outcome)
            {
                case WatcherDraftOutcome.Created:
                    proposalsCreated++;
                    var created = readyCase with
                    {
                        State = WatcherCaseStates.DecisionRequired,
                        ProposalId = draft.Proposal!.Id,
                        ProposalJobId = draft.Proposal.JobId,
                        EvidenceDigest = pack.DigestSha256,
                    };
                    _caseStore.Save(workspaceRoot, created);
                    _activity.ProjectDecisionRequired(created, draft.Proposal);
                    break;

                case WatcherDraftOutcome.Commented:
                    commentsAppended++;
                    var commented = readyCase with { ProposalId = draft.Proposal!.Id, IsCommentOnly = true, EvidenceDigest = pack.DigestSha256 };
                    _caseStore.Save(workspaceRoot, commented);
                    _activity.ProjectDecisionRequired(commented, draft.Proposal);
                    break;

                case WatcherDraftOutcome.ContingentExhausted:
                    // Stays Open: the backlog is visible via the cases listing
                    // and will draft on a later sweep once the contingent
                    // resets. Not a terminal - a budget window is transient.
                    contingentBlocked++;
                    break;

                case WatcherDraftOutcome.CreationFailed:
                    break;
            }
        }

        Current = new WatcherRunSnapshot
        {
            LastRunAtUtc = nowUtc,
            SweepId = sweepId,
            Enabled = options.Enabled,
            ObservationsCollected = observations.Count,
            CasesUpdated = ingest.UpdatedCases.Count,
            ProposalsCreated = proposalsCreated,
            CommentsAppended = commentsAppended,
            ContingentBlocked = contingentBlocked,
            AnalysisCalls = analysisCalls,
        };

        _logger.LogInformation(
            "watcher-run observations={Observations} casesUpdated={CasesUpdated} proposals={Proposals} comments={Comments} contingentBlocked={ContingentBlocked} analysisCalls={AnalysisCalls} lastRunAtUtc={LastRunAtUtc}",
            Current.ObservationsCollected, Current.CasesUpdated, Current.ProposalsCreated, Current.CommentsAppended,
            Current.ContingentBlocked, Current.AnalysisCalls, Current.LastRunAtUtc);

        return Current;
    }
}
