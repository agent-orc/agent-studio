using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Categories for a system-initiated escalation to <c>5e-escalated</c>. The
/// value is carried in the <see cref="ReviewDecisionRecord.Reason"/> (see
/// <see cref="HumanReviewEscalation.FormatReason"/>) and in the status.md stub
/// so the board can say WHY a card was parked even though no agent review ran.
/// </summary>
public static class HumanReviewEscalationCategories
{
    /// <summary>Fallback for legacy auto-review escalation paths.</summary>
    public const string AutoReviewEscalation = "auto-review-escalation";
    /// <summary>A failure boundary raised a deduplicated orchestrator follow-up task.</summary>
    public const string FailureIntervention = "failure-intervention";

    /// <summary>A remote coding claim could not prepare its repository or
    /// execution environment after the durable per-task retry budget.</summary>
    public const string RemoteClaimEnvironment = "remote-claim-environment";

    /// <summary>The agent explicitly reported that it could not proceed.</summary>
    public const string AgentBlocked = "agent-blocked";

    /// <summary>The remote agent explicitly requested an operator choice.</summary>
    public const string AgentNeedsInput = "agent-needs-input";

    /// <summary>The agent needs information automation could not derive safely.</summary>
    public const string NeedsHumanInput = "needs-human-input";

    /// <summary>The deterministic completion gate still found unfinished work
    /// after the bounded reissue budget was exhausted.</summary>
    public const string CompletionGateUnresolved = "completion-gate-unresolved";

    /// <summary>A run exhausted its bounded recovery budget without emitting a
    /// recognized terminal completion signal.</summary>
    public const string NoCompletionSignal = "no-completion-signal";

    /// <summary>A remote run ended without a recognized terminal outcome.</summary>
    public const string RemoteOutcomeUnknown = "remote-outcome-unknown";

    /// <summary>The task server could not resolve a cloneable repository for a
    /// remotely assigned task.</summary>
    public const string RemoteRepositoryUnavailable = "remote-repository-unavailable";

    /// <summary>The remote runner exhausted its bounded clone/fetch/worktree
    /// preparation attempts before the agent process could start.</summary>
    public const string RemoteEnvironmentPreparation = "remote-environment-preparation";

    /// <summary>An out-of-band completion call explicitly parked a problem
    /// rather than submitting a finished delivery for acceptance.</summary>
    public const string ExternalCompletionBlocked = "external-completion-blocked";

    /// <summary>AGT-2220: a completion claim whose commits could not be proven
    /// to exist in the target repository. The card is parked honestly instead of
    /// being stamped completed.</summary>
    public const string UnverifiedDelivery = "unverified-delivery";

    public const string WatchdogKill = "watchdog-kill";
    public const string PermissionBlocked = "permission-blocked";
    public const string EnvironmentBlocker = "environment-blocker";
    public const string AutoFailurePark = "auto-failure-park";
    public const string PickupZombie = "pickup-zombie";
    /// <summary>A task worktree remained locked after bounded cleanup retries.
    /// The busy path is included in the escalation reason for operator action.</summary>
    public const string WorktreeBlocked = "worktree-blocked";
    public const string EmptyFastExit = "empty-fast-exit";

    /// <summary>The agent CLI process died hard (exitCode &lt; 0) before it could
    /// reach a terminal verdict. An infra fault, not a logical failure; routed to
    /// operator intervention rather than left stranded in 3-progress.</summary>
    public const string InfraCrash = "infra-crash";

    /// <summary>The run failed and produced real text that maps to no terminal
    /// verdict. The orchestrator could not conclude it, so it stops and hands the
    /// task to an operator (replaces the old classifier-unknown stranding).</summary>
    public const string OrchestratorInconclusive = "orchestrator-inconclusive";

    /// <summary>The run exceeded the model's input window (prompt too long /
    /// context length). Non-retryable, so it is routed straight to Escalated
    /// instead of being re-issued into the same overflow.</summary>
    public const string ContextOverflow = "context-overflow";

    /// <summary>The configured model is invalid/unsupported for this account or
    /// CLI (invalid_request / HTTP 400 "model not supported"). Non-retryable:
    /// re-issuing spawns into the same 400, so it is routed to Escalated with
    /// a clear model-invalid reason instead of the orchestrator-inconclusive
    /// catch-all.</summary>
    public const string ModelInvalid = "model-invalid";

