using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Why an upcoming CLI run was triggered. The planner uses this single
/// dimension to decide between the start-shaped and continue-shaped
/// branches; everything else (session state, job state, CLI compatibility)
/// is plain data. Keeping the trigger reason explicit in one enum is what
/// lets <see cref="RunPlanner.PlanRun"/> own the whole decision tree
/// instead of having the start and continue endpoints reinvent it
/// independently.
/// </summary>
public enum RunIntent
{
    /// <summary>User clicked Play / hit /jobs/{id}/start.</summary>
    ManualStart,
    /// <summary>Auto-pickup tick chose this job from the ready queue.</summary>
    AutoPickup,
    /// <summary>User typed in the chat / hit /jobs/{id}/continue with a follow-up.</summary>
    UserContinue
}

/// <summary>
/// Pure description of what a single CLI invocation should do. Produced by
/// <see cref="RunPlanner.PlanRun"/> from inputs (intent, job state, session
/// state, CLI capabilities) and consumed by <see cref="ProjectRunner"/>,
/// which then applies side-effects (state moves, scanner writes, log writes,
/// event append, CLI start). Splitting plan from apply keeps the decision
/// tree fully unit-testable without mocking the scanner or CLI.
/// </summary>
public sealed record RunPlan(
    string? PromptTemplate,
    IReadOnlyDictionary<string, string?> PromptVariables,
    string? PromptOverride,
    string? SessionToResume,
    bool ResumeFlag,
    string EventKind,
    string? EventReason,
    string? EventInputSessionId,
    bool MoveJobToProgress,
    bool MarkSessionChainRecovery,
    bool WriteCutMarker,
    string? CutMarkerReason,
    string? PersistSessionName,
    bool ClearStaleSessionName)
{
    /// <summary>Business trigger, independent from resume/recovery session mechanics.</summary>
    public RunTriggerMetadata TriggerMetadata { get; init; } = new(
        RunTriggers.Initial, "pipeline", "Initial task run.");
    /// <summary>
    /// Non-null only when this invocation was assigned to the bounded reissue
    /// prompt experiment. This metadata does not participate in model routing,
    /// review, gate, or pipeline selection.
    /// </summary>
    public ReissuePromptAssignment? ReissuePromptAssignment { get; init; }
}

/// <summary>
/// Pure decision library for runner invocations - no I/O, no field access,
/// no DI. Owns the full mapping from (intent × job state × session state ×
/// CLI compatibility) to a <see cref="RunPlan"/>, plus the prompt builders
/// and slug heuristics those decisions reference.
///
/// Why this is a separate static class: the previous design hid this logic
/// inside a stateful runner that also managed lifecycle and side-effects,
/// so the start and continue endpoints reinvented the decision tree
/// independently - and a recovery fix landed only on one side, producing
/// the "no session yet" 400 the user reported. Pulling the planner out as
/// a pure library makes that divergence structurally impossible: there is
/// only one function to change, and the matrix in <c>TaskRunnerPlanTests</c>
/// locks every cell of the table.
/// </summary>
public static class RunPlanner
{
    /// <summary>
    /// Replace a planned resume with the same full-context recovery plan used
    /// when a stored session is absent. Callers use this when an execution-time
    /// precondition (for example a missing Codex rollout in the effective
    /// CODEX_HOME) proves that the otherwise valid session id cannot be opened.
    /// </summary>
    public static RunPlan FallBackToRecovery(
        RunPlan plan,
        string promptPath,
        string jobFolder,
        string? followupPrompt,
        string reason)
        => plan with
        {
            PromptTemplate = RuntimePromptService.RunnerRecoveryContinuation,
            PromptVariables = PromptVariables(promptPath, jobFolder, followupPrompt ?? string.Empty),
            PromptOverride = null,
            SessionToResume = null,
            ResumeFlag = false,
            EventKind = "recovery",
            EventReason = reason,
            EventInputSessionId = null,
            MarkSessionChainRecovery = true,
            WriteCutMarker = true,
            CutMarkerReason = reason,
            PersistSessionName = null,
            ReissuePromptAssignment = null,
        };

