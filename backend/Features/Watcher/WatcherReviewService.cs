namespace AgentStudio.Watcher;

/// <summary>One operator answer on a proposal.</summary>
public sealed record WatcherDecisionRequest
{
    /// <summary>One of <see cref="WatcherProposalDecisions"/>, excluding <c>pending</c>.</summary>
    public string Decision { get; init; } = string.Empty;

    /// <summary>Required for a rejection: it is what feeds the visible suppression entry.</summary>
    public string? Reason { get; init; }

    /// <summary>Card the proposal is folded into when the decision is <c>merged</c>.</summary>
    public string? MergeIntoTaskKey { get; init; }

    /// <summary>True when the operator changed the draft before approving. Promotion evidence for section 10.4.</summary>
    public bool Edited { get; init; }
}

public sealed record WatcherDecisionOutcome(bool Applied, string Reason, WatcherProposal? Proposal);

/// <summary>
/// Review mode. A proposal never advances by itself; this is the only path from
/// <c>pending</c> to any other decision, and every path records who answered.
/// </summary>
/// <remarks>
/// Auto-approval is deliberately absent. What the service does instead is
/// record acceptances and edits per detector class, which is the evidence the
/// dossier's promotion rule needs before that question can even be asked.
/// </remarks>
public sealed class WatcherReviewService
{
    private readonly WatcherStore _store;
    private readonly IWatcherTaskGateway _gateway;
    private readonly IWatcherActivityPublisher _activity;
    private readonly IConfiguration _config;
    private readonly ILogger<WatcherReviewService> _logger;

    public WatcherReviewService(
        WatcherStore store,
        IWatcherTaskGateway gateway,
        IWatcherActivityPublisher activity,
        IConfiguration config,
        ILogger<WatcherReviewService> logger)
    {
        _store = store;
        _gateway = gateway;
        _activity = activity;
        _config = config;
        _logger = logger;
    }

    public async Task<WatcherDecisionOutcome> DecideAsync(
        string proposalId,
        WatcherDecisionRequest request,
        string decidedBy,
        DateTime? nowUtc = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = nowUtc ?? DateTime.UtcNow;
        var options = WatcherOptions.FromConfiguration(_config);

        var decision = (request.Decision ?? string.Empty).Trim().ToLowerInvariant();
        if (decision is not (WatcherProposalDecisions.Approved
            or WatcherProposalDecisions.Edited
            or WatcherProposalDecisions.Merged
            or WatcherProposalDecisions.Rejected))
        {
            return new WatcherDecisionOutcome(false, $"'{request.Decision}' is not an operator decision.", null);
        }

        var proposal = _store.FindProposal(proposalId);
        if (proposal is null)
            return new WatcherDecisionOutcome(false, $"Proposal '{proposalId}' does not exist.", null);
        if (proposal.Decision != WatcherProposalDecisions.Pending)
            return new WatcherDecisionOutcome(false, $"Proposal '{proposalId}' was already {proposal.Decision}.", proposal);

        var watcherCase = _store.FindCase(proposal.CaseId);
        if (watcherCase is null)
            return new WatcherDecisionOutcome(false, $"Case '{proposal.CaseId}' is missing for this proposal.", proposal);

        if (decision == WatcherProposalDecisions.Rejected && string.IsNullOrWhiteSpace(request.Reason))
            return new WatcherDecisionOutcome(false, "A rejection needs a reason; it is what the suppression entry shows.", proposal);

        if (decision == WatcherProposalDecisions.Merged && string.IsNullOrWhiteSpace(request.MergeIntoTaskKey))
            return new WatcherDecisionOutcome(false, "A merge needs the card the proposal is folded into.", proposal);

        // Approving is the only decision that moves a card, and it moves only
        // the card this proposal drafted.
        if (WatcherProposalDecisions.PromotesToReady(decision) && proposal.CreatedTaskId is { Length: > 0 })
        {
            var promoted = await _gateway.PromoteToReadyAsync(
                proposal.Project, proposal.CreatedTaskId, proposal.Recommendation, decidedBy, ct);
            if (!promoted)
            {
                return new WatcherDecisionOutcome(
                    false, "The drafted card could not be moved to Ready; it stays in the proposal state.", proposal);
            }
        }

        if (decision == WatcherProposalDecisions.Merged)
        {
            _gateway.AppendComment(
                proposal.Project,
                request.MergeIntoTaskKey!,
                watcherCase.CaseId,
                $"Merged Watcher case {watcherCase.CaseId}: {watcherCase.Title}",
                proposal.Draft.Prompt);
        }

        var settled = proposal with
        {
            Decision = decision,
            DecisionReason = request.Reason,
            DecidedBy = decidedBy,
            DecidedAtUtc = now,
            // An edit is an approval that needed changes, so it is recorded as
            // both: the decision stays 'edited' and the edited flag drives the
            // promotion evidence.
            Edited = request.Edited || decision == WatcherProposalDecisions.Edited,
            TargetTaskKey = decision == WatcherProposalDecisions.Merged
                ? request.MergeIntoTaskKey
                : proposal.TargetTaskKey,
        };

        _store.Mutate(state =>
        {
            var index = state.Proposals.FindIndex(row =>
                string.Equals(row.ProposalId, settled.ProposalId, StringComparison.Ordinal));
            if (index >= 0) state.Proposals[index] = settled;

            var caseIndex = state.Cases.FindIndex(row =>
                string.Equals(row.CaseId, settled.CaseId, StringComparison.Ordinal));
            if (caseIndex >= 0)
            {
                state.Cases[caseIndex] = state.Cases[caseIndex] with
                {
                    State = decision == WatcherProposalDecisions.Rejected
                        ? WatcherCaseStates.Suppressed
                        : WatcherCaseStates.Resolved,
                    BacklogReason = null,
                    UpdatedAtUtc = now,
                };
            }

            if (decision == WatcherProposalDecisions.Rejected)
            {
                state.Suppressions.RemoveAll(row =>
                    string.Equals(row.Fingerprint, settled.Fingerprint, StringComparison.Ordinal));
                state.Suppressions.Add(new WatcherSuppression
                {
                    Fingerprint = settled.Fingerprint,
                    DetectorClass = settled.DetectorClass,
                    Reason = request.Reason!,
                    SuppressedBy = decidedBy,
                    SuppressedAtUtc = now,
                    ExpiresAtUtc = now + options.SuppressionDuration,
                });
            }

            return true;
        });

        _activity.ProposalDecided(watcherCase, settled);
        _logger.LogInformation(
            "watcher-proposal-decided proposal={ProposalId} case={CaseId} decision={Decision} class={DetectorClass} edited={Edited} by={DecidedBy}",
            settled.ProposalId,
            settled.CaseId,
            settled.Decision,
            settled.DetectorClass,
            settled.Edited,
            decidedBy);

        return new WatcherDecisionOutcome(true, $"Proposal {settled.ProposalId} {decision}.", settled);
    }