    /// <summary>The account's usage/session/rate-limit budget is exhausted.
    /// Transient (clears when the quota window resets); escalated with an honest
    /// quota-exhausted reason so a human can re-queue after reset instead of
    /// mistaking it for an orchestrator-inconclusive failure.</summary>
    public const string QuotaExhausted = "quota-exhausted";

    /// <summary>A transient environmental fault (host file lock / MSB302x, network
    /// glitch) that persisted after the orchestrator's bounded retry-with-backoff.
    /// Flagged environmental so a reviewer reads it as an infra problem to retry,
    /// not a failed change (AGT-1944).</summary>
    public const string Environmental = "environmental";

    /// <summary>The agent CLI could not launch or resume its session even after an
    /// automatic fresh-start retry (a dead session after a backend restart, a
    /// rejected resume id). Distinct from the generic auto-failure park so the
    /// board shows the recoverable host/CLI cause (AGT-1944; belege
    /// AGT-1945/1929/1930).</summary>
    public const string CliLaunchFailed = "cli-launch-failed";

    /// <summary>The agent CLI could not launch because its OAuth session expired
    /// and the token refresh failed. Non-retryable and shared across every
    /// parallel run, so the orchestrator STOPS immediately (breaker) and escalates
    /// with a re-auth instruction instead of burning further launches - the
    /// AGT-2066 token-roulette signature (17 cards drained on 2026-07-10).</summary>
    public const string AuthRefreshFailed = "auth-refresh-failed";

    /// <summary>The run could not be mapped to a terminal verdict, but it left
    /// files in <c>results/</c>. Routed to Escalated WITH a "there is partial
    /// work to inspect" hint rather than a bare inconclusive park, so a reviewer
    /// looks at the deliverables before deciding (AGT-1944 taxonomy:
    /// inconclusive-with-results).</summary>
    public const string InconclusiveWithResults = "inconclusive-with-results";

    /// <summary>The per-task circuit breaker tripped after N consecutive failed
    /// runs without progress; the task was parked to stop an endless reissue
    /// loop.</summary>
    public const string Quarantined = "quarantined";

    /// <summary>The worker CLI advanced git history during its own run, bypassing
    /// the platform-owned commit/push boundary.</summary>
    public const string AgentGitViolation = "agent-git-violation";

    /// <summary>A card carrying the human-decision-needed marker: it exists for
    /// a person to decide, never for an agent to run. Routed to 5e-escalated
    /// after the retired 1b-needs-human-review lane was removed.</summary>
    public const string HumanDecisionNeeded = "human-decision-needed";

    /// <summary>The deterministic integration recovery rail exhausted its
    /// configured conflict-requeue budget.</summary>
    public const string IntegrationRecoveryExhausted = "integration-recovery-exhausted";

    /// <summary>An unanswered steer / NeedsInput question timed out (Run-Liveness
    /// Slice B, concept Rule 2) and the answer was not derivable from the task
    /// context. Routed to 5e-escalated with a clear reason instead of waiting
    /// indefinitely (belegt 2062/2067/2068, 2026-07-10).</summary>
    public const string SteerUnanswered = "steer-unanswered";

    /// <summary>The bounded UI feedback loop exhausted its configured cap, or
    /// repeatedly failed to produce the mandatory iteration evidence.</summary>
    public const string UiIterationCap = "ui-iteration-cap";

    /// <summary>A review subject can never be materialized - either because the
    /// pre-plane source completion has no immutable Result-Envelope, or because
    /// all bounded infrastructure retries for that subject were exhausted.</summary>
    public const string ReviewSubjectUnmaterializable = "review-subject-unmaterialisierbar";

    /// <summary>Retroactive category for cards parked in 5-human-review before
    /// the escalation funnel existed (boot-time backfill).</summary>
    public const string UnknownLegacy = "unknown-legacy";
}