    /// <summary>
    /// Maps a trigger + observed state to a fully-described <see cref="RunPlan"/>.
    /// Called from <see cref="ProjectRunner.RunCliAsync"/>; never throws, never
    /// returns null - the contract is "always produces a runnable plan", which
    /// is the property that closes the "no session yet" bug class.
    /// </summary>
    public static RunPlan PlanRun(
        RunIntent intent,
        string initialState,
        string? sessionName,
        string cliType,
        Func<string?, bool> isCompatibleSessionName,
        string jobId,
        string promptPath,
        string jobFolder,
        string? followupPrompt,
        IReadOnlyList<string>? sessionChain = null,
        string? continueMode = null,
        RunTriggerMetadata? triggerMetadata = null)
    {
        var mode = ContinueModes.Normalize(continueMode);
        var trigger = triggerMetadata ?? DefaultTrigger(intent, initialState, followupPrompt);
        if (intent == RunIntent.UserContinue)
        {
            // sessionName may be empty when MarkSessionChainRecovery cleared it
            // before the previous run completed, but the previous run actually
            // captured a UUID we could resume. Falling back to the chain's
            // latest non-recovery entry rescues that case so a follow-up after
            // a recovery does not loop into recovery again.
            var resumeCandidate = !string.IsNullOrWhiteSpace(sessionName)
                ? sessionName
                : LatestRealSessionId(sessionChain);
            var hasSession = !string.IsNullOrWhiteSpace(resumeCandidate);
            var compatible = hasSession && isCompatibleSessionName(resumeCandidate);
            var placeholder = IsPlaceholderSessionSlug(resumeCandidate);
            var canResume = hasSession && compatible && !placeholder;
            string? reason =
                !hasSession ? "no session recorded"
                : !compatible ? $"recorded id is not a valid {cliType} session"
                : placeholder ? "recorded id is a legacy placeholder slug"
                : null;
            var moveToProgress = initialState is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated or TaskStates.Completed or TaskStates.Ready;

            if (canResume)
            {
                return new RunPlan(
                    PromptTemplate: null,
                    PromptVariables: EmptyPromptVariables,
                    PromptOverride: BuildContinuePrompt(mode, followupPrompt),
                    SessionToResume: resumeCandidate,
                    ResumeFlag: true,
                    EventKind: "continue",
                    EventReason: mode == ContinueModes.Continue ? null : $"mode={mode}",
                    EventInputSessionId: resumeCandidate,
                    MoveJobToProgress: moveToProgress,
                    MarkSessionChainRecovery: false,
                    WriteCutMarker: false,
                    CutMarkerReason: null,
                    // Persist the chain-recovered id so SessionName advances
                    // in lockstep and the next planner pass sees it directly.
                    PersistSessionName: string.IsNullOrWhiteSpace(sessionName) ? resumeCandidate : null,
                    ClearStaleSessionName: false) { TriggerMetadata = trigger };
            }

            return new RunPlan(
                PromptTemplate: RuntimePromptService.RunnerRecoveryContinuation,
                PromptVariables: PromptVariables(
                    promptPath: promptPath,
                    jobFolder: jobFolder,
                    userFollowup: followupPrompt ?? string.Empty),
                PromptOverride: null,
                SessionToResume: null,
                ResumeFlag: false,
                EventKind: "recovery",
                EventReason: reason,
                EventInputSessionId: null,
                MoveJobToProgress: moveToProgress,
                MarkSessionChainRecovery: true,
                WriteCutMarker: true,
                CutMarkerReason: reason ?? "session lost",
                PersistSessionName: null,
                ClearStaleSessionName: false) { TriggerMetadata = trigger };
        }

        // ManualStart / AutoPickup share the same plan shape - only the trigger
        // differs, and that is logged at the call site, not branched here.
        // Ready -> Progress is the normal pickup path. Review/Completed ->
        // Progress fires when the user re-starts a finished task (typically
        // after editing prompt.md): the job moves back into the active lane
        // so its CLI run is visible in the runner's status, and the state
        // matches the actual situation (a process is working on it).
        var moveStartToProgress =
            initialState == TaskStates.Ready
            || initialState is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated or TaskStates.Completed;
        var startSession = sessionName;
        var sessionDropped = false;
        var clearStale = false;
        var markRecovery = false;
        if (!string.IsNullOrWhiteSpace(startSession) && !isCompatibleSessionName(startSession))
        {
            var isLegacyPlaceholder = IsPlaceholderSessionSlug(startSession);
            startSession = null;
            if (isLegacyPlaceholder)
            {
                clearStale = true;
            }
            else
            {
                markRecovery = true;
                sessionDropped = true;
            }
        }
        var resume = !string.IsNullOrWhiteSpace(startSession);
        string? persistSessionName = null;
        // Claude / Codex / Gemini capture a real session UUID during streaming and
        // leave SessionName null until then; nothing to pre-generate here.

        var promptTemplate = SelectStartPromptTemplate(initialState, resume, sessionDropped);

        string evtKind;
        string? evtReason;
        if (sessionDropped)
        {
            evtKind = "recovery";
            evtReason = "previous session was for another CLI. Files reconstructed.";
        }
        else if (resume && initialState is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated or TaskStates.Completed)
        {
            // Re-starting an already-finished task with the same session is
            // almost always a "user updated the prompt and wants the agent to
            // act on the delta" event - not a plain continue and not a fresh
            // start. The dedicated event kind makes that intent visible to
            // anyone reading the session log.
            evtKind = "restart";
            evtReason = "previous run completed; user re-started with updated prompt.";
        }
        else
        {
            evtKind = resume ? "continue" : "start";
            evtReason = null;
        }

        return new RunPlan(
            PromptTemplate: promptTemplate,
            PromptVariables: PromptVariables(promptPath, jobFolder, userFollowup: null),
            PromptOverride: null,
            SessionToResume: startSession,
            ResumeFlag: resume,
            EventKind: evtKind,
            EventReason: evtReason,
            EventInputSessionId: resume ? startSession : null,
            MoveJobToProgress: moveStartToProgress,
            MarkSessionChainRecovery: markRecovery,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: persistSessionName,
            ClearStaleSessionName: clearStale) { TriggerMetadata = trigger };
    }

