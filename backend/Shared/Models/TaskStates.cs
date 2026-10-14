namespace AgentStudio.Shared;

/// <summary>
/// String constants and helpers for the optional <c>phase</c> substate on
/// <see cref="TaskInfo"/>. The hybrid V1 model picked in
/// <c>docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md</c> keeps the
/// existing six folder-level states as the durable skeleton and adds this
/// optional substate so the orchestrator-driven lanes (Intake, Post
/// Processing) can be projected by the UI without a filesystem-state
/// explosion.
///
/// Phase values are stored in <c>job.json</c> as plain strings; the wire
/// format is the literal string so hand-edited JSON stays readable. The
/// richer lifecycle history (which intake checks ran, when each phase was
/// entered, last blocking reason) lives in the optional sidecar file
/// <c>lifecycle.json</c> described by <see cref="LifecycleSnapshot"/>.
/// </summary>
public static class LifecyclePhases
{
    // 2-ready substates: which seat in the Ready group a card sits in.
    public const string HumanReady = "human-ready";
    public const string IntakeRunning = "intake-running";
    public const string IntakeBlocked = "intake-blocked";
    /// <summary>
    /// Card passed orchestrator intake; the main coding runner is now allowed
    /// to pick it up. When per-project intake is enabled, the runner skips
    /// 2-ready cards that have not reached this phase. When intake is
    /// disabled (default), the gate is open regardless of phase.
    /// </summary>
    public const string IntakePassed = "intake-passed";

    // 3-progress / 4-auto-review substates: distinguishes "coding CLI is
    // working" from "post-processing pipeline (auto-commit, summary, future
    // checks) is working" without a new filesystem state.
    public const string ExecutionRunning = "execution-running";
    public const string ExecutionStalled = "execution-stalled";
    /// <summary>
    /// The coding CLI has exited after asking for input and the orchestrator is
    /// deciding whether/how to continue the loop. This is an intentional,
    /// visible no-slot state; the next continuation must acquire a fresh slot.
    /// </summary>
    public const string LoopWaiting = "loop-waiting";
    /// <summary>
    /// 3-progress substate for Run-Liveness Slice B (concept Rule 2): the run
    /// asked a steer / NeedsInput question in an auto mode and is waiting for an
    /// answer. Surfaces the "waiting for answer since mm:ss" pill so the wait is
    /// visible instead of an invisible hang. Written next to the durable
    /// <c>steer-pending.json</c> marker; cleared when a new run resumes the card.
    /// </summary>
    public const string SteerPending = "steer-pending";
    /// <summary>
    /// A CodingAgentRunner quota interrupt is waiting for a confirmed nearby
    /// reset. The process is stopped and the same request has a scheduled wake.
    /// </summary>
    public const string QuotaWaiting = "quota-waiting";
    public const string PostProcessingRunning = "post-processing-running";
    public const string PostProcessingBlocked = "post-processing-blocked";
    public const string AwaitingReview = "awaiting-review";
    /// <summary>
    /// Compatibility marker for an integration transaction written by an older
    /// backend process. New acceptance requests never create this phase; the
    /// recovery worker may finish it after restart.
    /// </summary>
    public const string Integrating = "integrating";

    public static readonly string[] All =
    [
        HumanReady, IntakeRunning, IntakeBlocked, IntakePassed,
        ExecutionRunning, ExecutionStalled, LoopWaiting, SteerPending, QuotaWaiting,
        PostProcessingRunning, PostProcessingBlocked, AwaitingReview, Integrating
    ];

    /// <summary>
    /// The phases each filesystem state is allowed to carry. States not in
    /// this map (preparation, the orchestrator-prep lane, the two review
    /// lanes, completed, archive) carry no phase: the
    /// state already says enough. Keeping this small dictionary avoids a
    /// scatter of <c>switch</c> statements when the migration tests and
    /// future frontend lane projection both need to know "is this phase
    /// legal here".
    /// </summary>
    public static readonly Dictionary<string, string[]> AllowedByState = new()
    {
        [TaskStates.Ready] = [HumanReady, IntakeRunning, IntakeBlocked, IntakePassed],
        [TaskStates.Progress] = [ExecutionRunning, ExecutionStalled, LoopWaiting, SteerPending, QuotaWaiting, PostProcessingRunning, PostProcessingBlocked, AwaitingReview],
        [TaskStates.AutoReview] = [PostProcessingRunning, PostProcessingBlocked, AwaitingReview, Integrating],
        [TaskStates.HumanReview] = [Integrating],
    };

