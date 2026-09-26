

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Decision-tree matrix for <see cref="RunPlanner.PlanRun"/> - the single
/// place that maps (intent × job state × session state × CLI compatibility) to
/// a <see cref="RunPlan"/>. The whole point of having one planner is that this
/// matrix locks every cell of the table; if a future change introduces a path
/// that "Continue" handles but "Start" doesn't (or vice versa), the gap shows
/// up here as a missing or surprising row instead of as a 4xx that ships to
/// the user.
///
/// The bug class this guards against: pre-refactor, "Start" and "Continue"
/// owned independent decision trees and the recovery fix landed only on one
/// side, so user follow-ups on a job without a captured session got a 400
/// "This job has no session yet - start it once before continuing" while the
/// Play button worked. Same inputs, different outputs depending on which
/// endpoint you reached. The planner now serialises both intents through one
/// function - these tests prove they cannot diverge silently.
/// </summary>
public class TaskRunnerPlanTests
{
    // Claude's compat predicate: only true 8-4-4-4-12 UUIDs are accepted.
    // Anything else (placeholder slug, foreign-CLI handle, garbage) is rejected.
    private static readonly System.Text.RegularExpressions.Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    private static bool ClaudeCompat(string? n) =>
        !string.IsNullOrWhiteSpace(n) && UuidRegex.IsMatch(n!);
    // Permissive base predicate (Gemini / Codex default): non-empty.
    private static bool PermissiveCompat(string? n) => !string.IsNullOrWhiteSpace(n);

    private const string ValidUuid       = "a1b2c3d4-e5f6-4789-abcd-ef0123456789";
    private const string PlaceholderSlug = "taskboard-fix-bug-202604282114";
    private const string ForeignSlug     = "rollover_2025-04-01T12:00:00Z";

    private static RunPlan Plan(
        RunIntent intent,
        string state,
        string? sessionName,
        string cliType = CliTypes.Claude,
        System.Func<string?, bool>? compat = null,
        string? followup = null,
        IReadOnlyList<string>? sessionChain = null)
    {
        return RunPlanner.PlanRun(
            intent,
            state,
            sessionName,
            cliType,
            compat ?? ClaudeCompat,
            jobId: "fix-bug",
            promptPath: @"C:\jobs\fix-bug\prompt.md",
            jobFolder: @"C:\jobs\fix-bug",
            followupPrompt: followup,
            sessionChain: sessionChain);
    }