/// <summary>
/// The single funnel every SYSTEM-initiated move into <c>5e-escalated</c>
/// must pass through. It writes both halves of the board contract that the
/// agent-driven <see cref="ReviewDecisionOrchestrator"/> already writes for its
/// own promotions:
/// <list type="number">
///   <item>an <see cref="ReviewDecisionKind.Escalate"/>
///   <see cref="ReviewDecisionRecord"/> in the per-project decision journal, so
///   the endpoint-derived <c>OrchestratorVerdict</c> is <c>escalate</c> rather
///   than <c>null</c>; and</item>
///   <item>a minimal <c>status.md</c> stub in the moved folder, so
///   <c>StatusMarkdown</c> is not empty - written only when the folder has no
///   real summary yet, so a genuine summary is never clobbered.</item>
/// </list>
///
/// <para>Before this funnel existed, three <see cref="ProjectRunner"/> paths
/// (watchdog kill / permission / environment block, the auto-failure park, and
/// the over-budget pickup zombie escalation) moved a folder straight from
/// 3-progress into the operator-intervention path without either half, producing cards the
/// board could not explain - the bug this funnel fixes. The
/// <c>HumanReviewVerdictDriftTest</c> mechanically forbids any new move into the
/// lane from outside this file and the orchestrator.</para>
/// </summary>
public sealed class HumanReviewEscalation
{
    private const string SystemPrompt = "(deterministic system escalation)";
    private const string NoModelResponse = "(no fast-model call)";

    private readonly TaskTransitionService _transitions;
    private readonly string? _workspaceRoot;
    private readonly ILogger _logger;
    private readonly TaskScannerService? _scanner;
    private readonly WorkspaceArtifactCommitService? _workspaceArtifactCommits;

    /// <summary>DI ctor: the workspace root is the same <c>TaskRepository</c> the
    /// verdict-read side (<c>TaskEndpointHelpers.BuildOrchestratorVerdictLookup</c>)
    /// uses, so the journal this funnel writes is the journal the board reads.</summary>
    public HumanReviewEscalation(
        TaskStateMachine states,
        TaskTransitionService transitions,
        IConfiguration configuration,
        ILogger<HumanReviewEscalation> logger,
        TaskScannerService? scanner = null,
        WorkspaceArtifactCommitService? workspaceArtifactCommits = null)
        : this(states, transitions, configuration["TaskRepository"], logger, scanner, workspaceArtifactCommits)
    {
    }

    /// <summary>Explicit-root ctor for tests and any caller that already resolved
    /// the workspace root.</summary>
    public HumanReviewEscalation(
        TaskStateMachine states,
        TaskTransitionService transitions,
        string? workspaceRoot,
        ILogger logger,
        TaskScannerService? scanner = null,
        WorkspaceArtifactCommitService? workspaceArtifactCommits = null)
    {
        _transitions = transitions;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
        _scanner = scanner;
        _workspaceArtifactCommits = workspaceArtifactCommits;
    }

    /// <summary>
    /// Move <paramref name="jobId"/> into 5e-escalated through
    /// <see cref="TaskTransitionService.MoveAsync"/> (so the
    /// <c>OnJobMoved</c> side effects fire: the runner's active-job latch is
    /// cleared and the board gets a live SignalR push), then record the verdict
    /// and status stub. Used by the in-runner escalation paths that run inside
    /// the async completion flow.
    /// </summary>
    public async Task<MoveJobOutcome> EscalateAsync(
        string jobId, string watchPath, string project,
        string category, string reason, CancellationToken ct = default,
        AttemptWriteReference? authorityWrite = null,
        string? statusDetail = null)
    {
        var beforeFolder = ResolveSourceFolder(jobId, watchPath);
        // Write the Result scaffold before the lane mutation. The folder moves
        // with status.md on success, and a refused/interrupted move still leaves
        // reviewable evidence at the source instead of a verdict-less card.
        if (!string.IsNullOrWhiteSpace(beforeFolder))
            WriteStatusStubIfMissing(beforeFolder, category, reason, statusDetail);
        var outcome = await _transitions.MoveAsync(
            jobId,
            TaskStates.Escalated,
            watchPath,
            ct,
            cause: TimelineActors.System,
            reason: reason,
            authorityWrite: authorityWrite,
            suppressProductExecution: authorityWrite is not null,
            transitionCause: LaneChangeCauses.Escalated,
            transitionDetail: category);
        if (outcome.Status == MoveJobStatus.Success)
        {
            RecordVerdictAndStatus(project, jobId, outcome.NewFolderPath, category, reason, statusDetail);
            if (CarriesNeedsInputArtifact(category) && outcome.NewFolderPath is not null)
                NeedsInputArtifact.ReferenceFromParkedBlocker(outcome.NewFolderPath, _logger);
            TryCommitArtifacts(project, jobId, beforeFolder, outcome.NewFolderPath);
        }
        else
            _logger.LogWarning(
                "HumanReviewEscalation: move of {Project}/{JobId} to 5e-escalated failed: {Status} {Message}",
                project, jobId, outcome.Status, outcome.Message);
        return outcome;
    }

