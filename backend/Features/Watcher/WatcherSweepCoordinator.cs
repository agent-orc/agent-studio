namespace AgentStudio.Watcher;

/// <summary>Counters for one sweep. These are the fields of the <c>watcher-run</c> structured log.</summary>
public sealed record WatcherSweepResult
{
    public bool Enabled { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public int Findings { get; init; }
    public int CasesOpened { get; init; }
    public int CasesUpdated { get; init; }
    public int CasesClosed { get; init; }
    public int ProposalsCreated { get; init; }
    public int CommentsAppended { get; init; }
    public int SuppressedFindings { get; init; }
    public int Backlog { get; init; }
    public int ModelCalls { get; init; }
    public int Failed { get; init; }

    public static readonly WatcherSweepResult Disabled = new() { Enabled = false };
}

/// <summary>
/// The W1 to W2 chain: findings become deduplicated cases, cases that survive
/// the persistence check become ticket proposals, and everything else stays a
/// visible, counted backlog.
/// </summary>
/// <remarks>
/// The flow is deliberately ordered as boundary reads, then pure decisions,
/// then bounded side effects. Detection and admission never call a model and
/// never mutate; the only writes are one proposal card or one comment per case,
/// both through <see cref="IWatcherTaskGateway"/>.
/// </remarks>
public sealed class WatcherSweepCoordinator
{
    private readonly WatcherStore _store;
    private readonly IWatcherTaskGateway _gateway;
    private readonly IWatcherAnalyst _analyst;
    private readonly ModelRoutingPolicyRegistry _routing;
    private readonly IWatcherActivityPublisher _activity;
    private readonly ILogger<WatcherSweepCoordinator> _logger;

    public WatcherSweepCoordinator(
        WatcherStore store,
        IWatcherTaskGateway gateway,
        IWatcherAnalyst analyst,
        ModelRoutingPolicyRegistry routing,
        IWatcherActivityPublisher activity,
        ILogger<WatcherSweepCoordinator> logger)
    {
        _store = store;
        _gateway = gateway;
        _analyst = analyst;
        _routing = routing;
        _activity = activity;
        _logger = logger;
    }