    /// <summary>
    /// Acceptance evidence per detector class. This is what the promotion rule
    /// of section 10.4 would be evaluated against; nothing here promotes
    /// anything on its own.
    /// </summary>
    public IReadOnlyList<WatcherClassEvidence> ClassEvidence()
        => _store.Proposals()
            .Where(row => row.Decision != WatcherProposalDecisions.Pending)
            .GroupBy(row => row.DetectorClass, StringComparer.Ordinal)
            .Select(group => new WatcherClassEvidence
            {
                DetectorClass = group.Key,
                Promotable = WatcherDetectorClasses.IsPromotable(group.Key),
                Accepted = group.Count(row => row.Decision == WatcherProposalDecisions.Approved && !row.Edited),
                AcceptedWithEdit = group.Count(row => row.Edited),
                Merged = group.Count(row => row.Decision == WatcherProposalDecisions.Merged),
                Rejected = group.Count(row => row.Decision == WatcherProposalDecisions.Rejected),
            })
            .OrderBy(row => row.DetectorClass, StringComparer.Ordinal)
            .ToList();

    /// <summary>Drop a suppression before it expires, so a mistaken rejection is recoverable.</summary>
    public bool Unsuppress(string fingerprint)
        => _store.Mutate(state =>
            state.Suppressions.RemoveAll(row =>
                string.Equals(row.Fingerprint, fingerprint, StringComparison.Ordinal)) > 0);
}

/// <summary>Per-class acceptance and edit counts, the input to any later promotion decision.</summary>
public sealed record WatcherClassEvidence
{
    public required string DetectorClass { get; init; }

    /// <summary>False for contradiction and drift, which section 10.4 keeps human-approved permanently.</summary>
    public bool Promotable { get; init; }

    public int Accepted { get; init; }
    public int AcceptedWithEdit { get; init; }
    public int Merged { get; init; }
    public int Rejected { get; init; }
}