    /// <summary>
    /// Synchronous variant for the pickup loop. It blocks only on the shared
    /// transition service so the Result invariant and move notifications remain
    /// identical to the asynchronous path.
    /// </summary>
    public MoveJobOutcome Escalate(
        string jobId, string watchPath, string project,
        string category, string reason)
    {
        var beforeFolder = ResolveSourceFolder(jobId, watchPath);
        if (!string.IsNullOrWhiteSpace(beforeFolder))
            WriteStatusStubIfMissing(beforeFolder, category, reason);
        var outcome = _transitions.MoveAsync(
                jobId,
                TaskStates.Escalated,
                watchPath,
                CancellationToken.None,
                cause: TimelineActors.System,
                reason: reason,
                transitionCause: LaneChangeCauses.Escalated,
                transitionDetail: category)
            .GetAwaiter()
            .GetResult();
        if (outcome.Status == MoveJobStatus.Success)
        {
            RecordVerdictAndStatus(project, jobId, outcome.NewFolderPath, category, reason);
            if (CarriesNeedsInputArtifact(category) && outcome.NewFolderPath is not null)
                NeedsInputArtifact.ReferenceFromParkedBlocker(outcome.NewFolderPath, _logger);
            TryCommitArtifacts(project, jobId, beforeFolder, outcome.NewFolderPath);
        }
        else
            _logger.LogWarning(
                "HumanReviewEscalation: move of {Project}/{JobId} to 5e-escalated failed: {Status} {Message}",
                project, jobId, outcome.Status, outcome.Message);
        return outcome;
    }

    private static bool CarriesNeedsInputArtifact(string category)
        => category is HumanReviewEscalationCategories.AgentNeedsInput
            or HumanReviewEscalationCategories.NeedsHumanInput
            or HumanReviewEscalationCategories.SteerUnanswered;