    public async Task<WatcherSweepResult> SweepAsync(
        WatcherSweepInput input,
        WatcherOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return WatcherSweepResult.Disabled;

        var findings = WatcherDetectors.Detect(input, options.Detectors);
        var reconciliation = Reconcile(findings, input.NowUtc, options);

        foreach (var raised in reconciliation.Raised)
            _activity.FindingRaised(raised);

        var proposals = 0;
        var comments = 0;
        var modelCalls = 0;
        var failed = 0;

        foreach (var watcherCase in reconciliation.ReadyForProposal)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ProposeAsync(watcherCase, options, input.NowUtc, ct);
                proposals += outcome.Proposals;
                comments += outcome.Comments;
                modelCalls += outcome.ModelCalls;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "watcher-proposal-failed case={CaseId} fingerprint={Fingerprint}",
                    watcherCase.CaseId,
                    watcherCase.Fingerprint);
            }
        }

        var backlog = _store.Cases().Count(row =>
            row.State == WatcherCaseStates.Open
            && row.BacklogReason == WatcherBacklogReasons.ContingentExhausted);

        return new WatcherSweepResult
        {
            Enabled = true,
            LastRunAtUtc = input.NowUtc,
            Findings = findings.Count,
            CasesOpened = reconciliation.Raised.Count,
            CasesUpdated = reconciliation.Updated,
            CasesClosed = reconciliation.Closed,
            ProposalsCreated = proposals,
            CommentsAppended = comments,
            SuppressedFindings = reconciliation.Suppressed,
            Backlog = backlog,
            ModelCalls = modelCalls,
            Failed = failed,
        };
    }

    // ------------------------------------------------------------ pure decisions

    private sealed record Reconciliation(
        List<WatcherCase> Raised,
        List<WatcherCase> ReadyForProposal,
        int Updated,
        int Closed,
        int Suppressed);

    /// <summary>
    /// Fold this sweep's findings into the durable case set: append evidence to
    /// what is already known, open what is new, and give up on cases whose
    /// signal has stopped.
    /// </summary>
    private Reconciliation Reconcile(
        IReadOnlyList<WatcherFinding> findings,
        DateTime nowUtc,
        WatcherOptions options)
        => _store.Mutate(state =>
        {
            var raised = new List<WatcherCase>();
            var ready = new List<WatcherCase>();
            var updated = 0;
            var closed = 0;
            var suppressed = 0;

            var byFingerprint = state.Cases.ToDictionary(row => row.Fingerprint, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var finding in findings)
            {
                seen.Add(finding.Fingerprint);

                var suppression = state.Suppressions.FirstOrDefault(row =>
                    string.Equals(row.Fingerprint, finding.Fingerprint, StringComparison.Ordinal));
                var isSuppressed = suppression?.IsActive(nowUtc) == true;
                if (isSuppressed) suppressed++;

                var evidence = BuildEvidence(finding);

                if (byFingerprint.TryGetValue(finding.Fingerprint, out var existing))
                {
                    // A terminal case that recurs is genuinely new work again,
                    // so it reopens rather than staying quietly closed. An
                    // unexpired suppression is the one thing that keeps it
                    // closed; once the suppression lapses, a still-live fault
                    // must be able to ask again.
                    var reopened = existing.IsTerminal && !isSuppressed;

                    var next = existing with
                    {
                        LastSeenAtUtc = finding.LastSeenAtUtc,
                        Occurrences = finding.Occurrences,
                        SweepsSeen = existing.SweepsSeen + 1,
                        AffectedCards = finding.AffectedCards,
                        Title = finding.Title,
                        Summary = finding.Summary,
                        Evidence = evidence,
                        EvidencePackDigest = evidence.Digest,
                        UpdatedAtUtc = nowUtc,
                        State = reopened ? WatcherCaseStates.Open : existing.State,
                        // Releasing the answered proposal is what lets the
                        // reopened case draft again. Without this the case
                        // reopens and then silently never proposes, because a
                        // case that already owns a proposal is skipped.
                        ProposalId = reopened ? null : existing.ProposalId,
                        BacklogReason = isSuppressed
                            ? WatcherBacklogReasons.Suppressed
                            : reopened || existing.State == WatcherCaseStates.DecisionRequired
                                ? null
                                : existing.BacklogReason,
                    };

                    Replace(state.Cases, next);
                    byFingerprint[next.Fingerprint] = next;
                    updated++;
                    if (!isSuppressed && QualifiesForProposal(next, options)) ready.Add(next);
                    continue;
                }

                var opened = new WatcherCase
                {
                    CaseId = WatcherStore.NextCaseId(state),
                    Fingerprint = finding.Fingerprint,
                    DetectorClass = finding.DetectorClass,
                    DetectorRule = finding.DetectorRule,
                    Project = finding.Project,
                    Title = finding.Title,
                    Summary = finding.Summary,
                    FirstSeenAtUtc = finding.FirstSeenAtUtc,
                    LastSeenAtUtc = finding.LastSeenAtUtc,
                    Occurrences = finding.Occurrences,
                    SweepsSeen = 1,
                    AffectedCards = finding.AffectedCards,
                    Evidence = evidence,
                    EvidencePackDigest = evidence.Digest,
                    State = WatcherCaseStates.Open,
                    BacklogReason = isSuppressed
                        ? WatcherBacklogReasons.Suppressed
                        : WatcherBacklogReasons.AwaitingPersistence,
                    UpdatedAtUtc = nowUtc,
                };

                state.Cases.Add(opened);
                byFingerprint[opened.Fingerprint] = opened;
                raised.Add(opened);
                if (!isSuppressed && QualifiesForProposal(opened, options)) ready.Add(opened);
            }

            // A case whose signal stopped before it ever produced a proposal
            // reaches an explicit terminal instead of lingering as an open row.
            foreach (var stale in state.Cases
                         .Where(row => row.State == WatcherCaseStates.Open && !seen.Contains(row.Fingerprint))
                         .ToList())
            {
                Replace(state.Cases, stale with
                {
                    State = WatcherCaseStates.GaveUp,
                    BacklogReason = null,
                    UpdatedAtUtc = nowUtc,
                });
                closed++;
            }

            return new Reconciliation(raised, ready, updated, closed, suppressed);
        });

    /// <summary>
    /// A proposal needs a case that survived the persistence check and does not
    /// already have one. A single sweep is a blip; two is a pattern.
    /// </summary>
    private static bool QualifiesForProposal(WatcherCase watcherCase, WatcherOptions options)
        => watcherCase.SweepsSeen >= options.PersistenceSweeps
           && watcherCase.ProposalId is null
           && watcherCase.State == WatcherCaseStates.Open;

    // ------------------------------------------------------- bounded side effects

    private sealed record ProposalOutcome(int Proposals, int Comments, int ModelCalls);

    private async Task<ProposalOutcome> ProposeAsync(
        WatcherCase watcherCase,
        WatcherOptions options,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var project = watcherCase.Project ?? string.Empty;

        // Which card, if any, this fingerprint already owns. A previous
        // proposal's card that is still open turns this into a comment.
        var priorCard = _store.Proposals()
            .Where(row => string.Equals(row.Fingerprint, watcherCase.Fingerprint, StringComparison.Ordinal))
            .Select(row => row.CreatedTaskKey)
            .LastOrDefault(key => key is not null);
        var openPriorCards = priorCard is null
            ? []
            : _gateway.OpenCards(watcherCase.Project, [priorCard]);
        var overlapping = _gateway.OpenCards(watcherCase.Project, watcherCase.AffectedCards);

        var kind = openPriorCards.Count > 0 ? WatcherProposalKinds.Comment : WatcherProposalKinds.NewCard;
        var spendKind = kind == WatcherProposalKinds.Comment ? WatcherSpendKind.Comment : WatcherSpendKind.Proposal;

        var admission = Admit(spendKind, 0, options, nowUtc);
        if (!admission.Allowed)
        {
            MarkBacklog(watcherCase, WatcherBacklogReasons.ContingentExhausted, nowUtc);
            _activity.ContingentExhausted(watcherCase, admission.Reason);
            _logger.LogInformation(
                "watcher-contingent-exhausted case={CaseId} kind={Kind} reason={Reason}",
                watcherCase.CaseId,
                kind,
                admission.Reason);
            return new ProposalOutcome(0, 0, 0);
        }

        var analysis = await AnalyseAsync(watcherCase, options, nowUtc, ct);
        var draft = WatcherCardDraftComposer.Compose(watcherCase, overlapping, analysis.Result?.Note);
        var recommendation = Recommend(draft);

        var proposal = new WatcherProposal
        {
            ProposalId = $"{watcherCase.CaseId}-P{_store.Proposals().Count(row => row.CaseId == watcherCase.CaseId) + 1}",
            CaseId = watcherCase.CaseId,
            Fingerprint = watcherCase.Fingerprint,
            DetectorClass = watcherCase.DetectorClass,
            DetectorRule = watcherCase.DetectorRule,
            Project = project,
            Kind = kind,
            TargetTaskKey = kind == WatcherProposalKinds.Comment ? openPriorCards[0] : null,
            Draft = draft,
            Recommendation = recommendation,
            EvidencePackDigest = watcherCase.EvidencePackDigest,
            Analysis = analysis.Result?.Receipt ?? WatcherAnalysisReceipt.None with { Route = analysis.Plan.Reason },
            CreatedAtUtc = nowUtc,
        };

        if (kind == WatcherProposalKinds.Comment)
        {
            var appended = _gateway.AppendComment(
                project,
                proposal.TargetTaskKey!,
                watcherCase.CaseId,
                watcherCase.Title,
                draft.Prompt);
            if (!appended)
            {
                _logger.LogWarning(
                    "watcher-comment-refused case={CaseId} task={TaskKey}",
                    watcherCase.CaseId,
                    proposal.TargetTaskKey);
                return new ProposalOutcome(0, 0, analysis.ModelCalls);
            }

            Commit(watcherCase, proposal, analysis, nowUtc, comments: 1, proposals: 0);
            _activity.ProposalCreated(watcherCase, proposal);
            return new ProposalOutcome(0, 1, analysis.ModelCalls);
        }

        var created = _gateway.CreateProposalCard(project, draft, recommendation, watcherCase.CaseId);
        if (created is null)
        {
            _logger.LogWarning(
                "watcher-proposal-card-refused case={CaseId} project={Project}",
                watcherCase.CaseId,
                project);
            return new ProposalOutcome(0, 0, analysis.ModelCalls);
        }

        proposal = proposal with { CreatedTaskId = created.TaskId, CreatedTaskKey = created.TaskKey };
        Commit(watcherCase, proposal, analysis, nowUtc, comments: 0, proposals: 1);
        _activity.ProposalCreated(watcherCase, proposal);
        return new ProposalOutcome(1, 0, analysis.ModelCalls);
    }

    private sealed record AnalysisOutcome(WatcherAnalysisPlan Plan, WatcherAnalysis? Result, int ModelCalls);

    private async Task<AnalysisOutcome> AnalyseAsync(
        WatcherCase watcherCase,
        WatcherOptions options,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var plan = WatcherAnalysisPolicy.Plan(watcherCase, EvidenceChars(watcherCase));
        if (!plan.NeedsModel) return new AnalysisOutcome(plan, null, 0);

        var estimate = WatcherAnalysisPolicy.EstimatedTokens(plan.Route);
        var admission = Admit(WatcherSpendKind.ModelCall, estimate, options, nowUtc);
        if (!admission.Allowed)
        {
            _logger.LogInformation(
                "watcher-analysis-skipped case={CaseId} route={Route} reason={Reason}",
                watcherCase.CaseId,
                plan.Route,
                admission.Reason);
            return new AnalysisOutcome(
                plan with { Reason = $"{plan.Reason}; skipped because {admission.Reason}" },
                null,
                0);
        }

        var result = await _analyst.AnalyseAsync(watcherCase, plan, ct);
        if (result is null) return new AnalysisOutcome(plan, null, 0);

        _activity.AnalysisComplete(watcherCase, result.Receipt);
        return new AnalysisOutcome(plan, result, result.Receipt.Calls.Count);
    }

    private WatcherContingentDecision Admit(
        WatcherSpendKind kind,
        long estimatedTokens,
        WatcherOptions options,
        DateTime nowUtc)
    {
        var spend = _store.Snapshot().Spend;
        return WatcherContingentPolicy.Admit(
            kind,
            estimatedTokens,
            options.Contingent,
            WatcherContingentPolicy.Usage(spend, nowUtc, WatcherContingentPolicy.DailyWindow),
            WatcherContingentPolicy.Usage(spend, nowUtc, WatcherContingentPolicy.WeeklyWindow));
    }

    /// <summary>Record the proposal, move the case to decision-required, and bill the contingent in one write.</summary>
    private void Commit(
        WatcherCase watcherCase,
        WatcherProposal proposal,
        AnalysisOutcome analysis,
        DateTime nowUtc,
        int comments,
        int proposals)
        => _store.Mutate(state =>
        {
            state.Proposals.Add(proposal);

            var current = state.Cases.FirstOrDefault(row => row.CaseId == watcherCase.CaseId);
            if (current is not null)
            {
                Replace(state.Cases, current with
                {
                    State = WatcherCaseStates.DecisionRequired,
                    BacklogReason = null,
                    ProposalId = proposal.ProposalId,
                    UpdatedAtUtc = nowUtc,
                });
            }

            var receipt = analysis.Result?.Receipt;
            state.Spend.Add(new WatcherSpendEntry
            {
                AtUtc = nowUtc,
                InputTokens = receipt?.TotalInputTokens ?? 0,
                OutputTokens = receipt?.TotalOutputTokens ?? 0,
                ModelCalls = receipt?.Calls.Count ?? 0,
                Proposals = proposals,
                Comments = comments,
                Dollars = receipt?.TotalDollars ?? 0d,
            });
            return true;
        });

    private void MarkBacklog(WatcherCase watcherCase, string reason, DateTime nowUtc)
        => _store.Mutate(state =>
        {
            var current = state.Cases.FirstOrDefault(row => row.CaseId == watcherCase.CaseId);
            if (current is null) return false;
            Replace(state.Cases, current with { BacklogReason = reason, UpdatedAtUtc = nowUtc });
            return true;
        });

    private WatcherModelRecommendation Recommend(WatcherCardDraft draft)
    {
        var recommendation = _routing.RecommendFromPolicy(draft.TaskType, title: draft.Title, prompt: draft.Prompt);
        return new WatcherModelRecommendation
        {
            PolicyVersion = recommendation.PolicyVersion,
            Tier = recommendation.Tier,
            Model = recommendation.Model,
            ThinkingLevel = recommendation.ThinkingLevel,
            CorrectnessFloorTier = recommendation.CorrectnessFloorTier,
            Score = recommendation.Score,
            Reason = recommendation.Reason,
        };
    }

    // --------------------------------------------------------------------- shared

    private static WatcherEvidencePack BuildEvidence(WatcherFinding finding)
    {
        var items = finding.Evidence
            .Take(WatcherDefaults.MaxEvidenceItems)
            .Select(item => item.Value is { Length: > WatcherDefaults.MaxEvidenceValueChars }
                ? item with { Value = item.Value[..WatcherDefaults.MaxEvidenceValueChars] + "..." }
                : item)
            .ToList();
        return new WatcherEvidencePack
        {
            Digest = WatcherFingerprint.DigestEvidence(items),
            Items = items,
        };
    }

    private static int EvidenceChars(WatcherCase watcherCase)
        => watcherCase.Evidence.Items.Sum(item => item.Value?.Length ?? 0);

    private static void Replace(List<WatcherCase> cases, WatcherCase next)
    {
        var index = cases.FindIndex(row => string.Equals(row.CaseId, next.CaseId, StringComparison.Ordinal));
        if (index >= 0) cases[index] = next;
        else cases.Add(next);
    }
}