    /// <summary>
    /// Pure default-derivation for jobs whose <c>phase</c> is null on disk.
    /// Implements the compatibility contract from
    /// <c>docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md</c>
    /// section 10: a job with no <c>phase</c> renders in the default lane of
    /// its state. Returns null for states that carry no phase (preparation,
    /// the orchestrator-prep lane, the review lanes,
    /// completed, archive).
    /// </summary>
    public static string? DefaultFor(string state, string? executionStatus, TaskSummaryStatus summaryStatus)
    {
        return state switch
        {
            TaskStates.Ready => HumanReady,
            TaskStates.Progress when string.Equals(executionStatus, "running", StringComparison.OrdinalIgnoreCase) => ExecutionRunning,
            TaskStates.Progress when summaryStatus == TaskSummaryStatus.Generating => PostProcessingRunning,
            // Stopped / failed / unfinished runs still live in 3-progress;
            // the existing UI treats them as the execution lane today, so
            // the lane projection keeps that behavior under the new model.
            TaskStates.Progress => ExecutionRunning,
            TaskStates.AutoReview => PostProcessingRunning,
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="phase"/> is empty or in the allowed set for
    /// <paramref name="state"/>. Permissive on a null phase (the state's
    /// default lane covers it) and on unknown future states (no constraint
    /// declared); strict for known task states so review / completed lanes do
    /// not retain stale orchestrator-owned phases.
    /// </summary>
    public static bool IsAllowed(string state, string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return true;
        if (!AllowedByState.TryGetValue(state, out var allowed))
            return !TaskStates.All.Contains(state);
        return allowed.Contains(phase);
    }
}

/// <summary>
/// Optional sidecar file written next to <c>job.json</c> as
/// <c>lifecycle.json</c>. Carries the richer phase history that does not
/// fit on the wire-level <see cref="TaskInfo.Phase"/> field: which intake
/// or post-processing checks were scheduled, when the current phase was
/// entered, and the last blocking reason if any.
///
/// This file is optional; absence means "default phase for the state, no
/// history". The follow-up tasks <c>ready-orchestrator-intake-lane</c>
/// and <c>post-processing-orchestrator-lane</c> populate it. The shape is
/// version-tagged so it can grow without breaking older readers.
/// </summary>
public record LifecycleSnapshot
{
    public int Version { get; init; } = 1;
    /// <summary>The current phase. Mirrors <see cref="TaskInfo.Phase"/>; the wire field is the source of truth.</summary>
    public string? Phase { get; init; }
    /// <summary>UTC time the current phase was entered.</summary>
    public DateTime? PhaseEnteredAt { get; init; }
    /// <summary>Free-form blocking reason when the phase is <see cref="LifecyclePhases.IntakeBlocked"/> or <see cref="LifecyclePhases.PostProcessingBlocked"/>.</summary>
    public string? BlockingReason { get; init; }
    /// <summary>Intake checks scheduled or run for this job, in pipeline order.</summary>
    public List<LifecycleCheck> IntakeChecks { get; init; } = [];
    /// <summary>Post-processing checks scheduled or run for this job, in pipeline order.</summary>
    public List<LifecycleCheck> PostProcessingChecks { get; init; } = [];
    /// <summary>
    /// Context-load manifest produced by intake (resolved/missing cross-references
    /// and prompt attachments). Null when intake has not run or recorded no
    /// context. Informational: the board and a future LLM context-gathering pass
    /// can read what context the card carries and what is still outstanding.
    /// </summary>
    public ContextManifest? Context { get; init; }
    /// <summary>
    /// Constraint/context enrichment selected by intake and foregrounded in the
    /// coding-run prompt. Null when intake has not run or selected no
    /// constraints. The Markdown artifact lives in the job folder at
    /// <see cref="IntakeEnrichmentManifest.ArtifactPath"/> so the exact injected
    /// block is auditable after the fact.
    /// </summary>
    public IntakeEnrichmentManifest? Enrichment { get; init; }
}

/// <summary>
/// Result of the intake <b>context-load</b> step: the cross-references and
/// prompt attachments discovered for a card, each split into the ones that
/// resolve to a known task / on-disk file and the ones still missing, plus the
/// card's tags. Recorded in <see cref="LifecycleSnapshot.Context"/>. Purely
/// informational — missing context does not by itself gate pickup; the typed
/// intake verdict owns the gate. The shape is deterministic so the context-load
/// step is unit-testable and can later be enriched by a model call without
/// changing the sidecar contract.
/// </summary>
public record ContextManifest
{
    /// <summary>Reference edges (formatted <c>kind:target</c>) that resolved to a known task in scope.</summary>
    public List<string> ResolvedReferences { get; init; } = [];
    /// <summary>Reference edges (formatted <c>kind:target</c>) with no matching known task in scope.</summary>
    public List<string> MissingReferences { get; init; } = [];
    /// <summary>Attachment paths referenced by the prompt that exist on disk.</summary>
    public List<string> ResolvedAttachments { get; init; } = [];
    /// <summary>Attachment paths referenced by the prompt with no file on disk.</summary>
    public List<string> MissingAttachments { get; init; } = [];
    /// <summary>The card's tags, surfaced as part of the loaded context.</summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>True when every referenced task and attachment was resolved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComplete => MissingReferences.Count == 0 && MissingAttachments.Count == 0;
}

/// <summary>
/// Result of the intake constraint/context enrichment step. The step selects a
/// small set of repository-wide constraints that are relevant to the task and
/// writes the rendered block to <see cref="ArtifactPath"/> under the job folder.
/// </summary>
public record IntakeEnrichmentManifest
{
    public int Version { get; init; } = 3;
    /// <summary>Relative Markdown artifact path inside the job folder.</summary>
    public string ArtifactPath { get; init; } = "";
    /// <summary>Selector implementation that produced this manifest.</summary>
    public string Selector { get; init; } = "";
    /// <summary>Detected task areas that drove relevance selection.</summary>
    public List<string> Areas { get; init; } = [];
    /// <summary>Cached style-guide catalogue snapshot used for this selection, when available.</summary>
    public string? StyleGuideSnapshotId { get; init; }
    /// <summary>Constraints injected into the coding-run prompt.</summary>
    public List<IntakeConstraintSelection> Constraints { get; init; } = [];
    /// <summary>Hard rendered-character ceiling applied by the selector.</summary>
    public int CharacterBudget { get; init; }
    /// <summary>Conservative planning estimate derived from the character ceiling.</summary>
    public int EstimatedTokenBudget { get; init; }
    /// <summary>Maximum number of non-mandatory context blocks that may be appended.</summary>
    public int OptionalBlockLimit { get; init; }
    /// <summary>Final rendered artifact length after deterministic omissions.</summary>
    public int UsedCharacters { get; init; }
    /// <summary>Estimated final token count using the documented four-characters-per-token rule.</summary>
    public int EstimatedTokens { get; init; }
    /// <summary>Bounded audit rows for relevant constraints omitted by the hard budget.</summary>
    public List<IntakeConstraintOmission> Omissions { get; init; } = [];
    /// <summary>All matching blocks rejected because their source or catalogue was not valid for this project.</summary>
    public List<IntakeConstraintOmission> SourceRejections { get; init; } = [];
    /// <summary>Further omissions summarized after the bounded audit-row limit.</summary>
    public int AdditionalOmissionCount { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Constraints.Count == 0;
}

/// <summary>Audit record for a relevant constraint that could not fit the prompt budget.</summary>
public record IntakeConstraintOmission
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Reason { get; init; } = "";
    public string? MissingPath { get; init; }
    public int EstimatedCharacters { get; init; }
    public int EstimatedTokens { get; init; }
}

/// <summary>One repository-wide constraint selected for a task by intake.</summary>
public record IntakeConstraintSelection
{
    /// <summary>Stable id used by tests, audits, and future UI chips.</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string Source { get; init; } = "";
    /// <summary>Source-owned revision used to invalidate cached enrichment decisions.</summary>
    public string Revision { get; init; } = "1";
    /// <summary>Mandatory project policy or optional task-specific context.</summary>
    public string Tier { get; init; } = "optional";
    /// <summary>How the selector accepted this source for the target project.</summary>
    public string SourceVerification { get; init; } = "repository";
    /// <summary>Mandatory blocks do not consume the optional-block allowance.</summary>
    public bool Mandatory { get; init; }
    /// <summary>Deterministic selection priority. Lower values are selected first.</summary>
    public int Priority { get; init; } = 100;
    public List<string> Areas { get; init; } = [];
}

/// <summary>One scheduled or completed check inside a <see cref="LifecycleSnapshot"/>.</summary>
public record LifecycleCheck
{
    public string Name { get; init; } = "";
    /// <summary>One of: <c>pending</c>, <c>running</c>, <c>completed</c>, <c>passed</c>, <c>failed</c>, <c>skipped</c>.</summary>
    public string Status { get; init; } = "pending";
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Typed evidence row written by orchestrator-owned post-processing after the
/// core coding CLI has finished. Rows live in
/// <c>post-processing-outcomes.jsonl</c> in the task folder. The row records
/// which supporting identity performed the check and which state-machine
/// outcome it produced; it never authorizes source edits.
/// </summary>
public record PostProcessingOutcomeRecord
{
    public int Version { get; init; } = 1;
    public DateTime At { get; init; } = DateTime.UtcNow;
    public string JobId { get; init; } = "";
    public string Project { get; init; } = "";
    public string Outcome { get; init; } = PostProcessingOutcomes.PassToHumanReview;
    public string Performer { get; init; } = PostProcessingPerformers.Orchestrator;
    public string? PerformerCliType { get; init; }
    public string? StepId { get; init; }
    public string? Summary { get; init; }
    public string? EvidenceRef { get; init; }
    public List<string> FindingRefs { get; init; } = [];
    public List<string> FollowUpTaskIds { get; init; } = [];
}

public static class PostProcessingOutcomes
{
    public const string PassToHumanReview = "pass-to-human-review";
    public const string FindingsAdded = "findings-added";
    public const string NeedsFollowUpTask = "needs-follow-up-task";
    public const string NeedsHumanInput = "needs-human-input";
    public const string FailedPostProcessing = "failed-post-processing";

    public static readonly string[] All =
    [
        PassToHumanReview,
        FindingsAdded,
        NeedsFollowUpTask,
        NeedsHumanInput,
        FailedPostProcessing,
    ];
}

public static class PostProcessingPerformers
{
    public const string Orchestrator = "orchestrator";
    public const string SupportingAgent = "supporting-agent";
    public const string Tool = "tool";
}

/// <summary>
/// Wire shape for <see cref="AgentStudio.Runner.StuckLoopState"/>
/// served to the frontend. A separate record so the wire contract is
/// stable even if the in-memory record gains internal fields.
/// </summary>
public record AutoLoopSnapshot
{
    public int Iteration { get; init; }
    public int MaxIterations { get; init; }
    public long TokensUsed { get; init; }
    public long MaxTokens { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime LastAt { get; init; }
    public string? LastQuestion { get; init; }
    public string? LastReply { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Well-known task-slug prefixes that carry semantic meaning across the
/// pipeline. Kept next to <see cref="TaskStates"/> so the runner, the
/// orchestrator-prep rules, and the workspace summary agree on one spelling.
/// </summary>
public static class TaskSlugs
{
    /// <summary>
    /// Prefix the orchestrator stamps on a card that exists only so a human
    /// can make a call the automation must not. Such a card is never
    /// machine-actionable: the runner's pickup sweep herds it to
    /// <see cref="TaskStates.Escalated"/> regardless of autonomy level (the
    /// former 1b-needs-human-review bounce lane was retired), and the runner
    /// refuses to auto-pick it out of
    /// <see cref="TaskStates.Ready"/> (which would NOOP-burn a CLI run and
    /// trip the cross-slug infra circuit breaker).
    /// </summary>
    public const string HumanDecisionNeededPrefix = "human-decision-needed-";

    /// <summary>True when <paramref name="slug"/> names a human-decision-needed card.</summary>
    public static bool IsHumanDecisionNeeded(string? slug) =>
        !string.IsNullOrEmpty(slug)
        && slug.StartsWith(HumanDecisionNeededPrefix, System.StringComparison.OrdinalIgnoreCase);
}