    private static RunTriggerMetadata DefaultTrigger(
        RunIntent intent,
        string initialState,
        string? followupPrompt)
    {
        if (intent == RunIntent.UserContinue)
            return new RunTriggerMetadata(
                RunTriggers.OperatorContinue,
                "operator local-default",
                string.IsNullOrWhiteSpace(followupPrompt)
                    ? "Operator requested a continuation."
                    : $"Operator requested: {RunTriggerMetadata.PromptPreview(followupPrompt)}",
                RunTriggerMetadata.PromptPreview(followupPrompt));
        if (intent == RunIntent.ManualStart
            && initialState is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated or TaskStates.Completed)
            return new RunTriggerMetadata(
                RunTriggers.Restart,
                "operator local-default",
                "Operator restarted a previously completed or reviewed task.");
        return new RunTriggerMetadata(
            RunTriggers.Initial,
            intent == RunIntent.AutoPickup ? "pipeline" : "operator local-default",
            intent == RunIntent.AutoPickup ? "Pipeline picked up the task." : "Operator started the task.");
    }

    /// <summary>
    /// Decides whether a start should inject the resume-continuation prompt
    /// instead of the fresh-start prompt.
    /// <list type="bullet">
    /// <item>Sending it when no real session exists and nothing was dropped just
    /// gets a "I don't see an interrupted task" reply and an exit - so a job that
    /// happens to be in 3-progress without a captured UUID is treated as a fresh
    /// start.</item>
    /// <item><c>sessionDropped</c> means the persisted session id was for another
    /// CLI; the agent that wrote the job folder did real work so reconstruction
    /// from files is worth attempting regardless of the current state.</item>
    /// </list>
    /// </summary>
    public static bool ShouldUseResumePrompt(string initialState, bool resume, bool sessionDropped)
    {
        if (sessionDropped) return true;
        if (initialState == TaskStates.Progress && resume) return true;
        return false;
    }

    /// <summary>
    /// Picks the bootstrap template for a manual/auto start. Three branches:
    /// <list type="bullet">
    /// <item><c>RunnerResumeInterrupted</c> when we're recovering an in-flight
    /// job (Progress + resume, or session was dropped) - the agent reconstructs
    /// from job evidence.</item>
    /// <item><c>RunnerResumeRestart</c> when the user re-starts a task that
    /// already finished (Review/Completed) with the same session - the agent is
    /// told the previous run completed and to act on the delta in
    /// <c>prompt.md</c>. This closes the bug where re-issuing the fresh-start
    /// bootstrap on a finished session made Claude reply "I'll wait for your
    /// request" because the new turn looked like a duplicate of turn 1.</item>
    /// <item><c>RunnerFreshStart</c> for everything else, including any start
    /// without a resumable session.</item>
    /// </list>
    /// </summary>
    public static string SelectStartPromptTemplate(string initialState, bool resume, bool sessionDropped)
    {
        if (ShouldUseResumePrompt(initialState, resume, sessionDropped))
            return RuntimePromptService.RunnerResumeInterrupted;
        if (resume && initialState is TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Escalated or TaskStates.Completed)
            return RuntimePromptService.RunnerResumeRestart;
        return RuntimePromptService.RunnerFreshStart;
    }