    /// <summary>
    /// Journal half of the board contract for a REMOTE v1 review verdict that
    /// parks a card in 5-human-review. Without this record the boot-time
    /// verdict-less backfill later misreads the freshly reviewed card as
    /// pre-funnel legacy and escalates it (observed 28.07.: three Pass-reviewed
    /// cards bounced to 5e on restart). Pass maps to AcceptAsDone; a
    /// ProductFailure/Inconclusive park maps to Escalate - both make the
    /// endpoint-derived OrchestratorVerdict non-null.
    ///
    /// <para><paramref name="attemptChain"/> appends the attempt-chain headline
    /// to the reason so the park is described by the NEWEST attempt and every
    /// distinct failure classification, not by whichever classification happens
    /// to be the most frequent (AGT-2220).</para>
    /// </summary>
    public void RecordRemoteReviewParkVerdict(
        string project, string jobId, string? folderPath, string outcome, string summary,
        ReviewAttemptChainSummary? attemptChain = null)
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot) || string.IsNullOrWhiteSpace(project))
        {
            _logger.LogDebug(
                "HumanReviewEscalation: TaskRepository not configured or project empty; skipped remote-review verdict journal for {JobId}.",
                jobId);
            return;
        }
        try
        {
            var pass = string.Equals(outcome, "Pass", StringComparison.OrdinalIgnoreCase);
            var chain = attemptChain is null ? string.Empty : " " + attemptChain.Headline;
            ReviewDecisionLog.Append(_workspaceRoot!, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: jobId,
                Project: project,
                Kind: pass ? ReviewDecisionKind.AcceptAsDone : ReviewDecisionKind.Escalate,
                Reason: $"Remote v1 review parked the card in human review with outcome {outcome}.{chain}",
                Prompt: "(remote v1 review plane)",
                Response: string.IsNullOrWhiteSpace(summary) ? $"outcome={outcome}" : summary,
                FollowUp: string.Empty)
            {
                AttemptEpoch = OperatorReviewRequeueService.ReadEpoch(folderPath),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "HumanReviewEscalation: failed to append remote-review verdict for {Project}/{JobId} (outcome={Outcome})",
                project, jobId, outcome);
        }
    }

    /// <summary>
    /// Verdict + status only, no move. Used by the boot-time backfill for cards
    /// that are ALREADY parked in 5-human-review with no verdict (the legacy
    /// cards this fix repairs). Idempotent on the status half (it never
    /// overwrites a non-empty status.md); the journal append is additive, so the
    /// caller must gate on "no existing verdict" before calling.
    /// </summary>
    public void RecordVerdictAndStatus(
        string project, string jobId, string? folderPath, string category, string reason,
        string? statusDetail = null)
    {
        if (!string.IsNullOrWhiteSpace(_workspaceRoot) && !string.IsNullOrWhiteSpace(project))
        {
            try
            {
                ReviewDecisionLog.Append(_workspaceRoot!, new ReviewDecisionRecord(
                    CreatedAt: DateTime.UtcNow,
                    JobId: jobId,
                    Project: project,
                    Kind: ReviewDecisionKind.Escalate,
                    Reason: FormatReason(category, reason),
                    Prompt: SystemPrompt,
                    Response: NoModelResponse,
                    FollowUp: string.Empty)
                {
                    AttemptEpoch = OperatorReviewRequeueService.ReadEpoch(folderPath),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "HumanReviewEscalation: failed to append Escalate verdict for {Project}/{JobId} (category={Category})",
                    project, jobId, category);
            }
        }
        else
        {
            _logger.LogDebug(
                "HumanReviewEscalation: TaskRepository not configured or project empty; skipped verdict journal for {JobId} (category={Category}).",
                jobId, category);
        }

        if (!string.IsNullOrWhiteSpace(folderPath))
            WriteStatusStubIfMissing(folderPath!, category, reason, statusDetail);
    }

    /// <summary>Encodes the category into the verdict reason so the decision
    /// journal carries the cause, e.g. <c>[watchdog-kill] CLI exceeded the
    /// watchdog deadline</c>.</summary>
    public static string FormatReason(string category, string reason)
    {
        var c = string.IsNullOrWhiteSpace(category) ? HumanReviewEscalationCategories.UnknownLegacy : category.Trim();
        var r = (reason ?? string.Empty).Trim();
        return r.Length == 0 ? $"[{c}]" : $"[{c}] {r}";
    }

    /// <summary>
    /// Enforces the lane contract for a decision-journal escalation. Existing
    /// typed reasons are preserved; untyped auto-review reasons receive the
    /// stable fallback category, and a category-only reason receives a concrete
    /// sentence. This keeps every newly-written 5e card explainable even while
    /// older specialized paths are migrated incrementally.
    /// </summary>
    public static string EnsureFormattedReason(
        string? reason,
        string fallbackCategory = HumanReviewEscalationCategories.AutoReviewEscalation)
    {
        var value = (reason ?? string.Empty).Trim();
        if (!value.StartsWith("[", StringComparison.Ordinal))
            return FormatReason(fallbackCategory, value);

        var close = value.IndexOf(']');
        if (close <= 1)
            return FormatReason(fallbackCategory, value);

        var category = value[1..close].Trim();
        var sentence = value[(close + 1)..].Trim();
        return FormatReason(category, sentence);
    }

    /// <summary>Builds the minimal status.md the board renders for an
    /// escalated-without-review card: a <c>- Result:</c> line (same shape the
    /// generated summaries use), the category, the reason, and a pointer to the
    /// logs and the decision journal.
    ///
    /// <para><paramref name="detail"/> carries an optional pre-rendered markdown
    /// block (today: the review attempt-chain summary of
    /// <see cref="ReviewAttemptChainSummary.Detail"/>) that a one-line reason
    /// cannot hold. It is appended AFTER the <c>- Category:</c> / <c>- Reason:</c>
    /// pair and must never introduce a second line of either shape: the board's
    /// <c>parseStatusStubEscalation</c> lifts exactly those two lines back
    /// out.</para></summary>
    public static string BuildStatusStub(
        string category, string reason, bool partialResultsPresent = false, string? detail = null)
    {
        var c = string.IsNullOrWhiteSpace(category) ? HumanReviewEscalationCategories.UnknownLegacy : category.Trim();
        var r = (reason ?? string.Empty).Trim();
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.Append("# Status").Append(nl).Append(nl);
        sb.Append("- Result: Escalated to human decision (").Append(c).Append(')').Append(nl).Append(nl);
        // When a dying run left files in results/, say so: "no agent-written
        // summary" made AGT-1917 look twice as lost as it was. Surfacing the
        // partial results tells the reviewer there is work to inspect before
        // deciding (docs/concepts/out-of-band-task-completion.md §3, last para).
        if (partialResultsPresent)
            sb.Append("This card was routed to 5e-escalated by the orchestrator runtime without an automated quality review, so there is no agent-written summary - but partial results are present in `results/`, review them before deciding.")
              .Append(nl).Append(nl);
        else
            sb.Append("This card was routed to 5e-escalated by the orchestrator runtime without an automated quality review, so there is no agent-written summary.")
              .Append(nl).Append(nl);
        sb.Append("- Category: ").Append(c).Append(nl);
        if (r.Length > 0)
            sb.Append("- Reason: ").Append(r).Append(nl);
        var d = (detail ?? string.Empty).Trim();
        if (d.Length > 0)
            sb.Append(d).Append(nl);
        sb.Append("- See `logs/` in this folder for the run output, and the project decision journal (`logs/decisions/<project>.jsonl`) for the escalation record.")
          .Append(nl);
        return sb.ToString();
    }

    private void WriteStatusStubIfMissing(
        string folderPath, string category, string reason, string? detail = null)
    {
        var path = Path.Combine(folderPath, "status.md");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(existing)
                    && !IsPendingPlaceholder(existing))
                    return; // never clobber a real summary
            }
            Directory.CreateDirectory(folderPath);
            File.WriteAllText(path, BuildStatusStub(category, reason, HasPartialResults(folderPath), detail));
        }
        catch (Exception ex)
        {
            // Best-effort: the verdict already records the escalation; an
            // unwritable status.md must not crash the runner.
            _logger.LogWarning(ex, "HumanReviewEscalation: failed to write status.md stub at {Path}", path);
        }
    }

    private static bool IsPendingPlaceholder(string status)
    {
        var value = status.Replace("\r", string.Empty).Trim();
        return value.Equals("Result: pending.", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Result: pending", StringComparison.OrdinalIgnoreCase)
            || value.Equals("- Result: pending.", StringComparison.OrdinalIgnoreCase)
            || value.Equals("- Result: pending", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveSourceFolder(string jobId, string watchPath)
    {
        var projected = _scanner?.FindJob(jobId, watchPath)?.FolderPath;
        if (!string.IsNullOrWhiteSpace(projected)) return projected;

        // Explicit-root/test callers do not always inject a scanner. Resolve
        // the current lane without mutating anything so the pre-move Result
        // guarantee still applies to those paths.
        foreach (var state in TaskStates.All)
        {
            var candidate = Path.Combine(watchPath, state, jobId);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// True when the task's <c>results/</c> directory holds at least one file -
    /// i.e. a dying run left partial deliverables the reviewer should see.
    /// Best-effort and fails closed (no results claim on an unreadable dir):
    /// a wrong "partial results present" line is worse than a missing one.
    /// </summary>
    private static bool HasPartialResults(string folderPath)
    {
        try
        {
            var resultsDir = AgentStudio.Tasks.TaskPaths.ResultsDir(folderPath);
            return Directory.Exists(resultsDir)
                && Directory.EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "HumanReviewEscalation: best-effort partial-results probe for the status stub.");
            return false;
        }
    }

    private void TryCommitArtifacts(string project, string jobId, string? beforeFolderPath, string? afterFolderPath)
    {
        var result = _workspaceArtifactCommits?.TryCommitRunBoundary(
            _workspaceRoot,
            jobId,
            beforeFolderPath,
            afterFolderPath,
            ReviewDecisionKind.Escalate);
        if (result is { Success: false })
        {
            _logger.LogWarning(
                "HumanReviewEscalation: workspace artifact commit failed for {Project}/{JobId}: {Error}",
                project, jobId, result.Error);
        }
    }
}