    /// <summary>
    /// Bug class: after a Recovery run the sessionName is cleared by
    /// MarkSessionChainRecovery before the run starts; if the
    /// post-run capture race lost the new UUID, sessionName stays empty
    /// and the next follow-up loops back into Recovery, which clears the
    /// chain again. The fallback fixes that by reading the latest
    /// non-recovery, non-placeholder entry from sessionChain when
    /// sessionName itself is empty.
    /// </summary>
    [Fact]
    public void Continue_EmptySessionNameButChainHasUuid_ResumesFromChain()
    {
        const string priorUuid = "11111111-2222-4333-8444-555555555555";
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: null,
                     followup: "tighten the spacing",
                     sessionChain: new[] { priorUuid });

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(priorUuid, p.SessionToResume);
        Assert.Equal(priorUuid, p.PersistSessionName);
        Assert.False(p.MarkSessionChainRecovery);
    }

    /// <summary>
    /// Recovery sentinels and placeholder slugs in the chain must not be
    /// mistaken for resumable session ids.
    /// </summary>
    [Fact]
    public void Continue_ChainTailIsRecoverySentinel_StillRoutesToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: null,
                     followup: "do it",
                     sessionChain: new[] { "(recovery)" });

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
    }

    /// <summary>
    /// Bug class (capture-fail loop): when claude rejects a --resume target,
    /// ProjectRunner clears sessionName and appends "(recovery)" to the
    /// chain, leaving the rejected UUID as the chain's last UUID entry. The
    /// next Continue must NOT resurrect that dead UUID via the chain
    /// fallback - the recovery sentinel is a tombstone meaning "every id
    /// before this one is older than the failure". Without this guard, the
    /// planner re-issues --resume against the same dead UUID and claude
    /// returns "No conversation found with session ID:" identically forever.
    /// </summary>
    [Fact]
    public void Continue_ChainHasUuidThenRecoveryTombstone_RoutesToRecovery()
    {
        const string deadUuid = "3e80651e-57fa-438a-94d0-7078a7112167";
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: null,
                     followup: "Continue from where the previous run left off.",
                     sessionChain: new[] { deadUuid, "(recovery)" });

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
    }

    /// <summary>
    /// A new UUID captured AFTER a recovery marker is the chain's authoritative
    /// resume target; the marker only invalidates entries that came before it.
    /// </summary>
    [Fact]
    public void Continue_ChainHasUuidAfterRecoveryMarker_ResumesViaThatUuid()
    {
        const string oldUuid = "11111111-2222-4333-8444-555555555555";
        const string newUuid = "99999999-8888-4777-a666-555555555555";
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: null,
                     followup: "keep going",
                     sessionChain: new[] { oldUuid, "(recovery)", newUuid });

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(newUuid, p.SessionToResume);
    }

    // ===== Continue modes =====

    /// <summary>
    /// Steer mode wraps the follow-up so the agent treats it as a course
    /// correction, not a generic next message.
    /// </summary>
    [Fact]
    public void Continue_SteerMode_WrapsAsCorrection()
    {
        var prompt = RunPlanner.BuildContinuePrompt(ContinueModes.Steer, "use Tailwind tokens, not CSS vars");
        Assert.StartsWith("User correction", prompt, StringComparison.Ordinal);
        Assert.Contains("use Tailwind tokens", prompt);
    }

    [Fact]
    public void Continue_ExtendMode_TellsAgentAboutPromptHistory()
    {
        var prompt = RunPlanner.BuildContinuePrompt(ContinueModes.Extend, "also add a fullscreen image overlay");
        Assert.Contains("prompt-N.md", prompt);
        Assert.Contains("New extension", prompt);
        Assert.Contains("also add a fullscreen image overlay", prompt);
    }

    [Fact]
    public void Continue_NewTaskMode_FramesAsSubTask()
    {
        var prompt = RunPlanner.BuildContinuePrompt(ContinueModes.NewTask, "now switch focus to the activity log");
        Assert.Contains("New sub-task", prompt);
        Assert.Contains("switch focus to the activity log", prompt);
    }

    [Fact]
    public void Continue_DefaultMode_PassesFollowupVerbatim()
    {
        var prompt = RunPlanner.BuildContinuePrompt(ContinueModes.Continue, "Looks good, ship it.");
        Assert.Equal("Looks good, ship it.", prompt);
    }

    /// <summary>
    /// Mode flows through to the resume plan and is reflected in the event
    /// reason so the session-events log shows what kind of continuation
    /// happened.
    /// </summary>
    [Fact]
    public void Continue_SteerModeOnLiveSession_IsReflectedInEventReason()
    {
        var p = RunPlanner.PlanRun(
            RunIntent.UserContinue,
            TaskStates.Progress,
            sessionName: ValidUuid,
            cliType: CliTypes.Claude,
            isCompatibleSessionName: ClaudeCompat,
            jobId: "fix-bug",
            promptPath: @"C:\jobs\fix-bug\prompt.md",
            jobFolder: @"C:\jobs\fix-bug",
            followupPrompt: "use Tailwind tokens",
            sessionChain: null,
            continueMode: ContinueModes.Steer);

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Contains("steer", p.EventReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("User correction", p.PromptOverride ?? string.Empty, StringComparison.Ordinal);
    }

    // ===== Continue (the original symptom path: "no session yet") =====

    /// <summary>
    /// THE regression: user types in the chat for a job that never captured a
    /// session UUID. Pre-refactor: 400. Post-refactor: recovery plan that
    /// starts a fresh CLI run, instructs the agent to reconstruct context from
    /// disk, marks the chain break, writes a cut marker, appends the user
    /// follow-up to the recovery prompt.
    /// </summary>
    [Fact]
    public void Continue_NoSession_FallsBackToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: null,
                     followup: "please continue with the chat box");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.True(p.MarkSessionChainRecovery);
        Assert.True(p.WriteCutMarker);
        Assert.Equal("no session recorded", p.EventReason);
        Assert.Equal(RuntimePromptService.RunnerRecoveryContinuation, p.PromptTemplate);
        Assert.Null(p.PromptOverride);
        Assert.Equal("please continue with the chat box", Var(p, "user_followup"));
    }

    /// <summary>
    /// Continue against a real captured UUID - the happy path. Resume flag on,
    /// session passed through, no recovery side-effects, prompt is the raw
    /// follow-up so the agent receives it as the next conversation turn.
    /// </summary>
    [Fact]
    public void Continue_WithValidSession_Resumes()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ValidUuid,
                     followup: "tighten the spacing");

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.Equal(ValidUuid, p.EventInputSessionId);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.False(p.WriteCutMarker);
        Assert.Null(p.PromptTemplate);
        Assert.Equal("tighten the spacing", p.PromptOverride);
        Assert.Null(p.EventReason);
    }

    /// <summary>
    /// Legacy placeholder slug from before real session capture: the planner
    /// must treat it as "no session" and route through recovery. If we
    /// accidentally accepted it as a resume handle, Claude's `-r` would hang
    /// or reply "I don't see an interrupted task" - the exact regression the
    /// placeholder check exists to prevent.
    ///
    /// On a strict-compat CLI like Claude, the placeholder slug fails the UUID
    /// shape check before the placeholder check fires - so the recovery reason
    /// reads "not a valid claude session" rather than the more specific
    /// "legacy placeholder slug" text. Either way the run lands in recovery,
    /// which is the user-visible property that matters.
    /// </summary>
    [Fact]
    public void Continue_WithPlaceholderSlug_OnClaude_RoutesToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: PlaceholderSlug,
                     followup: "go on");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.True(p.MarkSessionChainRecovery);
        // Strict-compat CLI rejects the slug at the UUID gate - so we report
        // the compat-fail reason rather than the placeholder-fail reason.
        Assert.Contains("not a valid claude session", p.EventReason ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On a permissive-compat CLI (e.g. Gemini), the placeholder slug passes the
    /// "is non-empty" compat check, so the planner's dedicated placeholder
    /// branch fires and the reason text identifies it as a legacy slug. This
    /// is the path that the placeholder regex was actually written for.
    /// </summary>
    [Fact]
    public void Continue_WithPlaceholderSlug_OnPermissiveCli_RoutesToRecoveryWithPlaceholderReason()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: PlaceholderSlug,
                     cliType: CliTypes.Gemini, compat: PermissiveCompat, followup: "go on");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.True(p.MarkSessionChainRecovery);
        Assert.Equal("recorded id is a legacy placeholder slug", p.EventReason);
    }

    /// <summary>
    /// Foreign-CLI handle (e.g. a Gemini slug under a Claude job): not UUID-
    /// shaped, so the Claude compat predicate rejects it. Planner routes to
    /// recovery with the cli-specific reason text so the session-events log
    /// explains why.
    /// </summary>
    [Fact]
    public void Continue_WithForeignCliHandle_RoutesToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ForeignSlug,
                     followup: "go on");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Contains("not a valid claude session", p.EventReason ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Continue from 4-review/5-completed (user re-opens a finished job with a
    /// follow-up): the plan must signal MoveJobToProgress so the runner state
    /// machine stays consistent - without that, the job would stay in review
    /// while a CLI is actively writing to it. This is the move-policy
    /// asymmetry vs Start (which only moves from Ready → Progress); both must
    /// be preserved.
    /// </summary>
    [Theory]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.Completed)]
    public void Continue_MovesJobBackToProgress(string state)
    {
        var p = Plan(RunIntent.UserContinue, state, sessionName: ValidUuid, followup: "go");
        Assert.True(p.MoveJobToProgress);
    }

    [Fact]
    public void Continue_AlreadyInProgress_DoesNotMove()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ValidUuid, followup: "go");
        Assert.False(p.MoveJobToProgress);
    }

    // ===== Manual / Auto Start =====

    /// <summary>
    /// Brand-new job from 2-ready, no session: plain fresh start. The fresh-
    /// start prompt points at prompt.md and the job folder so the agent reads
    /// the task and runs it.
    /// </summary>
    [Fact]
    public void Start_FromReadyNoSession_FreshStart()
    {
        var p = Plan(RunIntent.ManualStart, TaskStates.Ready, sessionName: null);

        Assert.Equal("start", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.True(p.MoveJobToProgress);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.False(p.WriteCutMarker);
        Assert.Equal(RuntimePromptService.RunnerFreshStart, p.PromptTemplate);
        Assert.Equal(@"C:\jobs\fix-bug\prompt.md", Var(p, "prompt_path"));
        Assert.Null(p.PersistSessionName);
    }

    /// <summary>
    /// Auto-pickup is identical to manual start - only the trigger reason
    /// differs, the plan must not. Locks in the symmetry; if a future change
    /// adds an Auto-only branch, this test catches it.
    /// </summary>
    [Fact]
    public void AutoPickup_AndManualStart_ProducePlansThatDifferOnlyByTrigger()
    {
        var manual = Plan(RunIntent.ManualStart, TaskStates.Ready, sessionName: null);
        var auto = Plan(RunIntent.AutoPickup, TaskStates.Ready, sessionName: null);

        Assert.Equal(manual.PromptTemplate, auto.PromptTemplate);
        Assert.Equal(manual.PromptOverride, auto.PromptOverride);
        Assert.Equal(manual.SessionToResume, auto.SessionToResume);
        Assert.Equal(manual.ResumeFlag, auto.ResumeFlag);
        Assert.Equal(manual.EventKind, auto.EventKind);
        Assert.Equal(manual.EventReason, auto.EventReason);
        Assert.Equal(manual.MoveJobToProgress, auto.MoveJobToProgress);
        Assert.Equal(RunTriggers.Initial, auto.TriggerMetadata.Trigger);
        Assert.Equal(RunTriggers.Initial, manual.TriggerMetadata.Trigger);
    }

    [Fact]
    public void Trigger_metadata_is_independent_of_session_resume_choice()
    {
        var trigger = new RunTriggerMetadata(
            RunTriggers.IntegrationRecovery,
            "pipeline",
            "Integration recovery was queued after a merge conflict.",
            "failure=merge-conflict;file=README.md");
        var plan = RunPlanner.PlanRun(
            RunIntent.UserContinue,
            TaskStates.Progress,
            ValidUuid,
            CliTypes.Claude,
            ClaudeCompat,
            "AGT-1",
            @"C:\jobs\fix-bug\prompt.md",
            @"C:\jobs\fix-bug",
            "Resolve the conflict.",
            triggerMetadata: trigger);

        Assert.Equal("continue", plan.EventKind);
        Assert.True(plan.ResumeFlag);
        Assert.Equal(trigger, plan.TriggerMetadata);
    }

    /// <summary>
    /// Job stuck in 3-progress with a captured UUID - previous run was
    /// interrupted. The planner detects "interrupted resume" via the
    /// ShouldUseResumePrompt rule and switches to the resume continuation
    /// prompt that re-anchors the agent to the job folder.
    /// </summary>
    [Fact]
    public void Start_FromProgressWithSession_UsesResumePrompt()
    {
        var p = Plan(RunIntent.ManualStart, TaskStates.Progress, sessionName: ValidUuid);

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.Equal(RuntimePromptService.RunnerResumeInterrupted, p.PromptTemplate);
        Assert.False(p.MoveJobToProgress); // already there
    }

    /// <summary>
    /// Job in 3-progress but no UUID was ever captured (e.g. CLI crashed
    /// before the first stream-json frame). Sending the resume prompt here
    /// would just make the agent reply "I don't see an interrupted task" - so
    /// the plan must fall back to a fresh start with the regular prompt.
    /// </summary>
    [Fact]
    public void Start_FromProgressNoSession_FallsBackToFreshPrompt()
    {
        var p = Plan(RunIntent.ManualStart, TaskStates.Progress, sessionName: null);

        Assert.Equal("start", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Equal(RuntimePromptService.RunnerFreshStart, p.PromptTemplate);
    }

    /// <summary>
    /// Foreign-CLI session on a Claude start: drop the session, mark the chain
    /// break, send the resume prompt so the agent reconstructs from files the
    /// previous CLI wrote. Event kind is "recovery" because the chain visibly
    /// broke; without that the chip would never show the cut.
    /// </summary>
    [Fact]
    public void Start_DropsForeignSession_AndRecovers()
    {
        var p = Plan(RunIntent.ManualStart, TaskStates.Progress, sessionName: ForeignSlug);

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.True(p.MarkSessionChainRecovery);
        Assert.False(p.ClearStaleSessionName); // recovery, not a quiet drop
        Assert.Equal(RuntimePromptService.RunnerResumeInterrupted, p.PromptTemplate);
        Assert.Equal("previous session was for another CLI. Files reconstructed.", p.EventReason);
    }

    /// <summary>
    /// Legacy placeholder on a Claude start: dropped quietly (ClearStale, no
    /// chain-break mark, no recovery event) - placeholders never represented
    /// real sessions, so there is nothing to recover from and the chip should
    /// not show a cut.
    /// </summary>
    [Fact]
    public void Start_DropsLegacyPlaceholder_Quietly()
    {
        var p = Plan(RunIntent.ManualStart, TaskStates.Progress, sessionName: PlaceholderSlug);

        Assert.Equal("start", p.EventKind);
        Assert.True(p.ClearStaleSessionName);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.Equal(RuntimePromptService.RunnerFreshStart, p.PromptTemplate);
    }

    /// <summary>
    /// Re-starting a finished task (4-review or 5-completed) with a captured
    /// session: this is the "user updated prompt.md and clicked Start again"
    /// path. Pre-fix it routed through fresh-start, which made Claude reply
    /// "I'll wait for your request" because the bootstrap turn was a duplicate
    /// of the original turn 1. The dedicated restart template tells Claude the
    /// previous run completed and to act on the delta.
    /// </summary>
    [Theory]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.Completed)]
    public void Start_FromReviewOrCompletedWithSession_UsesRestartPrompt(string state)
    {
        var p = Plan(RunIntent.ManualStart, state, sessionName: ValidUuid);

        Assert.Equal("restart", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.Equal(RuntimePromptService.RunnerResumeRestart, p.PromptTemplate);
        Assert.True(p.MoveJobToProgress, "restart must move job back into the active lane");
        Assert.False(p.MarkSessionChainRecovery);
        Assert.False(p.WriteCutMarker);
        Assert.Contains("re-started", p.EventReason ?? "", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-start of a finished task without a captured session: there is no
    /// session to resume, so the planner must fall back to a plain fresh start
    /// (and still move the job out of review/completed, because a CLI run is
    /// about to write to it).
    /// </summary>
    [Theory]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.Completed)]
    public void Start_FromReviewOrCompletedNoSession_FallsBackToFreshStart(string state)
    {
        var p = Plan(RunIntent.ManualStart, state, sessionName: null);

        Assert.Equal("start", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Equal(RuntimePromptService.RunnerFreshStart, p.PromptTemplate);
        Assert.True(p.MoveJobToProgress);
    }

    [Fact]
    public void Start_Claude_DoesNotPreGenerateSessionSlug()
    {
        var p = Plan(RunIntent.ManualStart, TaskStates.Ready, sessionName: null);
        Assert.Null(p.PersistSessionName);
        Assert.Null(p.SessionToResume);
    }

    // ===== Cross-intent invariants =====

    /// <summary>
    /// Whatever the intent, a valid captured session must always result in
    /// resume=true and the session being passed through. This is the property
    /// the user expects ("if my session is good, use it"); a planner change
    /// that breaks it on either path would be a regression.
    /// </summary>
    [Theory]
    [InlineData(RunIntent.ManualStart)]
    [InlineData(RunIntent.AutoPickup)]
    [InlineData(RunIntent.UserContinue)]
    public void AnyIntent_WithValidSession_ResumesIt(RunIntent intent)
    {
        var p = Plan(intent, TaskStates.Progress, sessionName: ValidUuid, followup: "x");
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
    }

    /// <summary>
    /// THE invariant. No matter the intent, no matter the session state, the
    /// planner must always produce a runnable plan - never throw, never
    /// produce a "this is impossible" sentinel. The "no session yet - start it
    /// once" 400 was the violation of this property; this test enumerates the
    /// matrix that has to stay green.
    /// </summary>
    [Theory]
    [InlineData(RunIntent.ManualStart,  TaskStates.Ready,    null)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Ready,    ValidUuid)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Ready,    PlaceholderSlug)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Ready,    ForeignSlug)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Progress, null)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Progress, ValidUuid)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Progress, PlaceholderSlug)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Progress, ForeignSlug)]
    [InlineData(RunIntent.AutoPickup,   TaskStates.Ready,    null)]
    [InlineData(RunIntent.AutoPickup,   TaskStates.Ready,    ValidUuid)]
    [InlineData(RunIntent.UserContinue, TaskStates.Progress, null)]
    [InlineData(RunIntent.UserContinue, TaskStates.Progress, ValidUuid)]
    [InlineData(RunIntent.UserContinue, TaskStates.Progress, PlaceholderSlug)]
    [InlineData(RunIntent.UserContinue, TaskStates.Progress, ForeignSlug)]
    [InlineData(RunIntent.UserContinue, TaskStates.AutoReview,   ValidUuid)]
    [InlineData(RunIntent.UserContinue, TaskStates.AutoReview,   null)]
    [InlineData(RunIntent.UserContinue, TaskStates.Completed,ValidUuid)]
    [InlineData(RunIntent.UserContinue, TaskStates.Completed,null)]
    [InlineData(RunIntent.ManualStart,  TaskStates.AutoReview,   ValidUuid)]
    [InlineData(RunIntent.ManualStart,  TaskStates.AutoReview,   null)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Completed,ValidUuid)]
    [InlineData(RunIntent.ManualStart,  TaskStates.Completed,null)]
    public void Plan_AlwaysProducesRunnableOutput(RunIntent intent, string state, string? sessionName)
    {
        var p = Plan(intent, state, sessionName, followup: "go");

        Assert.NotNull(p);
        Assert.True(!string.IsNullOrEmpty(p.PromptOverride) || !string.IsNullOrEmpty(p.PromptTemplate),
            "Plan must always carry a prompt override or template");
        Assert.Contains(p.EventKind, new[] { "start", "continue", "recovery", "restart" });
        // resume flag and session-to-resume must agree
        if (p.ResumeFlag) Assert.NotNull(p.SessionToResume);
        // a continuation event must reference the session it claims to resume
        if (p.EventKind == "continue") Assert.NotNull(p.EventInputSessionId);
        // a recovery event must explain itself
        if (p.EventKind == "recovery") Assert.False(string.IsNullOrEmpty(p.EventReason));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("COMPLETED", true)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    [InlineData("running", false)]
    [InlineData(null, false)]
    public void CompletionPolicy_OnlyCompletedRunsMoveToReview(string? status, bool expected)
    {
        Assert.Equal(expected, RunCompletionPolicy.ShouldMoveToReview(status));
    }

    // =================================================================
    // "Fluffy chat" continuation matrix
    //
    // What we want: the user sends a follow-up, the agent replies, the
    // user sends another follow-up — repeat without surprises. Each
    // turn must produce a plan that resumes the prior session, sends
    // the user's followup as the next conversation turn, and never
    // fires recovery / chain-break side effects when the chain is
    // intact. These tests lock the per-turn plan shape so a refactor
    // of the planner can't silently change the chat semantics.
    // =================================================================

    /// <summary>
    /// First follow-up after a fresh start: the planner must resume the
    /// captured UUID, pass the followup as the prompt override, and not move
    /// the job (it's already in 3-progress).
    /// </summary>
    [Fact]
    public void Continue_FirstFollowupAfterFreshStart_ResumesSessionWithoutSideEffects()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ValidUuid,
                     followup: "Now also add tests.");

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.Equal("Now also add tests.", p.PromptOverride);
        Assert.Null(p.PromptTemplate);            // no bootstrap on a continuation
        Assert.False(p.MoveJobToProgress);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.False(p.WriteCutMarker);
        Assert.Null(p.PersistSessionName);
    }

    /// <summary>
    /// Long back-and-forth: two consecutive follow-ups must produce identical
    /// plan shapes. This is the property the user actually feels as "fluffy
    /// chat" — every turn behaves the same, no random recovery or template
    /// swap mid-conversation.
    /// </summary>
    [Fact]
    public void Continue_TwoConsecutiveFollowupsProduceSamePlanShape()
    {
        var first  = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ValidUuid, followup: "first");
        var second = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ValidUuid, followup: "second");

        Assert.Equal(first.EventKind, second.EventKind);
        Assert.Equal(first.ResumeFlag, second.ResumeFlag);
        Assert.Equal(first.SessionToResume, second.SessionToResume);
        Assert.Equal(first.PromptTemplate, second.PromptTemplate);   // both null
        Assert.Equal(first.MoveJobToProgress, second.MoveJobToProgress);
        Assert.Equal(first.MarkSessionChainRecovery, second.MarkSessionChainRecovery);
        // Only the prompt content differs.
        Assert.NotEqual(first.PromptOverride, second.PromptOverride);
    }

    /// <summary>
    /// Continue from Review (user reopened a finished job and typed in chat):
    /// move job back to 3-progress, resume, attach the followup. Distinct
    /// from ManualStart-from-Review which uses the restart bootstrap; this
    /// path skips the bootstrap because the user is providing fresh
    /// instructions verbatim.
    /// </summary>
    [Fact]
    public void Continue_FromReview_MovesToProgressAndResumesWithFollowupVerbatim()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.AutoReview, sessionName: ValidUuid,
                     followup: "One more tweak: tighten the spacing.");

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.True(p.MoveJobToProgress, "review → progress when continuing");
        Assert.Equal("One more tweak: tighten the spacing.", p.PromptOverride);
        Assert.Null(p.PromptTemplate);
    }

    /// <summary>
    /// Empty follow-up is still a valid plan (recovery path passes empty
    /// string when the runner has no user text). Locks: empty prompt does
    /// not crash and does not flip the kind.
    /// </summary>
    [Fact]
    public void Continue_EmptyFollowup_StillProducesRunnablePlan()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: ValidUuid, followup: "");

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(string.Empty, p.PromptOverride);
        Assert.NotNull(p.SessionToResume);
    }

    /// <summary>
    /// Recovery boundary: if the persisted session is dropped and we re-start
    /// from Progress, the plan must NOT use the chat-continuation path —
    /// recovery hands control to the runner-recovery-continuation prompt
    /// which tells the agent to reconstruct from job folder. This test pins
    /// the "no silent recovery" property: only an explicit Continue with a
    /// followup hits recovery, never a Continue with a fresh session.
    /// </summary>
    [Fact]
    public void Continue_AfterChainRecovery_StartsFreshButKeepsUserFollowup()
    {
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: null,
                     followup: "Pick up where you left off please.");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.Equal(RuntimePromptService.RunnerRecoveryContinuation, p.PromptTemplate);
        Assert.True(p.MarkSessionChainRecovery);
        Assert.True(p.WriteCutMarker);
        // The followup is woven into the recovery-prompt template via
        // PromptVariables, not as a literal override.
        Assert.Null(p.PromptOverride);
        Assert.Equal("Pick up where you left off please.", Var(p, "user_followup"));
    }

    [Fact]
    public void MissingCodexRollout_FallsBackBeforeRender_WithFullRecoveryContext()
    {
        var resume = Plan(
            RunIntent.UserContinue,
            TaskStates.Progress,
            sessionName: ValidUuid,
            cliType: CliTypes.Codex,
            compat: ClaudeCompat,
            followup: "Inspect the successful run and continue.");

        var fallback = RunPlanner.FallBackToRecovery(
            resume,
            promptPath: @"C:\jobs\fix-bug\prompt.md",
            jobFolder: @"C:\jobs\fix-bug",
            followupPrompt: "Inspect the successful run and continue.",
            reason: "Codex rollout is absent from the current CODEX_HOME");

        Assert.False(fallback.ResumeFlag);
        Assert.Null(fallback.SessionToResume);
        Assert.Equal("recovery", fallback.EventKind);
        Assert.Equal(RuntimePromptService.RunnerRecoveryContinuation, fallback.PromptTemplate);
        Assert.Equal(@"C:\jobs\fix-bug\prompt.md", Var(fallback, "prompt_path"));
        Assert.Equal(@"C:\jobs\fix-bug", Var(fallback, "job_folder"));
        Assert.Equal("Inspect the successful run and continue.", Var(fallback, "user_followup"));
        Assert.True(fallback.MarkSessionChainRecovery);
        Assert.True(fallback.WriteCutMarker);
    }

    /// <summary>
    /// A permissive-compat session whose persisted slug DOESN'T match the
    /// <c>taskboard-...-NNNNNNNNNNNN</c> placeholder shape (e.g. a slug from
    /// a previous version of the app or one entered manually). Continue
    /// must resume via the slug — the auto-generated slug from
    /// <see cref="RunPlanner.BuildSessionName"/> is treated as a legacy
    /// placeholder and routes to recovery instead (see
    /// <see cref="Continue_WithPlaceholderSlug_OnPermissiveCli_RoutesToRecoveryWithPlaceholderReason"/>).
    /// That asymmetry keeps old jobs from resuming with a slug that was
    /// never a real session. A real session ID stored under a
    /// non-placeholder shape follows this resume path.
    /// </summary>
    [Fact]
    public void Continue_PermissiveCliNonPlaceholderSlugSession_ResumesViaSlug()
    {
        const string slug = "user-named-session-2026";  // doesn't match placeholder regex
        var p = Plan(RunIntent.UserContinue, TaskStates.Progress, sessionName: slug,
                     cliType: CliTypes.Gemini, compat: PermissiveCompat,
                     followup: "Try running the tests.");

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(slug, p.SessionToResume);
        Assert.False(p.MarkSessionChainRecovery);
    }

    [Fact]
    public void Default_trigger_producer_covers_initial_operator_continue_and_restart()
    {
        Assert.Equal(
            RunTriggers.Initial,
            RunPlanner.DefaultTrigger(RunIntent.AutoPickup, TaskStates.Ready, null).Trigger);
        Assert.Equal(
            RunTriggers.OperatorContinue,
            RunPlanner.DefaultTrigger(RunIntent.UserContinue, TaskStates.Progress, "finish the fix").Trigger);
        Assert.Equal(
            RunTriggers.Restart,
            RunPlanner.DefaultTrigger(RunIntent.ManualStart, TaskStates.HumanReview, null).Trigger);
    }

    [Theory]
    [InlineData("integration-conflict", RunTriggers.IntegrationRecovery, "pipeline")]
    [InlineData("timeout-salvage", RunTriggers.TimeoutContinuation, "watchdog")]
    [InlineData("loop-continuation", RunTriggers.Replan, "pipeline")]
    [InlineData("provider-crash", RunTriggers.RecoveryAfterCrash, "pipeline")]
    [InlineData("project-busy", RunTriggers.OperatorContinue, "operator desktop-client")]
    public void Pending_intent_producer_records_business_trigger_actor_and_source(
        string savedReason,
        string expectedTrigger,
        string expectedActor)
    {
        var trigger = ProjectRunner.TriggerForPendingIntent(
            new PendingIntent { SavedReason = savedReason, Prompt = "continue exactly here" },
            "desktop-client");

        Assert.Equal(expectedTrigger, trigger.Trigger);
        Assert.Equal(expectedActor, trigger.TriggeredBy);
        Assert.Contains(savedReason, trigger.TriggerSource, StringComparison.Ordinal);
        Assert.Contains("continue exactly here", trigger.TriggerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Queued_operator_intent_uses_saved_caller_and_reason_on_local_pickup()
    {
        var intent = new PendingIntent
        {
            SavedReason = FollowUpQueueReasons.ProjectBusy,
            Prompt = "Fix the test",
            TriggeredBy = "operator desktop-client",
            TriggerReason = "Address the review comment.",
        };

        var trigger = ProjectRunner.TriggerForPendingIntent(intent, "task-owner");

        Assert.Equal(RunTriggers.OperatorContinue, trigger.Trigger);
        Assert.Equal("operator desktop-client", trigger.TriggeredBy);
        Assert.Equal("Address the review comment.", trigger.TriggerReason);
        Assert.Contains("Fix the test", trigger.TriggerSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunIssueKind.WatchdogTimeout, RunTriggers.TimeoutContinuation, "watchdog")]
    [InlineData(RunIssueKind.InfraCrash, RunTriggers.RecoveryAfterCrash, "pipeline")]
    [InlineData(RunIssueKind.EnvironmentBlocker, RunTriggers.GateFailure, "pipeline")]
    public void Automatic_retry_producer_records_timeout_crash_and_gate_failure(
        RunIssueKind issueKind,
        string expectedTrigger,
        string expectedActor)
    {
        var trigger = ProjectRunner.TriggerForAutomaticRetry(
            issueKind,
            "Bounded retry selected.",
            "retry prompt");

        Assert.Equal(expectedTrigger, trigger.Trigger);
        Assert.Equal(expectedActor, trigger.TriggeredBy);
        Assert.Contains("failure=", trigger.TriggerSource, StringComparison.Ordinal);
        Assert.Contains("retry prompt", trigger.TriggerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_claim_producer_covers_every_claim_trigger_without_session_inference()
    {
        var concern = new ReviewConcernRoundLedger(
            1, 1, "review_01", ["code-quality"], DateTime.UtcNow,
            RoundKind: RunTriggers.ReviewConcern);
        var finding = concern with { RoundKind = RunTriggers.ReviewFinding };
        var cases = new[]
        {
            LeaseEndpoints.BuildRemoteClaimTrigger(null, null, 0, null, "runner-1", "run-1"),
            LeaseEndpoints.BuildRemoteClaimTrigger(null, null, 2, null, "runner-1", "run-2"),
            LeaseEndpoints.BuildRemoteClaimTrigger("desktop", new PendingIntent { SavedReason = "project-busy", Prompt = "go" }, 1, null, "runner-1", "run-3"),
            LeaseEndpoints.BuildRemoteClaimTrigger(null, new PendingIntent { SavedReason = "integration-conflict", Prompt = "fix" }, 1, null, "runner-1", "run-4"),
            LeaseEndpoints.BuildRemoteClaimTrigger(null, new PendingIntent { SavedReason = "timeout-salvage", Prompt = "resume" }, 1, null, "runner-1", "run-5"),
            LeaseEndpoints.BuildRemoteClaimTrigger(null, new PendingIntent { SavedReason = "loop-continuation", Prompt = "answer" }, 1, null, "runner-1", "run-6"),
            LeaseEndpoints.BuildRemoteClaimTrigger(null, null, 1, concern, "runner-1", "run-7"),
            LeaseEndpoints.BuildRemoteClaimTrigger(null, null, 1, finding, "runner-1", "run-8"),
        };

        Assert.Equal(
            [
                RunTriggers.Initial,
                RunTriggers.DependencyRelease,
                RunTriggers.OperatorContinue,
                RunTriggers.IntegrationRecovery,
                RunTriggers.TimeoutContinuation,
                RunTriggers.Replan,
                RunTriggers.ReviewConcern,
                RunTriggers.ReviewFinding,
            ],
            cases.Select(item => item.Trigger).ToArray());
        Assert.All(cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.TriggeredBy));
            Assert.False(string.IsNullOrWhiteSpace(item.TriggerReason));
            Assert.False(string.IsNullOrWhiteSpace(item.TriggerSource));
        });
        Assert.Equal("pipeline", cases[6].TriggeredBy);
        Assert.Contains("review_01", cases[6].TriggerSource, StringComparison.Ordinal);
    }

    private static string? Var(RunPlan plan, string key) =>
        plan.PromptVariables.TryGetValue(key, out var value) ? value : null;
}