    /// <summary>
    /// True for slugs we generated via <see cref="BuildSessionName"/> on an earlier
    /// run. These were never real sessions on the agent side - recognising them
    /// lets the cross-CLI guard drop them silently instead of treating the
    /// next start as a recovery from an interrupted run.
    /// </summary>
    /// <summary>
    /// Wraps the user's follow-up with mode-specific framing. Continue is the
    /// default and passes the follow-up through verbatim. Steer, Extend, and
    /// NewTask wrap the follow-up so the agent treats it as a course
    /// correction, an addition to the original task, or a new sub-task in the
    /// same session, respectively.
    /// </summary>
    public static string BuildContinuePrompt(string mode, string? followup)
    {
        var body = (followup ?? string.Empty).TrimEnd();
        return mode switch
        {
            ContinueModes.Steer =>
                "User correction (override the current plan, then continue):\n\n" + body,
            ContinueModes.Extend =>
                "The user has extended the task. A new prompt-N.md file has been written to the job folder; the new instruction below is added to the existing work, not a replacement. Read prompt.md plus any prompt-N.md siblings in the job folder for the full timeline before acting.\n\nNew extension:\n" + body,
            ContinueModes.NewTask =>
                "New sub-task in the same session (keep prior context, but treat this as a new request):\n\n" + body,
            _ => body
        };
    }

    public static bool IsPlaceholderSessionSlug(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName)
           && PlaceholderSessionSlugRegex.IsMatch(sessionName!);

    /// <summary>
    /// Returns the latest resumable entry in <paramref name="chain"/>, or
    /// null when none exists. Used by <see cref="PlanRun"/> as the fallback
    /// resume candidate when <c>sessionName</c> is empty but the chain
    /// still records a real captured id from an earlier run.
    ///
    /// <para>
    /// A <c>"(recovery)"</c> sentinel is the chain's tombstone for "the
    /// previous attempt to use the prior id failed; do not retry it". When
    /// the most recent non-empty entry is that sentinel, every UUID before
    /// it is older than the failure and equally untrustworthy, so we return
    /// null (force a Recovery) rather than pick the rejected id back up.
    /// We only return a UUID that was captured AFTER the most recent
    /// recovery marker. Without this guard, a capture-fail clears
    /// SessionName and appends "(recovery)" but the dead UUID remains the
    /// latest non-sentinel entry in the chain - the next Continue then
    /// plans Resume against it, claude rejects it identically, and the
    /// loop never breaks.
    /// </para>
    /// </summary>
    public static string? LatestRealSessionId(IReadOnlyList<string>? chain)
    {
        if (chain == null) return null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var entry = chain[i];
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (string.Equals(entry, "(recovery)", StringComparison.Ordinal)) return null;
            if (IsPlaceholderSessionSlug(entry)) continue;
            return entry;
        }
        return null;
    }

    private static readonly Regex PlaceholderSessionSlugRegex =
        new(@"^taskboard-[A-Za-z0-9_-]+-\d{12}$", RegexOptions.Compiled);

    /// <summary>
    /// Builds the short, deterministic placeholder session slug some legacy runs
    /// persisted before the surviving CLIs (Claude / Codex / Gemini) captured a
    /// real streaming UUID. Retained so <see cref="IsPlaceholderSessionSlug"/> can
    /// still recognise and drop those slugs on resume.
    /// </summary>
    public static string BuildSessionName(string jobId)
    {
        var slug = new string(jobId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (slug.Length > 40) slug = slug[..40];
        return $"taskboard-{slug}-{DateTime.UtcNow:yyyyMMddHHmm}";
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyPromptVariables =
        new Dictionary<string, string?>();

    private static IReadOnlyDictionary<string, string?> PromptVariables(
        string promptPath,
        string jobFolder,
        string? userFollowup) =>
        new Dictionary<string, string?>
        {
            ["prompt_path"] = promptPath,
            ["job_folder"] = jobFolder,
            ["user_followup"] = userFollowup
        };
}
