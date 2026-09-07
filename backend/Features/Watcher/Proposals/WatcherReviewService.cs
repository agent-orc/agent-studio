using System.Globalization;

namespace AgentStudio.Watcher;

/// <summary>Why a decision could not be recorded.</summary>
public enum WatcherDecisionStatus
{
    Success,
    ProposalNotFound,
    InvalidDecision,
    AlreadyDecided,
    CardNotFound,
    MoveRefused,
    ReasonRequired,
    MergeTargetRequired,
}

/// <param name="Status">Outcome of the decision attempt.</param>
/// <param name="Proposal">The proposal after the decision, when one was recorded.</param>
/// <param name="Message">Operator-facing explanation on a refusal.</param>
public sealed record WatcherDecisionResult(
    WatcherDecisionStatus Status,
    WatcherProposal? Proposal = null,
    string? Message = null)
{
    public bool Ok => Status == WatcherDecisionStatus.Success;
}

/// <summary>
/// Review mode of dossier section 10.4. Proposals never enter Ready by
/// themselves; approving one is an attributable operator act that moves the
/// card with the recommended model.
/// </summary>
/// <remarks>
/// Acceptance without an edit and acceptance after an edit are recorded
/// separately, because the promotion rule for auto-approval counts unedited
/// acceptances only, and only for the hygiene and repetition classes.
/// </remarks>
public sealed class WatcherReviewService
{
    private readonly WatcherStore _store;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly WatcherBusPublisher _publisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherReviewService> _logger;

