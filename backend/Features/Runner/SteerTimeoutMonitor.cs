using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Run-Liveness Slice B executor (concept:
/// <c>docs/concepts/run-liveness-and-slot-semantics.md</c>, Rule 2). Enforces
/// the steer-timeout invariant: <b>no steered / NeedsInput card waits
/// indefinitely</b>. An auto-mode run that asks a question the orchestrator
/// cannot answer on its own leaves a durable <see cref="SteerPendingRecord"/>
/// marker; this monitor sweeps those markers and, once a card has waited past
/// its bounded timeout, resolves the wait one of two ways (concept Rule 2):
/// <list type="bullet">
///   <item><b>Auto-answer</b> - when the answer is unambiguous from the task
///   context (the branch-state check for the "is this already implemented?"
///   class), feed it back as a Continue so the run resumes.</item>
///   <item><b>Blocked</b> - otherwise route the card to a normal
///   <c>5e-escalated</c> escalation with a clear reason, via the
///   <see cref="HumanReviewEscalation"/> funnel. Never an endless wait.</item>
/// </list>
///
/// <para>
/// Belegt (2026-07-10): three cards (2062/2067/2068) hung ~5 hours on steer
/// questions whose work was already merged. The loss was invisible because no
/// lane moved. Slice A demotes a card whose PROCESS is gone; a steered card is
/// waiting on purpose (so it is excluded from Slice A's heartbeat check) and
/// needs this separate bounded wait + phase-aware recovery.
/// </para>
///
/// <para>
/// Same shape as <see cref="RunLivenessMonitor"/>: a short-cadence uptime sweep
/// (<see cref="SteerTimeoutMonitorHostedService"/>) over one pure policy
/// (<see cref="SteerTimeoutPolicy"/>). Every decision is appended to
/// <c>&lt;workspace&gt;/logs/steer-timeout.jsonl</c> and mirrored to the card's
/// timeline (<see cref="TimelineEventKinds.SteerTimeoutResolved"/>). Idempotent:
/// acting moves the card out of <c>3-progress</c>, so a second sweep no longer
/// finds it there.
/// </para>
/// </summary>
public sealed class SteerTimeoutMonitor : IAiOperatorEffect
{
    private readonly TaskScannerService _scanner;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly HumanReviewEscalation _escalation;
    private readonly ProjectSettingsService _projectSettings;
    private readonly OrchestratorChatLog _chatLog;
    private readonly ISteerTimeoutResolver _resolver;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly AgentStudio.TaskAccess.ITaskAccess _taskAccess;
    private readonly ILogger<SteerTimeoutMonitor> _logger;
    private readonly AgentStudio.Tasks.TimelineLog? _timeline;
    private readonly IJsonlAppender _appender;
    private readonly AiOperatorDecisionGateway _decisions;

    /// <summary>Default bounded wait before an unanswered steer times out (concept Rule 2: "Default 120s, konfigurierbar").</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Test seam mirroring <see cref="RunLivenessMonitor.StatusProviderOverride"/>: replaces the runner-status lookup.</summary>
    internal Func<RunnerStatus?>? StatusProviderOverride { get; set; }

    public SteerTimeoutMonitor(
        TaskScannerService scanner,
        TaskTransitionService transitions,
        TaskMutationService mutations,
        HumanReviewEscalation escalation,
        ProjectSettingsService projectSettings,
        OrchestratorChatLog chatLog,
        ISteerTimeoutResolver resolver,
        IServiceProvider services,
        IConfiguration configuration,
        AgentStudio.TaskAccess.ITaskAccess taskAccess,
        ILogger<SteerTimeoutMonitor> logger,
        AgentStudio.Tasks.TimelineLog? timeline = null,
        IJsonlAppender? appender = null,
        AiOperatorDecisionGateway? decisions = null)
    {
        _scanner = scanner;
        _transitions = transitions;
        _mutations = mutations;
        _escalation = escalation;
        _projectSettings = projectSettings;
        _chatLog = chatLog;
        _resolver = resolver;
        _services = services;
        _configuration = configuration;
        _taskAccess = taskAccess;
        _logger = logger;
        _timeline = timeline;
        _appender = appender ?? new JsonlAppender();
        _decisions = decisions ?? AiOperatorDecisionGateway.ForTestHost(configuration);
    }

    public string Decider => AiOperatorDeciders.SteerTimeout;

    /// <summary>
    /// One uptime sweep: for every <c>3-progress</c> card carrying a
    /// steer-pending marker or persisted steer-pending lifecycle phase, apply
    /// <see cref="SteerTimeoutPolicy"/> and execute
    /// the verdict. Returns the actions taken (auto-answered / blocked), for the
    /// hosted service to log and for tests to assert.
    /// </summary>
    public async Task<IReadOnlyList<SteerTimeoutOutcome>> SweepAsync(CancellationToken ct = default)
    {
        var outcomes = new List<SteerTimeoutOutcome>();

        if (!_configuration.GetValue("Runner:SteerTimeout:Enabled", true))
            return outcomes;

        var configTimeout = Math.Max(1, _configuration.GetValue("Runner:SteerTimeout:TimeoutSeconds", DefaultTimeoutSeconds));
        var now = DateTime.UtcNow;
        var status = SafeGetStatus();

        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            var projectStatus = status?.Projects != null && status.Projects.TryGetValue(entry.Name, out var ps) ? ps : null;
            var activeJobId = projectStatus?.ActiveJobId;
            var hasLiveActiveExecution = string.Equals(
                projectStatus?.ActiveExecution?.Status,
                "running",
                StringComparison.OrdinalIgnoreCase);

            // Snapshot candidates before acting on any (matching the Slice A /
            // StaleProgressArchiver measure-then-act discipline): acting moves a
            // folder, and FindJob has folder side effects.
            var candidates = new List<Candidate>();
            foreach (var laneFolder in _taskAccess.ListLaneFolders(entry.Path, TaskStates.Progress))
            {
                ct.ThrowIfCancellationRequested();
                if (_scanner.FindJob(laneFolder.Slug, entry.Path)?.Fixture == true)
                    continue;
                // A real live execution wins the race. ActiveJobId alone is not
                // enough: marker creation happens after Release, and a stale or
                // leaked latch must not suppress the timeout forever. A resumed
                // run clears the marker before it claims a new slot.
                if (hasLiveActiveExecution
                    && !string.IsNullOrEmpty(activeJobId)
                    && string.Equals(laneFolder.Slug, activeJobId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var marker = SteerPendingMarker.TryRead(laneFolder.FolderPath, _logger)
                    ?? RecoverMarkerFromPhase(laneFolder.Slug, laneFolder.FolderPath, entry.Path);
                if (marker == null) continue;
                // UI iteration reviews are deliberate human gates, not
                // unanswered agent questions. Part 2 consumes this marker;
                // the generic 120-second steer timeout must never auto-answer it.
                if (string.Equals(marker.Kind, SteerPendingKinds.UiIterationReview, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(marker.Kind, SteerPendingKinds.ConceptSightReview, StringComparison.OrdinalIgnoreCase))
                    continue;
                candidates.Add(new Candidate(laneFolder.Slug, laneFolder.FolderPath, marker));
            }

            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var timeoutSeconds = c.Marker.TimeoutSeconds > 0 ? c.Marker.TimeoutSeconds : configTimeout;
                var secondsWaiting = (now - c.Marker.WaitStartedAt).TotalSeconds;

                // Trigger signal only: a card inside its bounded wait keeps its
                // "waiting for answer" pill and is not asked about, so the
                // (git-touching) resolver runs only once the wait timed out.
                if (secondsWaiting < timeoutSeconds)
                    continue;
                var resolved = ResolveSafe(entry, c);
                var jobId = TryReadJobId(c.JobFolder) ?? c.Slug;
                var payload = new AiOperatorDecisionPayload(Decider,
                    $"steer:{jobId}:{c.Marker.WaitStartedAt:O}",
                    SteerTimeout: new SteerTimeoutFacts(
                        SecondsWaiting: secondsWaiting,
                        TimeoutSeconds: timeoutSeconds,
                        HasConfidentAutoAnswer: resolved.HasAnswer,
                        AutoAnswerText: resolved.AnswerText,
                        AmbiguityReason: resolved.AmbiguityReason));
                var submission = _decisions.Submit(new AiOperatorDecisionRequest(entry.Name, jobId,
                    TaskStates.Progress, payload, Period: null));
                if (submission.TestHostDecision is { } decision)
                {
                    var outcome = await ApplyDecisionAsync(entry, c, decision, secondsWaiting, timeoutSeconds, now, ct);
                    if (outcome is not null) outcomes.Add(outcome);
                }
            }
        }

        if (outcomes.Count > 0)
            _logger.LogInformation(
                "SteerTimeoutMonitor: resolved {Count} timed-out steer wait(s) ({Answered} auto-answered, {Blocked} blocked).",
                outcomes.Count,
                outcomes.Count(o => o.Kind == SteerTimeoutOutcomeKinds.AutoAnswered),
                outcomes.Count(o => o.Kind == SteerTimeoutOutcomeKinds.Blocked));

        return outcomes;
    }

    /// <summary>
    /// AGT-3060: applies the engine's steer-timeout receipt. A card that left
    /// 3-progress or whose wait was answered meanwhile (marker gone or newer)
    /// is settled without an effect.
    /// </summary>
    public async Task ApplyAsync(AiOperatorApplication application, CancellationToken ct)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, application.ProjectId, StringComparison.Ordinal));
        var job = entry is null ? null : _scanner.FindJob(application.TaskId, entry.Path);
        if (entry is null || job is null || job.State != TaskStates.Progress) return;
        var marker = SteerPendingMarker.TryRead(job.FolderPath, _logger);
        if (marker is null || application.Payload.SubjectKey
            != $"steer:{application.TaskId}:{marker.WaitStartedAt:O}")
            return;
        var facts = application.Payload.SteerTimeout
            ?? throw new InvalidDataException("The steer-timeout occurrence has no facts.");
        var outcome = await ApplyDecisionAsync(entry, new Candidate(Path.GetFileName(job.FolderPath),
                job.FolderPath, marker), application.Decision, facts.SecondsWaiting, facts.TimeoutSeconds,
            DateTime.UtcNow, ct);
        if (outcome?.Kind is SteerTimeoutOutcomeKinds.AutoAnswerFailed or SteerTimeoutOutcomeKinds.BlockFailed)
            throw new InvalidOperationException(outcome.Reason);
    }

    private async Task<SteerTimeoutOutcome?> ApplyDecisionAsync(WatchPathEntry entry, Candidate c,
        AiOperatorDecision decision, double secondsWaiting, double timeoutSeconds, DateTime now,
        CancellationToken ct)
    {
        SteerTimeoutOutcome outcome;
        switch (decision.Effect)
        {
            case AiOperatorEffects.AutoAnswer:
                outcome = await AutoAnswerAsync(entry, c, new SteerTimeoutDecision(SteerTimeoutAction.AutoAnswer,
                    decision.ReasonCode, decision.AnswerText, decision.Detail), secondsWaiting, timeoutSeconds, now, ct);
                break;
            case AiOperatorEffects.Escalate:
                outcome = await RouteBlockedAsync(entry, c, new SteerTimeoutDecision(SteerTimeoutAction.RouteBlocked,
                    decision.ReasonCode, null, decision.Detail), secondsWaiting, timeoutSeconds, now, ct);
                break;
            default:
                // Still waiting: no move, no audit noise.
                return null;
        }
        AppendAudit(outcome);
        return outcome;
    }

    /// <summary>
    /// Fail-safe for a torn marker write. The lifecycle phase is persisted by a
    /// separate write and is what makes the card visibly say "waiting for
    /// answer". Treat that phase as authoritative evidence of a bounded wait,
    /// using its entry timestamp as the original clock. Otherwise a missing
    /// sidecar can turn a visibly tracked wait into an infinite one (AGT-2087).
    /// </summary>
    private SteerPendingRecord? RecoverMarkerFromPhase(string slug, string folder, string watchPath)
    {
        TaskInfo? task;
        try { task = _scanner.FindJob(slug, watchPath); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SteerTimeoutMonitor: could not inspect markerless phase for {Slug}", slug);
            return null;
        }
        if (task == null || !string.Equals(task.Phase, LifecyclePhases.SteerPending, StringComparison.OrdinalIgnoreCase))
            return null;

        var taskJson = Path.Combine(folder, "task.json");
        var fileStamp = File.Exists(taskJson) ? File.GetLastWriteTimeUtc(taskJson) : DateTime.UtcNow;
        var waitStartedAt = task.PhaseEnteredAt ?? (task.LastActivity == default ? fileStamp : task.LastActivity);
        var recovered = new SteerPendingRecord
        {
            WaitStartedAt = waitStartedAt.ToUniversalTime(),
            Kind = SteerPendingKinds.NeedsInput,
            Question = "Steer-pending phase persisted without its durable question marker.",
            CliType = task.CliType,
        };
        SteerPendingMarker.Write(folder, recovered, _logger);
        _logger.LogWarning(
            "SteerTimeoutMonitor: recovered missing steer marker for {Project}/{Slug} from phase entered at {WaitStartedAt:o}",
            task.ProjectName, slug, recovered.WaitStartedAt);
        return recovered;
    }

    private SteerResolveResult ResolveSafe(WatchPathEntry entry, Candidate c)
    {
        try
        {
            var settings = _projectSettings.Get(entry.Name);
            var ctx = new SteerResolveContext(
                Project: entry.Name,
                JobId: TryReadJobId(c.JobFolder) ?? c.Slug,
                JobFolder: c.JobFolder,
                WatchPath: entry.Path,
                // Prefer the orchestrator's concrete STEER Need over the raw
                // agent sentinel summary. The named 2067 question lives in Ask;
                // Question remains the fallback for decline/circuit-break paths.
                Question: !string.IsNullOrWhiteSpace(c.Marker.Ask) ? c.Marker.Ask : c.Marker.Question,
                RepoRoot: string.IsNullOrWhiteSpace(entry.RootPath) ? null : entry.RootPath,
                TaskBranch: WorktreeTaskLifecycle.BranchFor(TryReadJobId(c.JobFolder) ?? c.Slug),
                ConfiguredIntegrationBranch: settings.IntegrationBranch);
            return _resolver.Resolve(ctx);
        }
        catch (Exception ex)
        {
            // Fail-safe: any resolver failure is ambiguous -> blocked escalation,
            // never a false auto-answer.
            _logger.LogDebug(ex, "SteerTimeoutMonitor: resolver threw for {Project}/{Slug}", entry.Name, c.Slug);
            return SteerResolveResult.Ambiguous($"resolver failed ({ex.Message})");
        }
    }

    private async Task<SteerTimeoutOutcome> AutoAnswerAsync(
        WatchPathEntry entry, Candidate c, SteerTimeoutDecision decision,
        double secondsWaiting, double timeoutSeconds, DateTime now, CancellationToken ct)
    {
        var jobId = TryReadJobId(c.JobFolder) ?? c.Slug;
        var answer = decision.AnswerText ?? string.Empty;

        // Queue the answer as a Continue and hand the card back to auto-pickup:
        // save the pending intent, clear the steer marker + phase, then demote to
        // 2-ready so the runner resumes the recorded session with the answer -
        // the same path a user follow-up to a busy project takes.
        var intent = _mutations.SavePendingIntent(
            jobId, ContinueModes.Continue, answer,
            reason: "steer-timeout-auto-answer", activeJobId: null, watchPath: entry.Path);
        if (intent == null)
            return Outcome(SteerTimeoutOutcomeKinds.AutoAnswerFailed, entry, c, decision, jobId,
                target: null, secondsWaiting, timeoutSeconds,
                reason: "could not save the auto-answer pending intent (job not found)", now);

        var moveOutcome = await _transitions.MoveAsync(
            jobId,
            TaskStates.Ready,
            entry.Path,
            ct,
            cause: "steer-timeout-detector",
            suppressProductExecution: true,
            // The run paused on a question and is handed back to the queue to
            // resume with the auto-answer: a system requeue, not a quality verdict.
            transitionCause: LaneChangeCauses.RunnerRequeue,
            transitionDetail: "steer-timeout-auto-answer");
        if (moveOutcome.Status != MoveJobStatus.Success)
        {
            // Keep the marker + phase on a failed move. The next sweep retries,
            // so a transient transition failure cannot turn this back into an
            // untracked, indefinitely waiting 3-progress card.
            return Outcome(SteerTimeoutOutcomeKinds.AutoAnswerFailed, entry, c, decision, jobId,
                target: TaskStates.Ready, secondsWaiting, timeoutSeconds,
                reason: $"auto-answer saved but demote to {TaskStates.Ready} refused: {moveOutcome.Status} {moveOutcome.Message}", now);
        }

        var moved = _scanner.FindJob(jobId, entry.Path);
        var movedFolder = moved?.FolderPath ?? moveOutcome.NewFolderPath ?? c.JobFolder;
        if (string.Equals(moved?.State, TaskStates.AutoReview, StringComparison.Ordinal))
        {
            // The answer was staged before the atomic lane move so a genuine
            // Ready transition cannot race pickup. BP-09 recovery proves there
            // will be no continuation, so consume that staging file without
            // disturbing the post-processing phase established by the move.
            _mutations.DiscardPendingIntent(movedFolder);
            SteerPendingMarker.Clear(movedFolder, _logger);
            _logger.LogWarning(
                "SteerTimeoutMonitor: recovered settled run {JobId} -> 4-auto-review; discarded the staged auto-answer and queued no replacement run.",
                jobId);
            return Outcome(
                SteerTimeoutOutcomeKinds.SettledRunRecovered,
                entry,
                c,
                decision,
                jobId,
                target: TaskStates.AutoReview,
                secondsWaiting,
                timeoutSeconds,
                reason: "attempt authority reported a completed immutable result; recovered the existing delivery to auto-review instead of requeueing",
                now,
                reasonCode: "settled-run-authority");
        }

        SteerPendingMarker.Clear(movedFolder, _logger);
        _mutations.SetJobPhase(movedFolder, null); // steer-pending phase is illegal in 2-ready

        _chatLog.Append(_scanner.FindJob(jobId, entry.Path) ?? FallbackInfo(jobId, movedFolder, entry.Name),
            OrchestratorMessageKind.Decision,
            $"[steer-timeout] No answer after {secondsWaiting:F0}s (> {timeoutSeconds:F0}s). Auto-answered from the task context and resumed the run: {Truncate(answer, 200)}");

        EmitResolvedTimeline(movedFolder, jobId,
            SteerTimeoutOutcomeKinds.AutoAnswered, decision, secondsWaiting, timeoutSeconds,
            c.Marker.Ask ?? c.Marker.Question, answer);

        _logger.LogWarning(
            "SteerTimeoutMonitor: auto-answered steer for {JobId} after {Silence:F0}s and demoted 3-progress -> 2-ready to resume.",
            jobId, secondsWaiting);
        return Outcome(SteerTimeoutOutcomeKinds.AutoAnswered, entry, c, decision, jobId,
            target: TaskStates.Ready, secondsWaiting, timeoutSeconds, reason: decision.Detail, now);
    }

    private async Task<SteerTimeoutOutcome> RouteBlockedAsync(
        WatchPathEntry entry, Candidate c, SteerTimeoutDecision decision,
        double secondsWaiting, double timeoutSeconds, DateTime now, CancellationToken ct)
    {
        var jobId = TryReadJobId(c.JobFolder) ?? c.Slug;
        var reason =
            $"Steer question unanswered for {secondsWaiting:F0}s (> {timeoutSeconds:F0}s timeout) and not derivable from the task context. " +
            $"Question: {Truncate(c.Marker.Ask ?? c.Marker.Question ?? "(none)", 200)}. " +
            (string.IsNullOrWhiteSpace(decision.Detail) ? "" : $"({decision.Detail})");

        try
        {
            var move = await _escalation.EscalateAsync(
                jobId, entry.Path, entry.Name,
                HumanReviewEscalationCategories.SteerUnanswered, reason, ct);
            if (move.Status != MoveJobStatus.Success)
                return Outcome(SteerTimeoutOutcomeKinds.BlockFailed, entry, c, decision, jobId,
                    target: TaskStates.Escalated, secondsWaiting, timeoutSeconds,
                    reason: $"escalation to {TaskStates.Escalated} refused: {move.Status} {move.Message}", now);

            var movedFolder = move.NewFolderPath ?? c.JobFolder;
            SteerPendingMarker.Clear(movedFolder, _logger);
            _mutations.SetJobPhase(movedFolder, null);

            _chatLog.Append(_scanner.FindJob(jobId, entry.Path) ?? FallbackInfo(jobId, movedFolder, entry.Name),
                OrchestratorMessageKind.GiveUp,
                $"[steer-timeout] No answer after {secondsWaiting:F0}s (> {timeoutSeconds:F0}s) and the answer is not derivable from the task context. Escalating to human review instead of waiting.");

            EmitResolvedTimeline(movedFolder, jobId,
                SteerTimeoutOutcomeKinds.Blocked, decision, secondsWaiting, timeoutSeconds,
                c.Marker.Ask ?? c.Marker.Question, answerGiven: null);

            _logger.LogWarning(
                "SteerTimeoutMonitor: steer for {JobId} unanswered {Silence:F0}s past timeout; escalated 3-progress -> 5e-escalated (steer-unanswered).",
                jobId, secondsWaiting);
            return Outcome(SteerTimeoutOutcomeKinds.Blocked, entry, c, decision, jobId,
                target: TaskStates.Escalated, secondsWaiting, timeoutSeconds, reason: decision.Detail, now);
        }
        catch (Exception ex)
        {
            return Outcome(SteerTimeoutOutcomeKinds.BlockFailed, entry, c, decision, jobId,
                target: TaskStates.Escalated, secondsWaiting, timeoutSeconds, reason: $"exception: {ex.Message}", now);
        }
    }

    private void EmitResolvedTimeline(
        string folderPath, string jobId, string outcomeKind, SteerTimeoutDecision decision,
        double secondsWaiting, double timeoutSeconds, string? question, string? answerGiven)
    {
        var summary = outcomeKind == SteerTimeoutOutcomeKinds.AutoAnswered
            ? $"Steer timeout: auto-answered after {secondsWaiting:F0}s. {Truncate(answerGiven ?? "", 160)}"
            : $"Steer timeout: no answer after {secondsWaiting:F0}s; routed to blocked escalation.";
        var details = new Dictionary<string, string>
        {
            ["reasonCode"] = decision.ReasonCode,
            ["secondsWaiting"] = ((long)secondsWaiting).ToString(),
            ["timeoutSeconds"] = ((long)timeoutSeconds).ToString(),
            ["outcome"] = outcomeKind,
            ["reason"] = Truncate(decision.Detail, 500),
        };
        if (!string.IsNullOrWhiteSpace(question)) details["question"] = Truncate(question!, 500);
        if (!string.IsNullOrWhiteSpace(answerGiven)) details["answer"] = Truncate(answerGiven!, 500);
        _timeline?.Append(folderPath, TimelineEventKinds.SteerTimeoutResolved, TimelineActors.System, summary, details: details);
    }

    private RunnerStatus? SafeGetStatus()
    {
        try
        {
            return StatusProviderOverride != null
                ? StatusProviderOverride()
                : (_services.GetService(typeof(TaskRunnerService)) as TaskRunnerService)?.GetStatus();
        }
        catch (Exception ex)
        {
            // Status is used only for the live active-job guard. A persisted
            // steer marker stays bounded regardless of a later mode change.
            _logger.LogDebug(ex, "SteerTimeoutMonitor: could not read runner status; enforcing persisted steer timeouts without an active-job guard.");
            return null;
        }
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

    private static TaskInfo FallbackInfo(string jobId, string folder, string project) => new()
    {
        Id = jobId,
        FolderPath = folder,
        ProjectName = project,
        Title = jobId,
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }

    private static SteerTimeoutOutcome Outcome(
        string kind, WatchPathEntry entry, Candidate c, SteerTimeoutDecision decision,
        string jobId, string? target, double secondsWaiting, double timeoutSeconds, string reason, DateTime at,
        string? reasonCode = null)
        => new()
        {
            At = at,
            Kind = kind,
            ReasonCode = reasonCode ?? decision.ReasonCode,
            ProjectName = entry.Name,
            Slug = c.Slug,
            JobId = jobId,
            TargetState = target,
            SecondsWaiting = (long)secondsWaiting,
            TimeoutSeconds = (long)timeoutSeconds,
            Reason = reason,
        };

    private void AppendAudit(SteerTimeoutOutcome outcome)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return;
        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "steer-timeout.jsonl");
            _appender.AppendAsync(path, outcome, JsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SteerTimeoutMonitor: failed to append steer-timeout.jsonl");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly record struct Candidate(string Slug, string JobFolder, SteerPendingRecord Marker);
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/steer-timeout.jsonl</c>.</summary>
public sealed record SteerTimeoutOutcome
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("targetState")] public string? TargetState { get; init; }
    [JsonPropertyName("secondsWaiting")] public long SecondsWaiting { get; init; }
    [JsonPropertyName("timeoutSeconds")] public long TimeoutSeconds { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="SteerTimeoutOutcome.Kind"/>.</summary>
public static class SteerTimeoutOutcomeKinds
{
    /// <summary>Timed out; auto-answered from the task context and resumed the run.</summary>
    public const string AutoAnswered = "auto-answered";
    /// <summary>The auto-answer save or resume move refused/threw.</summary>
    public const string AutoAnswerFailed = "auto-answer-failed";
    /// <summary>Attempt authority found a completed immutable result, so no auto-answer continuation was queued.</summary>
    public const string SettledRunRecovered = "settled-run-recovered";
    /// <summary>Timed out; no confident answer, escalated to 5e-escalated.</summary>
    public const string Blocked = "blocked";
    /// <summary>The escalation move refused or threw.</summary>
    public const string BlockFailed = "block-failed";
}
