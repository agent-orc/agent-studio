

namespace AgentStudio.Runner;

/// <summary>
/// Lets the orchestrator speak directly into the chat transcript as a
/// first-class participant.
///
/// <para>
/// <b>Why this exists.</b> The product treats orchestrator-to-CLI
/// communication as a core capability, not a side-effect. When the
/// orchestrator decides to re-issue a follow-up, accept a heuristic
/// verdict, or warn that the deterministic contract did not match, the
/// user has to see that decision next to the agent's own messages.
/// Hiding it in the backend log file would defeat the point. The
/// activity log already pulls from <c>logs/cli-output.log</c>, so the
/// cheapest reliable channel is to append <c>[orchestrator]</c>-stream
/// lines there.
/// </para>
///
/// <para>
/// Lines written here use the same persisted shape the CLI output
/// parser already understands (timestamp + bracketed stream tag), so
/// the disk-backed activity-log fallback continues to work and the
/// frontend can pick the messages up by stream alone.
/// </para>
/// </summary>
public class OrchestratorChatLog
{
    private readonly ILogger<OrchestratorChatLog> _logger;
    private readonly AgentMessageBusBridge? _bus;

    public OrchestratorChatLog(ILogger<OrchestratorChatLog> logger, AgentMessageBusBridge? bus = null)
    {
        _logger = logger;
        _bus = bus;
    }

