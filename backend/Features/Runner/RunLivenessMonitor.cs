using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Run-Liveness Slice A executor (concept:
/// <c>docs/concepts/run-liveness-and-slot-semantics.md</c>, Rule 4). Enforces
/// the invariant <b>no zombie survives 60s</b>: every <c>3-progress</c> card
/// must have a live run-heartbeat, and a card whose owning run process is gone
/// is demoted by the runner itself.
///
/// <para>
/// Runs in two modes over the same pure policy (<see cref="RunLivenessPolicy"/>):
/// <list type="bullet">
///   <item><b>Boot adoption scan</b> (<see cref="AdoptOnBootAsync"/>): at
///   backend boot every <c>3-progress</c> card without a live process is acted
///   on immediately (grace = 0). This replaces the meta-cycle "stuck-in-progress"
///   escalation and the project-pause that used to babysit it: after a restart
///   the old backend's runs are all dead, so every card is adopted at once.</item>
///   <item><b>Uptime sweep</b> (<see cref="SweepAsync"/>): a short-cadence timer
///   (<see cref="RunLivenessMonitorHostedService"/>) demotes a card within the
///   60s budget if its owning run dies while the backend stays up (e.g. a
///   foreign backend sharing the workspace crashed).</item>
/// </list>
/// </para>
///
/// <para>
/// The heartbeat source is PHASE-AWARE and never keyed on "is a CLI process
/// alive" alone - during post-processing there is no CLI process, so a healthy
/// post-processing card would be wrongly demoted. Instead the heartbeat is the
/// <b>owning run</b>: the runner's active-run latch, a live tracked CLI process,
/// or a live owning-runner lease (<see cref="PickupLockFile.HasLiveOwner"/>).
/// The recovery is phase-aware too:
/// <list type="bullet">
///   <item><b>Execution interrupted</b> (belegt AGT-2006) -&gt; demote to
///   <c>2-ready</c> (<see cref="RunLivenessReasons.ProcessLost"/>) AND clear the
///   session-resume pointer so the retry does not walk into the "No conversation
///   found" / "no rollout found" launch-fail chain. The worktree is left intact
///   (never torn down here), so uncommitted work is preserved for the reissue -
///   the AGT-1945 "never lose work" invariant holds by construction.</item>
///   <item><b>Post-processing interrupted</b> (belegt AGT-1932: run finished AND
///   merged, only post-processing died) -&gt; re-trigger post-processing by
///   finishing the missed move to <c>4-auto-review</c>
///   (<see cref="RunLivenessReasons.PostProcessingLost"/>) instead of demoting,
///   which would re-run the completed agent.</item>
/// </list>
/// </para>
///
/// <para>Single-state-machine authority: every move goes through
/// <see cref="TaskTransitionService"/>. Idempotent: a second pass finds the
/// candidates already gone from <c>3-progress</c>. Every decision is appended to
/// <c>&lt;workspace&gt;/logs/run-liveness.jsonl</c> for after-the-fact review.</para>
/// </summary>
public sealed class RunLivenessMonitor
{
    private readonly TaskScannerService _scanner;
    private readonly TaskTransitionService _transitions;
    private readonly TaskSessionLog _sessions;
    private readonly PickupLockFile _pickupLock;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ITaskAccess _taskAccess;
    private readonly ILogger<RunLivenessMonitor> _logger;
    private readonly IJsonlAppender _appender;
    private readonly RunLeaseService? _leases;
    private readonly CliRouter? _cliRouter;

    /// <summary>Default uptime grace: silence tolerated before a missing heartbeat counts as process-lost.</summary>
    public const int DefaultGraceSeconds = 30;

    /// <summary>Test seam mirroring <see cref="StaleProgressArchiver"/>: replaces the runner-status lookup.</summary>
    internal Func<RunnerStatus?>? StatusProviderOverride { get; set; }

    public RunLivenessMonitor(
        TaskScannerService scanner,
        TaskTransitionService transitions,
        TaskSessionLog sessions,
        PickupLockFile pickupLock,
        OrchestratorChatLog chatLog,
        IServiceProvider services,
        IConfiguration configuration,
        ITaskAccess taskAccess,
        ILogger<RunLivenessMonitor> logger,
        IJsonlAppender? appender = null,
        RunLeaseService? leases = null,
        CliRouter? cliRouter = null)
    {
        _scanner = scanner;
        _transitions = transitions;
        _sessions = sessions;
        _pickupLock = pickupLock;
        _chatLog = chatLog;
        _services = services;
        _configuration = configuration;
        _taskAccess = taskAccess;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
        _leases = leases;
        _cliRouter = cliRouter;
    }

    /// <summary>
    /// Boot adoption scan: demote every <c>3-progress</c> card that has no live
    /// process, immediately (grace = 0). Runs synchronously at boot before the
    /// first runner tick.
    /// </summary>
    public Task<IReadOnlyList<RunLivenessOutcome>> AdoptOnBootAsync(CancellationToken ct = default)
        => ScanAsync(isBoot: true, ct);

    /// <summary>
    /// Uptime sweep: demote a card within the 60s budget once its owning run
    /// dies while the backend stays up. Applies the configured uptime grace.
    /// </summary>
    public Task<IReadOnlyList<RunLivenessOutcome>> SweepAsync(CancellationToken ct = default)
        => ScanAsync(isBoot: false, ct);

    private async Task<IReadOnlyList<RunLivenessOutcome>> ScanAsync(bool isBoot, CancellationToken ct)
    {
        var outcomes = new List<RunLivenessOutcome>();

        if (!_configuration.GetValue("Runner:RunLiveness:Enabled", true))
        {
            _logger.LogInformation("RunLivenessMonitor: disabled (Runner:RunLiveness:Enabled = false); skipping {Mode} scan.",
                isBoot ? "boot-adoption" : "uptime");
            return outcomes;
        }

        // Grace = 0 at boot (adopt every dead run at once); a small window during
        // uptime so a just-moved card is not demoted before its run claims the
        // heartbeat. Clamped so uptime demotion still lands inside the 60s budget.
        var graceSeconds = isBoot
            ? 0
            : Math.Clamp(_configuration.GetValue("Runner:RunLiveness:GraceSeconds", DefaultGraceSeconds), 0, 55);
        var now = DateTime.UtcNow;
        var activeByProject = SafeGetActiveJobIds();

        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            activeByProject.TryGetValue(entry.Name, out var activeJobId);

            // Snapshot every candidate BEFORE acting on any (matching the
            // StaleProgressArchiver measure-then-act discipline): the transition
            // path calls _scanner.FindJob, which stamps ownerClientId on sibling
            // task.json files and bumps their mtime; measuring up front judges
            // each folder on its pre-scan state.
            var candidates = new List<Candidate>();
            foreach (var laneFolder in _taskAccess.ListLaneFolders(entry.Path, TaskStates.Progress))
            {
                ct.ThrowIfCancellationRequested();
                if (_scanner.FindJob(laneFolder.Slug, entry.Path)?.Fixture == true)
                    continue;
                var folder = laneFolder.FolderPath;
                // Slice B owns a valid steer-pending wait: it intentionally has
                // no live CLI heartbeat and must receive its configured T-second
                // auto-answer / blocked decision, not Slice A's 30s process-lost
                // recovery. Only exclude a successfully parsed marker. A torn or
                // unreadable marker falls through to the ordinary liveness net
                // instead of creating a new indefinite wait.
                if (SteerPendingMarker.TryRead(folder, _logger) != null)
                    continue;
                var isActiveHere = !string.IsNullOrEmpty(activeJobId)
                    && string.Equals(laneFolder.Slug, activeJobId, StringComparison.OrdinalIgnoreCase);
                // A folder-shaped orphan (no task.json) is not a runnable run;
                // leave it to StaleProgressArchiver's debris/mid-move handling.
                var hasJobJson = File.Exists(Path.Combine(folder, "task.json"));
                var taskKey = hasJobJson ? TryReadTaskKey(folder) ?? laneFolder.Slug : laneFolder.Slug;
                var durableJobKey = TaskIdentity.CreateKey(entry.Path, laneFolder.Slug);
                candidates.Add(new Candidate(
                    laneFolder.Slug,
                    folder,
                    hasJobJson,
                    isActiveHere,
                    HasRemoteLeaseHistory: _leases?.Inspect(taskKey).Lease is not null,
                    HasLiveHeartbeat: isActiveHere
                                      || _pickupLock.HasLiveOwner(folder)
                                      || (_cliRouter?.CanReattach(durableJobKey) ?? false),
                    HasVisibleWaitingState: hasJobJson && HasVisibleWaitingState(folder),
                    CoreRunFinished: hasJobJson && RunFinishedSignal.CoreRunFinished(folder),
                    SecondsSinceActivity: (now - MeasureLastActivity(folder)).TotalSeconds));
            }

            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!c.HasJobJson)
                {
                    // Not a runnable run - out of scope for run-liveness.
                    continue;
                }

                // Remote progress recovery is deliberately owned by the claim
                // handshake. After a Task Server restart an old lease can be
                // invalid while its host CLI is still alive; only the assigned
                // runner can answer that question after the remote grace.
                if (c.HasRemoteLeaseHistory)
                    continue;

                var facts = new RunLivenessFacts(
                    HasLiveRunHeartbeat: c.HasLiveHeartbeat,
                    CoreRunFinished: c.CoreRunFinished,
                    SecondsSinceActivity: c.SecondsSinceActivity,
                    GraceSeconds: graceSeconds,
                    HasVisibleWaitingState: c.HasVisibleWaitingState);
                var decision = RunLivenessPolicy.Decide(facts);

                switch (decision.Action)
                {
                    case RunLivenessAction.Healthy:
                    case RunLivenessAction.VisibleWait:
                    case RunLivenessAction.WithinGrace:
                        // Healthy / too-fresh: no move, no audit-log noise (mirrors
                        // the archiver leaving "fresh" verdicts unpersisted).
                        break;

                    case RunLivenessAction.DemoteToReady:
                    {
                        var outcome = await DemoteToReadyAsync(entry, c, decision, now, ct);
                        outcomes.Add(outcome);
                        AppendAudit(outcome);
                        break;
                    }

                    case RunLivenessAction.RetriggerPostProcessing:
                    {
                        var outcome = await RetriggerPostProcessingAsync(entry, c, decision, now, ct);
                        outcomes.Add(outcome);
                        AppendAudit(outcome);
                        break;
                    }
                }
            }
        }

        var actionable = outcomes.Count(o =>
            o.Kind is RunLivenessOutcomeKinds.DemotedProcessLost
                   or RunLivenessOutcomeKinds.RetriggeredPostProcessing
                   or RunLivenessOutcomeKinds.SettledRunRecovered);
        if (actionable > 0 || isBoot)
        {
            _logger.LogInformation(
                "RunLivenessMonitor: {Mode} scan acted on {Actionable} of {Total} 3-progress card(s) with no live run-heartbeat.",
                isBoot ? "boot-adoption" : "uptime", actionable, outcomes.Count);
        }

        return outcomes;
    }

    private static bool HasVisibleWaitingState(string jobFolder)
    {
        try
        {
            var path = Path.Combine(jobFolder, "task.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("phase", out var phase)) return false;
            var value = phase.GetString();
            return string.Equals(value, LifecyclePhases.LoopWaiting, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, LifecyclePhases.SteerPending, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<RunLivenessOutcome> DemoteToReadyAsync(
        WatchPathEntry entry, Candidate c, RunLivenessDecision decision, DateTime now, CancellationToken ct)
    {
        var jobId = TryReadJobId(c.JobFolder) ?? c.Slug;

        try
        {
            var move = await _transitions.MoveAsync(
                jobId,
                TaskStates.Ready,
                entry.Path,
                ct,
                cause: "run-liveness-detector",
                suppressProductExecution: true,
                transitionCause: LaneChangeCauses.LeaseRecovery,
                transitionDetail: "run-liveness-process-lost");
            if (move.Status != MoveJobStatus.Success)
            {
                return Outcome(RunLivenessOutcomeKinds.DemoteFailed, entry, c, decision, jobId,
                    target: TaskStates.Ready,
                    reason: $"demote to {TaskStates.Ready} refused: {move.Status} {move.Message}", now);
            }

            var moved = _scanner.FindJob(jobId, entry.Path);
            var movedFolder = moved?.FolderPath ?? move.NewFolderPath ?? c.JobFolder;
            _pickupLock.ClearIfStale(movedFolder);
            if (string.Equals(moved?.State, TaskStates.AutoReview, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "RunLivenessMonitor: recovered settled run {JobId} -> 4-auto-review; no replacement run was queued and the completed session was preserved.",
                    jobId);
                return Outcome(
                    RunLivenessOutcomeKinds.SettledRunRecovered,
                    entry,
                    c,
                    decision,
                    jobId,
                    target: TaskStates.AutoReview,
                    reason: "attempt authority reported a completed immutable result; recovered the existing delivery to auto-review instead of requeueing",
                    now,
                    reasonCode: "settled-run-authority");
            }

            // Authority has now confirmed that this is an interrupted run, not
            // a settled delivery. Break the launch-fail chain only on the real
            // Ready path so BP-09 recovery preserves the completed session.
            _sessions.SetJobSessionName(jobId, null, entry.Path);
            _sessions.MarkSessionChainRecovery(jobId, entry.Path);

            // One compact recovery line travels with the Ready folder. The
            // worktree remains task-owned so uncommitted work survives retry.
            WriteDemotionDiagnostic(movedFolder, c.SecondsSinceActivity);

            _logger.LogWarning(
                "RunLivenessMonitor: demoted {JobId} 3-progress -> 2-ready (process-lost, {Silence:F0}s silent) and cleared the session-resume pointer.",
                jobId, c.SecondsSinceActivity);
            return Outcome(RunLivenessOutcomeKinds.DemotedProcessLost, entry, c, decision, jobId,
                target: TaskStates.Ready,
                reason: decision.Detail, now);
        }
        catch (Exception ex)
        {
            return Outcome(RunLivenessOutcomeKinds.DemoteFailed, entry, c, decision, jobId,
                target: TaskStates.Ready, reason: $"exception: {ex.Message}", now);
        }
    }

    private async Task<RunLivenessOutcome> RetriggerPostProcessingAsync(
        WatchPathEntry entry, Candidate c, RunLivenessDecision decision, DateTime now, CancellationToken ct)
    {
        var jobId = TryReadJobId(c.JobFolder) ?? c.Slug;

        // The agent run finished (and, in the AGT-1932 case, was already merged);
        // re-running it would waste the completed work. Finish the missed
        // hand-off into 4-auto-review, where the ReviewDecisionOrchestrator
        // boot/interval sweep and the summary backstop own the rest of
        // post-processing. Do NOT clear the resume pointer: the finished session
        // is not going to be resumed, and tombstoning it would lose run history.
        try
        {
            var move = await _transitions.MoveAsync(
                jobId, TaskStates.AutoReview, entry.Path, ct,
                transitionCause: LaneChangeCauses.Delivered,
                transitionDetail: "run-liveness-post-processing-retrigger");
            if (move.Status != MoveJobStatus.Success)
            {
                return Outcome(RunLivenessOutcomeKinds.RetriggerFailed, entry, c, decision, jobId,
                    target: TaskStates.AutoReview,
                    reason: $"re-trigger to {TaskStates.AutoReview} refused: {move.Status} {move.Message}", now);
            }

            var moved = _scanner.FindJob(jobId, entry.Path);
            if (moved != null)
            {
                // The card reached 4-auto-review, so a surviving "ready to
                // transition" marker has done its job; clear it (matches the
                // runner clearing its own marker after a successful move) so
                // CrashRecoveryService does not see a stale marker later.
                CompletionMarker.Clear(moved.FolderPath, _logger);
                _chatLog.AppendSupervisor(
                    moved,
                    "post-processing-recovered",
                    "Run-liveness recovery: the agent run had already finished (agent_run_finished) but post-processing " +
                    "lost its heartbeat when the backend stopped. Re-triggered post-processing by promoting to 4-auto-review " +
                    "instead of re-running the completed agent.");
            }

            _logger.LogWarning(
                "RunLivenessMonitor: re-triggered post-processing for {JobId} 3-progress -> 4-auto-review (post-processing-lost); the finished agent run is not re-run.",
                jobId);
            return Outcome(RunLivenessOutcomeKinds.RetriggeredPostProcessing, entry, c, decision, jobId,
                target: TaskStates.AutoReview, reason: decision.Detail, now);
        }
        catch (Exception ex)
        {
            return Outcome(RunLivenessOutcomeKinds.RetriggerFailed, entry, c, decision, jobId,
                target: TaskStates.AutoReview, reason: $"exception: {ex.Message}", now);
        }
    }

    private Dictionary<string, string?> SafeGetActiveJobIds()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            RunnerStatus? status = StatusProviderOverride != null
                ? StatusProviderOverride()
                : (_services.GetService(typeof(TaskRunnerService)) as TaskRunnerService)?.GetStatus();
            if (status?.Projects == null) return map;
            foreach (var (projectName, projectStatus) in status.Projects)
                map[projectName] = projectStatus?.ActiveJobId;
        }
        catch (Exception ex)
        {
            // At boot the runner may not be wired yet; an empty map means "treat
            // nothing as actively running", which is the correct boot posture
            // (every 3-progress card is an adoption candidate).
            _logger.LogWarning(ex, "RunLivenessMonitor: could not read runner status; treating no jobs as active.");
        }
        return map;
    }

    /// <summary>
    /// Last run-produced activity for a folder: the max over every run-produced
    /// file mtime under <c>logs/</c> and the stable <c>enteredLaneAt</c> value in
    /// <c>task.json</c> (content, never the file mtime - a metadata edit bumps
    /// the mtime and would mask a real zombie). Mirrors the run-bound liveness
    /// signal <see cref="StaleProgressArchiver"/> settled on. A folder with no
    /// run-produced signal at all floors to <c>enteredLaneAt</c>, or epoch when
    /// even that is missing (so it always crosses the grace).
    /// </summary>
    private static DateTime MeasureLastActivity(string jobFolder)
    {
        var maxStamp = DateTime.MinValue.ToUniversalTime();
        var logsDir = Path.Combine(jobFolder, TaskPaths.LogsDirName);
        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.EnumerateFiles(logsDir))
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (stamp > maxStamp) maxStamp = stamp;
                }
                catch (Exception __ex)
                {
                    SilentCatch.Note(__ex, "RunLivenessMonitor: unreadable log file - skip");
                }
            }
        }

        var entered = TryReadEnteredLaneAt(Path.Combine(jobFolder, "task.json"));
        if (entered is { } e && e > maxStamp) maxStamp = e;
        return maxStamp;
    }

    private static DateTime? TryReadEnteredLaneAt(string jobJsonPath)
    {
        if (!File.Exists(jobJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jobJsonPath));
            if (doc.RootElement.TryGetProperty("enteredLaneAt", out var el)
                && el.ValueKind == JsonValueKind.String
                && el.TryGetDateTime(out var dt))
                return dt.ToUniversalTime();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "RunLivenessMonitor: unreadable enteredLaneAt - log mtimes drive the verdict");
        }
        return null;
    }

    private static string? TryReadJobId(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "task.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch { return null; }
    }

    private static string? TryReadTaskKey(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "task.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var name in new[] { "key", "taskKey", "id" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            }
        }
        catch (Exception ex)
        {
            // The existing malformed-folder recovery owns unreadable task JSON.
            SilentCatch.Note(
                ex,
                "RunLivenessMonitor: unreadable task key; folder slug drives the lease-history lookup");
        }
        return null;
    }

    private void WriteDemotionDiagnostic(string jobFolder, double silenceSeconds)
    {
        try
        {
            var logsDir = Path.Combine(jobFolder, TaskPaths.LogsDirName);
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, TaskPaths.CliOutputLogFileName);
            var line = RecoveryChatLine.PersistedLine(
                DateTime.UtcNow,
                RecoveryChatLine.ReasonHostRestart,
                $"run process lost in 3-progress ({silenceSeconds:F0}s no heartbeat)",
                $"requeued to {TaskStates.Ready}",
                sessionResumed: false);
            CliOutputLogFile.Append(logPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunLivenessMonitor: failed to write demotion diagnostic for {Folder}", jobFolder);
        }
    }

    private static RunLivenessOutcome Outcome(
        string kind, WatchPathEntry entry, Candidate c, RunLivenessDecision decision,
        string jobId, string? target, string reason, DateTime at, string? reasonCode = null)
        => new()
        {
            At = at,
            Kind = kind,
            ReasonCode = reasonCode ?? decision.ReasonCode,
            ProjectName = entry.Name,
            Slug = c.Slug,
            JobId = jobId,
            TargetState = target,
            SilenceSeconds = (long)c.SecondsSinceActivity,
            Reason = reason,
        };

    private void AppendAudit(RunLivenessOutcome outcome)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug("RunLivenessMonitor: TaskRepository not configured; skipping run-liveness.jsonl for {Slug}.", outcome.Slug);
            return;
        }
        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "run-liveness.jsonl");
            _appender.AppendAsync(path, outcome, JsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunLivenessMonitor: failed to append run-liveness.jsonl");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly record struct Candidate(
        string Slug,
        string JobFolder,
        bool HasJobJson,
        bool IsActiveHere,
        bool HasRemoteLeaseHistory,
        bool HasLiveHeartbeat,
        bool HasVisibleWaitingState,
        bool CoreRunFinished,
        double SecondsSinceActivity);
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/run-liveness.jsonl</c>.</summary>
public sealed record RunLivenessOutcome
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("targetState")] public string? TargetState { get; init; }
    [JsonPropertyName("silenceSeconds")] public long? SilenceSeconds { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="RunLivenessOutcome.Kind"/>.</summary>
public static class RunLivenessOutcomeKinds
{
    /// <summary>Execution interrupted (process-lost): demoted 3-progress -&gt; 2-ready and resume pointer cleared.</summary>
    public const string DemotedProcessLost = "demoted-process-lost";
    /// <summary>The demotion move to 2-ready refused or threw.</summary>
    public const string DemoteFailed = "demote-failed";
    /// <summary>Core run finished, post-processing lost: re-triggered 3-progress -&gt; 4-auto-review.</summary>
    public const string RetriggeredPostProcessing = "retriggered-post-processing";
    /// <summary>Attempt authority found a completed immutable result, so the existing delivery advanced to review.</summary>
    public const string SettledRunRecovered = "settled-run-recovered";
    /// <summary>The re-trigger move to 4-auto-review refused or threw.</summary>
    public const string RetriggerFailed = "retrigger-failed";
}