    public WatcherReviewService(
        WatcherStore store,
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskTransitionService transitions,
        TimelineLog timeline,
        WatcherBusPublisher publisher,
        IConfiguration configuration,
        ILogger<WatcherReviewService> logger)
    {
        _store = store;
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _timeline = timeline;
        _publisher = publisher;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Proposals still waiting for an operator answer, oldest first.</summary>
    public IReadOnlyList<WatcherProposal> Pending() => _store.Proposals()
        .Where(item => item.Decision.State == WatcherProposalDecisions.Pending)
        .OrderBy(item => item.CreatedAtUtc)
        .ToList();

    /// <summary>
    /// Record an operator answer. Approve and edit move the card to Ready with
    /// the recommended model; merge and reject leave the run queue untouched.
    /// </summary>
    public async Task<WatcherDecisionResult> DecideAsync(
        string proposalId,
        string decision,
        string decidedBy,
        string? reason,
        string? mergeIntoTaskKey,
        CancellationToken ct = default)
    {
        var proposal = _store.Proposal(proposalId);
        if (proposal is null)
            return new WatcherDecisionResult(WatcherDecisionStatus.ProposalNotFound);
        if (!WatcherProposalDecisions.IsValid(decision) || decision == WatcherProposalDecisions.Pending)
            return new WatcherDecisionResult(
                WatcherDecisionStatus.InvalidDecision,
                Message: $"decision must be one of {string.Join(", ", WatcherProposalDecisions.All.Where(value => value != WatcherProposalDecisions.Pending))}");
        if (proposal.Decision.State != WatcherProposalDecisions.Pending)
            return new WatcherDecisionResult(
                WatcherDecisionStatus.AlreadyDecided,
                proposal,
                $"Proposal {proposal.Id} was already {proposal.Decision.State}.");
        // A rejection without a reason cannot feed a suppression an operator can
        // later understand, so the reason is required rather than defaulted.
        if (decision == WatcherProposalDecisions.Rejected && string.IsNullOrWhiteSpace(reason))
            return new WatcherDecisionResult(
                WatcherDecisionStatus.ReasonRequired,
                proposal,
                "A rejection needs a reason; it feeds the visible suppression list.");
        if (decision == WatcherProposalDecisions.Merged && string.IsNullOrWhiteSpace(mergeIntoTaskKey))
            return new WatcherDecisionResult(
                WatcherDecisionStatus.MergeTargetRequired,
                proposal,
                "A merge needs the card key it merges into.");

        var nowUtc = DateTime.UtcNow;
        var card = FindCard(proposal);

        if (decision is WatcherProposalDecisions.Approved or WatcherProposalDecisions.Edited)
        {
            if (proposal.Kind == WatcherProposalKinds.NewCard)
            {
                if (card is null)
                    return new WatcherDecisionResult(
                        WatcherDecisionStatus.CardNotFound,
                        proposal,
                        $"The proposed card {proposal.CreatedTaskKey} no longer exists.");

                var moved = await MoveToReadyAsync(card, proposal, ct);
                if (moved is not null) return new WatcherDecisionResult(moved.Value, proposal,
                    "The card could not be moved to Ready.");
            }
        }

        var decided = proposal with
        {
            Decision = new WatcherProposalDecision
            {
                State = decision,
                DecidedAtUtc = nowUtc,
                DecidedBy = string.IsNullOrWhiteSpace(decidedBy) ? "operator" : decidedBy.Trim(),
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                MergedIntoTaskKey = string.IsNullOrWhiteSpace(mergeIntoTaskKey) ? null : mergeIntoTaskKey.Trim(),
            },
            UpdatedAtUtc = nowUtc,
        };
        _store.Upsert(decided);

        if (decision == WatcherProposalDecisions.Rejected)
            Suppress(decided, nowUtc);

        UpdateCase(decided, nowUtc);

        if (card is not null)
        {
            _timeline.Append(
                card.FolderPath,
                TimelineEventKinds.WatcherProposalDecided,
                TimelineActors.Human(decided.Decision.DecidedBy ?? "operator"),
                $"The Watcher proposal {decided.Id} was {decision}.",
                details: new Dictionary<string, string>
                {
                    ["caseId"] = decided.CaseId,
                    ["proposalId"] = decided.Id,
                    ["decision"] = decision,
                    ["detectorClass"] = decided.DetectorClass,
                    ["reason"] = decided.Decision.Reason ?? "",
                    ["mergedInto"] = decided.Decision.MergedIntoTaskKey ?? "",
                });
        }

        await _publisher.DecisionRecordedAsync(decided, ct);
        _logger.LogInformation(
            "watcher-decision proposal={ProposalId} case={CaseId} decision={Decision} by={DecidedBy} card={TaskKey}",
            decided.Id,
            decided.CaseId,
            decision,
            decided.Decision.DecidedBy,
            decided.CreatedTaskKey ?? decided.CommentedOnTaskKey);

        return new WatcherDecisionResult(WatcherDecisionStatus.Success, decided);
    }

    /// <summary>
    /// Acceptance evidence for the promotion rule of section 10.4: unedited
    /// acceptances per detector class. Auto-approval is out of scope, so this
    /// only reports; nothing reads it to change behaviour.
    /// </summary>
    public IReadOnlyList<WatcherPromotionEvidence> PromotionEvidence()
    {
        var decided = _store.Proposals()
            .Where(item => item.Decision.State != WatcherProposalDecisions.Pending)
            .ToList();

        return WatcherDetectorClasses.All.Select(detectorClass =>
        {
            var forClass = decided
                .Where(item => string.Equals(item.DetectorClass, detectorClass, StringComparison.Ordinal))
                .ToList();
            return new WatcherPromotionEvidence
            {
                DetectorClass = detectorClass,
                Accepted = forClass.Count(item => item.Decision.State == WatcherProposalDecisions.Approved),
                Edited = forClass.Count(item => item.Decision.State == WatcherProposalDecisions.Edited),
                Merged = forClass.Count(item => item.Decision.State == WatcherProposalDecisions.Merged),
                Rejected = forClass.Count(item => item.Decision.State == WatcherProposalDecisions.Rejected),
                EligibleForAutoApproval = WatcherDetectorClasses.PromotableToAutoApproval
                    .Contains(detectorClass, StringComparer.Ordinal),
            };
        }).ToList();
    }

    private async Task<WatcherDecisionStatus?> MoveToReadyAsync(
        TaskInfo card,
        WatcherProposal proposal,
        CancellationToken ct)
    {
        // The recommended route is applied at approval time, not at proposal
        // time, so an operator who edits the draft still gets the policy route
        // unless they pinned one themselves.
        if (!card.ModelExplicit && !string.IsNullOrWhiteSpace(proposal.Recommendation.Model))
        {
            _mutations.SetJobModel(card.Id, proposal.Recommendation.Model, card.WatchPath);
            if (!string.IsNullOrWhiteSpace(proposal.Recommendation.ThinkingLevel))
                _mutations.SetJobThinkingLevel(card.Id, proposal.Recommendation.ThinkingLevel, card.WatchPath);
        }

        var outcome = await _transitions.MoveAsync(
            card.Id,
            TaskStates.Ready,
            card.WatchPath,
            ct,
            cause: TimelineActors.System,
            reason: $"An operator approved Watcher proposal {proposal.Id} for case {proposal.CaseId}.",
            expectedSourceState: TaskStates.Preparation);
        if (outcome.Status == MoveJobStatus.Success) return null;

        _logger.LogWarning(
            "watcher-approve-move-refused proposal={ProposalId} card={TaskKey} status={Status} message={Message}",
            proposal.Id,
            card.TaskKey,
            outcome.Status,
            outcome.Message);
        return WatcherDecisionStatus.MoveRefused;
    }

    private void Suppress(WatcherProposal proposal, DateTime nowUtc)
    {
        var options = WatcherOptions.FromConfiguration(_configuration);
        _store.Upsert(new WatcherSuppression
        {
            Fingerprint = proposal.Fingerprint,
            DetectorClass = proposal.DetectorClass,
            Reason = proposal.Decision.Reason ?? "rejected without a recorded reason",
            CreatedAtUtc = nowUtc,
            // Suppression always expires. A permanently silent detector would
            // hide the next occurrence of a real problem.
            ExpiresAtUtc = nowUtc + options.SuppressionPeriod,
            CreatedBy = proposal.Decision.DecidedBy,
        });
        _logger.LogInformation(
            "watcher-suppression fingerprint={Fingerprint} detector={DetectorClass} untilUtc={ExpiresAtUtc}",
            proposal.Fingerprint,
            proposal.DetectorClass,
            nowUtc + options.SuppressionPeriod);
    }

    private void UpdateCase(WatcherProposal proposal, DateTime nowUtc)
    {
        var item = _store.Case(proposal.CaseId);
        if (item is null) return;

        var (state, terminal) = proposal.Decision.State switch
        {
            WatcherProposalDecisions.Rejected => (
                WatcherCaseStates.Suppressed,
                (string?)WatcherCasePolicy.TerminalReasons.Suppressed),
            WatcherProposalDecisions.Merged => (
                WatcherCaseStates.Resolved,
                $"Merged into {proposal.Decision.MergedIntoTaskKey} by {proposal.Decision.DecidedBy}."),
            _ => (
                WatcherCaseStates.Resolved,
                $"Proposal {proposal.Id} was {proposal.Decision.State} by {proposal.Decision.DecidedBy}."),
        };

        _store.Upsert(item with
        {
            State = state,
            TerminalReason = terminal,
            UpdatedAtUtc = nowUtc,
        });
    }

    private TaskInfo? FindCard(WatcherProposal proposal)
    {
        var key = proposal.CreatedTaskKey ?? proposal.CommentedOnTaskKey;
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _scanner.ScanAllAutomationJobs()
            .FirstOrDefault(job => string.Equals(job.TaskKey, key, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Per-class decision counts behind the section 10.4 promotion rule.</summary>
public sealed record WatcherPromotionEvidence
{
    public required string DetectorClass { get; init; }
    /// <summary>Approvals without an edit. Only these count toward promotion.</summary>
    public int Accepted { get; init; }
    public int Edited { get; init; }
    public int Merged { get; init; }
    public int Rejected { get; init; }
    /// <summary>False for contradiction and drift, which stay human-approved.</summary>
    public bool EligibleForAutoApproval { get; init; }

    /// <summary>Unedited acceptances the rule asks for before a class may be promoted.</summary>
    public const int PromotionThreshold = 10;

    public int RemainingForPromotion => EligibleForAutoApproval
        ? Math.Max(0, PromotionThreshold - Accepted)
        : PromotionThreshold;

    public string Summary => EligibleForAutoApproval
        ? $"{Accepted}/{PromotionThreshold.ToString(CultureInfo.InvariantCulture)} unedited acceptances"
        : "human-approved by policy";
}