    /// <summary>
    /// Append one orchestrator meta message to the job's <c>cli-output.log</c>
    /// and the runtime in-memory buffer (when one is supplied). The kind
    /// (<paramref name="kind"/>) becomes a leading tag on the persisted line
    /// (e.g. <c>[reissue]</c>) so future parsers can pick out structured
    /// classes without re-deriving them from the prose.
    /// </summary>
    public virtual bool Append(TaskInfo info, OrchestratorMessageKind kind, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        var ok = AppendWithStream(info, "orchestrator", $"[{kind.ToTag()}] {text}", liveBuffer);
        if (ok)
        {
            // Bridge to the Agent Message Bus. Best-effort; the chat log is the
            // canonical record (the activity-log parser reads it). The bus
            // mirrors typed entries so future tooling can query without
            // reparsing prose. See docs/system/architecture/bus/agent-message-bus.md section 9.
            try { _ = _bus?.EmitOrchestratorChatAsync(info, kind, text); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator chat failed for {JobId}", info?.Id); }
        }
        return ok;
    }

    /// <summary>
    /// Append a meta message attributed to the supervisor participant. Same
    /// persistence shape as the orchestrator stream, but with the
    /// <c>[supervisor]</c> stream tag so the activity-log parser renders it
    /// as a separate participant alongside <c>You</c>, the agent, and
    /// <c>Orchestrator</c>.
    /// </summary>
    public bool AppendSupervisor(TaskInfo info, string tag, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        var ok = AppendWithStream(info, "supervisor", $"[{tag}] {text}", liveBuffer);
        if (ok)
        {
            try { _ = _bus?.EmitSupervisorChatAsync(info, tag, text); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of supervisor chat failed for {JobId}", info?.Id); }
        }
        return ok;
    }

    private bool AppendWithStream(TaskInfo info, string streamTag, string body, ICollection<CliOutputLine>? liveBuffer)
    {
        if (info == null) return false;
        // If the job folder no longer exists, the job was moved (or deleted)
        // between the caller's lookup and this append. Recreating the folder
        // here would resurrect the source lane as a one-line skeleton —
        // exactly the residue that was littering 4-auto-review after every
        // accept-as-done. Refuse the write and let the caller treat it as
        // best-effort; the canonical record (decision journal, bus event)
        // still goes out.
        if (!Directory.Exists(info.FolderPath))
        {
            _logger.LogWarning(
                "OrchestratorChatLog: refusing to append {Stream} for {JobId}; folder gone at {Path}",
                streamTag, info.Id, info.FolderPath);
            return false;
        }
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            var ts = DateTime.UtcNow;
            var oneLine = (body ?? string.Empty).Replace("\r", " ").Replace("\n", " ").TrimEnd();
            var persistLine = $"[{ts:HH:mm:ss.fff}] [{streamTag}] {oneLine}";
            CliOutputLogFile.Append(logPath, persistLine);

            if (liveBuffer != null)
            {
                liveBuffer.Add(new CliOutputLine
                {
                    Timestamp = ts,
                    Stream = streamTag,
                    Text = oneLine
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append {Stream} message for {JobId}", streamTag, info.Id);
            return false;
        }
    }
}

/// <summary>
/// Kinds of orchestrator meta messages. Each maps to a short tag on the
/// persisted line so the frontend can render different glyphs / colors
/// without parsing the prose. Kept narrow so we are not tempted to use
/// the meta channel for general logging.
/// </summary>
public enum OrchestratorMessageKind
{
    /// <summary>The orchestrator made a decision about how to proceed (informational).</summary>
    Decision,
    /// <summary>The orchestrator is re-issuing a follow-up because the agent did not honor it.</summary>
    Reissue,
    /// <summary>The deterministic contract did not match; classification is a heuristic best-effort.</summary>
    HeuristicFallback,
    /// <summary>The orchestrator is intervening once to repair a recoverable protocol or tool-boundary issue.</summary>
    SoftIntervention,
    /// <summary>The agent hit tool permission boundaries and exhausted the one soft intervention.</summary>
    PermissionBlocked,
    /// <summary>The watchdog killed the run after a silence timeout.</summary>
    WatchdogTimeout,
    /// <summary>
    /// The watchdog noticed a silence gap but has not killed the run yet
    /// (Quiet or Suspicious states). Operator-facing copy is informational
    /// ("no action needed unless this repeats"); the topic is distinct from
    /// <see cref="Decision"/> so the workspace banner does not misread a
    /// silence advisory as a review verdict.
    /// </summary>
    WatchdogWarning,
    /// <summary>The agent did not emit the required terminal sentinel after one prompt repair.</summary>
    MissingTerminalSentinel,
    /// <summary>The agent reported done without a structured sentinel; kept as visible legacy heuristic.</summary>
    HeuristicDone,
    /// <summary>The agent CLI crashed hard (process death, exitCode &lt; 0) before reaching a terminal verdict; the orchestrator stops and surfaces it for review.</summary>
    InfraCrash,
    /// <summary>The run failed with real agent text the contract could not map to a terminal verdict; the orchestrator stops and hands the task to the user.</summary>
    OrchestratorInconclusive,
    /// <summary>
    /// The agent CLI failed to launch or its <c>--resume</c> target was
    /// rejected before any agent turn happened (exit != 0, ~0s, only a CLI
    /// error fragment). The orchestrator treats this as a recoverable
    /// host/CLI condition and rebuilds from disk via Recovery on the next
    /// attempt, rather than surfacing a terminal inconclusive FAILURE.
    /// </summary>
    CliLaunchFailed,
    /// <summary>
    /// The agent CLI exited almost immediately without producing an agent turn.
    /// This is a failed start, not an explicit agent no-op.
    /// </summary>
    EmptyFastExit,
    /// <summary>The orchestrator gave up after a retry budget; user attention required.</summary>
    GiveUp,
    /// <summary>The orchestrator could not pick a path on its own but identified a concrete unblocking ask the user can resolve. Renders distinctly so the user sees a productive escalation, not a silent deferral.</summary>
    Steer,
    /// <summary>
    /// An OS / sandbox / host-permission blocker was detected in-stream
    /// by <see cref="AgentEnvironmentDetector"/>. The run was killed
    /// before the silence budget elapsed; the job is escalated to human
    /// review with a typed diagnosis instead of a generic
    /// missing-terminal-sentinel verdict.
    /// </summary>
    EnvironmentBlocker,
    /// <summary>
    /// Codex stopped emitting frames after a successful tool call but
    /// never sent a closing <c>turn.completed</c> or sentinel
    /// (<see cref="CodexSilentCompletionDetector"/>). The runner finalized
    /// the run as Completed with the <c>outcome:silent-finish</c> tag so
    /// the auto-review aspect calls still run and the user sees why no
    /// sentinel landed.
    /// </summary>
    SilentCompletion,
    /// <summary>
    /// The run exceeded the model's input window (prompt too long / context
    /// length). Non-retryable: the orchestrator routes it straight to human
    /// review instead of re-issuing into the same overflow.
    /// </summary>
    ContextOverflow,
    /// <summary>
    /// The per-task circuit breaker tripped after N consecutive failed runs
    /// without progress; the task was parked in human review to stop an
    /// endless reissue loop.
    /// </summary>
    Quarantined,
    /// <summary>
    /// The configured model is invalid/unsupported for this account or CLI
    /// (invalid_request / HTTP 400 "model not supported"). Non-retryable: the
    /// orchestrator routes it to human review with a model-invalid reason so a
    /// human changes the model, instead of re-issuing into the same rejection.
    /// </summary>
    ModelInvalid,
    /// <summary>
    /// The account's usage/session/rate-limit budget is exhausted. Transient:
    /// the orchestrator routes it to human review with a quota-exhausted reason
    /// (and schedules a rate-limit cooldown) rather than the misleading
    /// orchestrator-inconclusive, so re-queueing after reset is the clear next
    /// step.
    /// </summary>
    QuotaExhausted,
    /// <summary>
    /// The agent CLI could not launch because its OAuth session expired and the
    /// token refresh failed (AGT-2066 token roulette). Shared across every
    /// parallel run and non-retryable, so the orchestrator STOPS immediately
    /// (the breaker) and routes to human review with a re-auth instruction
    /// rather than burning further launch budgets.
    /// </summary>
    AuthRefreshFailed,
    /// <summary>
    /// A transient environmental fault (host file lock / MSB302x copy-lock,
    /// network glitch) failed the run. The condition clears on its own, so the
    /// orchestrator retries the task with exponential backoff before escalating -
    /// this line marks the automatic retry, not a give-up (AGT-1944).
    /// </summary>
    EnvironmentalRetry,
    /// <summary>
    /// Worktree preparation failed before the CLI process started. The marker
    /// carries the stable failure code, Git message, retry count, and path so a
    /// Ready card remains visibly failed during bounded backoff.
    /// </summary>
    WorktreePreparationFailed,
    /// <summary>
    /// A worktree-isolated run modified the shared main checkout. The runner
    /// skipped integration and surfaced the harness integrity violation.
    /// </summary>
    WorktreeContainment,
    /// <summary>
    /// The runner verified genuine git damage during the worker window, such
    /// as a protected-branch push or a rewrite of pre-existing history.
    /// </summary>
    AgentGitViolation,
    /// <summary>
    /// Informational finding: the worker advanced HEAD before the platform
    /// commit. Post-processing either folded it back automatically or left a
    /// visible cleanup hint while allowing the pipeline to continue.
    /// </summary>
    WorkerHeadAdvanced,
    /// <summary>
    /// A parallel worktree branch could not be integrated into the configured
    /// work branch and needs manual merge/conflict resolution.
    /// </summary>
    IntegrationConflict,
    /// <summary>
    /// A parallel worktree integration step failed for a non-conflict git error.
    /// </summary>
    IntegrationError,
    /// <summary>
    /// The runner could not push a finished task branch to origin after retry.
    /// </summary>
    TaskBranchUnpushed,
    /// <summary>
    /// The platform recovered an interrupted run (crash requeue, watchdog
    /// reissue, host-restart resume, system-sleep wake). Renders as one calm
    /// <c>[recovery]</c> line; the long-form rationale stays in the run /
    /// lifecycle artifacts. Body is built by <see cref="RecoveryChatLine"/>.
    /// </summary>
    Recovery
}

internal static class OrchestratorMessageKindExtensions
{
    public static string ToTag(this OrchestratorMessageKind kind) => kind switch
    {
        OrchestratorMessageKind.Decision          => "decision",
        OrchestratorMessageKind.Reissue           => "reissue",
        OrchestratorMessageKind.HeuristicFallback => "heuristic",
        OrchestratorMessageKind.SoftIntervention  => "intervention",
        OrchestratorMessageKind.PermissionBlocked => "permission-blocked",
        OrchestratorMessageKind.WatchdogTimeout   => "watchdog-timeout",
        OrchestratorMessageKind.WatchdogWarning   => "watchdog",
        OrchestratorMessageKind.MissingTerminalSentinel => "missing-terminal-sentinel",
        OrchestratorMessageKind.HeuristicDone     => "heuristic-done",
        OrchestratorMessageKind.InfraCrash        => "infra-crash",
        OrchestratorMessageKind.OrchestratorInconclusive => "orchestrator-inconclusive",
        OrchestratorMessageKind.CliLaunchFailed   => "cli-launch-failed",
        OrchestratorMessageKind.EmptyFastExit     => "empty-fast-exit",
        OrchestratorMessageKind.GiveUp            => "giveup",
        OrchestratorMessageKind.Steer             => "steer",
        OrchestratorMessageKind.EnvironmentBlocker => "environment-blocker",
        OrchestratorMessageKind.SilentCompletion  => "codex-silent-completion",
        OrchestratorMessageKind.ContextOverflow   => "context-overflow",
        OrchestratorMessageKind.Quarantined       => "quarantined",
        OrchestratorMessageKind.ModelInvalid      => "model-invalid",
        OrchestratorMessageKind.QuotaExhausted    => "quota-exhausted",
        OrchestratorMessageKind.AuthRefreshFailed => "auth-refresh-failed",
        OrchestratorMessageKind.EnvironmentalRetry => "environmental-retry",
        OrchestratorMessageKind.WorktreePreparationFailed => "worktree-preparation-failed",
        OrchestratorMessageKind.WorktreeContainment => "worktree-containment",
        OrchestratorMessageKind.AgentGitViolation => "agent-git-violation",
        OrchestratorMessageKind.WorkerHeadAdvanced => "worker-head-advanced",
        OrchestratorMessageKind.IntegrationConflict => "integration-conflict",
        OrchestratorMessageKind.IntegrationError  => "integration-error",
        OrchestratorMessageKind.TaskBranchUnpushed => "task-branch-unpushed",
        OrchestratorMessageKind.Recovery          => RecoveryChatLine.RecoveryTag,
        _ => "info"
    };

    public static string ToBusTopic(this OrchestratorMessageKind kind) => kind switch
    {
        OrchestratorMessageKind.HeuristicFallback => "heuristicfallback",
        OrchestratorMessageKind.SoftIntervention  => "soft-intervention",
        OrchestratorMessageKind.PermissionBlocked => "permission-blocked",
        OrchestratorMessageKind.WatchdogTimeout   => "watchdog-timeout",
        OrchestratorMessageKind.WatchdogWarning   => "watchdog-warning",
        OrchestratorMessageKind.MissingTerminalSentinel => "missing-terminal-sentinel",
        OrchestratorMessageKind.HeuristicDone     => "heuristic-done",
        OrchestratorMessageKind.InfraCrash        => "infra-crash",
        OrchestratorMessageKind.OrchestratorInconclusive => "orchestrator-inconclusive",
        OrchestratorMessageKind.CliLaunchFailed   => "cli-launch-failed",
        OrchestratorMessageKind.EmptyFastExit     => "empty-fast-exit",
        OrchestratorMessageKind.EnvironmentBlocker => "environment-blocker",
        OrchestratorMessageKind.SilentCompletion  => "codex-silent-completion",
        OrchestratorMessageKind.ContextOverflow   => "context-overflow",
        OrchestratorMessageKind.Quarantined       => "quarantined",
        OrchestratorMessageKind.ModelInvalid      => "model-invalid",
        OrchestratorMessageKind.QuotaExhausted    => "quota-exhausted",
        OrchestratorMessageKind.AuthRefreshFailed => "auth-refresh-failed",
        OrchestratorMessageKind.EnvironmentalRetry => "environmental-retry",
        OrchestratorMessageKind.WorktreePreparationFailed => "worktree-preparation-failed",
        OrchestratorMessageKind.WorktreeContainment => "worktree-containment",
        OrchestratorMessageKind.AgentGitViolation => "agent-git-violation",
        OrchestratorMessageKind.WorkerHeadAdvanced => "worker-head-advanced",
        OrchestratorMessageKind.IntegrationConflict => "integration-conflict",
        OrchestratorMessageKind.IntegrationError  => "integration-error",
        OrchestratorMessageKind.TaskBranchUnpushed => "task-branch-unpushed",
        _ => kind.ToString().ToLowerInvariant()
    };
}
