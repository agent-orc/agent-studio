using System.Text;
using System.Text.Json;

namespace AgentStudio.Bus;

// TaskPaths is internal in AgentStudio.Tasks; same assembly,
// so the bridge can reference it without exposing it publicly.

/// <summary>
/// Single bridge that mirrors existing structured signals (orchestrator chat
/// log, supervisor advisories and interventions, run lifecycle, user prompts,
/// orchestrator token usage) into <see cref="AgentMessageBusStore"/> as typed
/// <see cref="AgentMessage"/> records.
/// </summary>
/// <remarks>
/// <para>
/// V1 is a derived, append-only projection over the raw streams that already
/// exist (<c>cli-output.log</c>, <c>observations.jsonl</c>,
/// <c>interventions.jsonl</c>, <c>orchestrator.jsonl</c>). Those raw streams
/// remain canonical: every legacy reader keeps reading them unchanged. The
/// bus messages reference the originating raw line via an
/// <c>artifact:log-slice</c> when applicable so reviewers can drill from a
/// typed message back to the underlying evidence.
/// </para>
/// <para>
/// All bridge calls are best-effort. Workspace not configured? Append fails?
/// We log and move on - the bus is observability, not authority. The producers
/// are unaware of any bus failure and their canonical writes are unaffected.
/// </para>
/// </remarks>
public sealed class AgentMessageBusBridge
{
    public const string ParticipantRuntime    = "runtime:taskboard";
    public const string ParticipantUser       = "user";
    public const string ParticipantOrchestrator = "orchestrator";
    public const string ParticipantSystemReview = "system-review";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentMessageBusBridge> _logger;
    private readonly TimeProvider _time;
    private int _participantsSeeded;

    public AgentMessageBusBridge(
        AgentMessageBusStore store,
        IConfiguration config,
        ILogger<AgentMessageBusBridge> logger,
        TimeProvider? time = null)
    {
        _store = store;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Stable run-id derivation used across producers so messages emitted on
    /// behalf of one CLI invocation share the same <c>runId</c>. The runs
    /// endpoint surfaces runs by chronological index; the bridge keys off
    /// <c>(jobId, startedAtUtcTicks)</c> so it does not need to consult the
    /// timeline builder.
    /// </summary>
    public static string DeriveRunId(string jobId, DateTime startedAtUtc)
    {
        var ticks = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc).Ticks;
        return $"{jobId}:{ticks}";
    }

    public static string ParticipantForCli(string? cliType)
    {
        var slug = string.IsNullOrWhiteSpace(cliType) ? "unknown" : cliType.Trim().ToLowerInvariant();
        return $"agent:{slug}";
    }

    public static string ParticipantSupervisor(string project) => $"supervisor:{project}";
    public static string ParticipantOrchestratorFor(string project) => $"orchestrator:{project}";

    /// <summary>
    /// Returns the workspace root, or null when <c>TaskRepository</c> is not
    /// configured. The bus is workspace-scoped so without a root we cannot
    /// emit; callers see this as a no-op.
    /// </summary>
    private string? Workspace() => _config["TaskRepository"];

    private static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>One-time registration of the built-in participant set.</summary>
    public async Task SeedBuiltInParticipantsAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _participantsSeeded, 1, 0) != 0) return;
        var ws = Workspace();
        if (string.IsNullOrWhiteSpace(ws)) return;

        var participants = new[]
        {
            new AgentParticipant { Id = ParticipantUser, Kind = "User", DisplayName = "You" },
            new AgentParticipant { Id = ParticipantRuntime, Kind = "Runtime", DisplayName = "Taskboard runtime" },
            new AgentParticipant { Id = ParticipantOrchestrator, Kind = "Orchestrator", DisplayName = "Orchestrator" },
            new AgentParticipant { Id = "agent:claude", Kind = "CodingAgent", DisplayName = "Claude", Cli = "claude" },
            new AgentParticipant { Id = "agent:codex", Kind = "CodingAgent", DisplayName = "Codex", Cli = "codex" },
            new AgentParticipant { Id = "agent:gemini", Kind = "CodingAgent", DisplayName = "Gemini", Cli = "gemini" },
            // support:adhoc covers one-shot Haiku calls (TitleGen, SummaryGen,
            // PromptEnhance, CommitMessage, ReviewDecision, SoftReasoning).
            // AdHocUsageRecorder mirrors every JSONL record onto the bus so
            // token aggregation has a single source of truth.
            new AgentParticipant { Id = "support:adhoc", Kind = "SupportingAgent", DisplayName = "Ad-hoc Haiku call", Cli = "claude" },
        };
        foreach (var p in participants)
        {
            try { await _store.RegisterParticipantAsync(ws!, p, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus participant seed skipped for {Id}", p.Id); }
        }
    }

    /// <summary>
    /// Mirror an orchestrator chat-log line. Mapping:
    /// <c>Decision -&gt; decision/Info</c>, <c>Reissue -&gt; decision/Warn</c>,
    /// <c>HeuristicFallback -&gt; decision/Warn</c>, <c>GiveUp -&gt; decision/High</c>.
    /// </summary>
    public Task EmitOrchestratorChatAsync(TaskInfo info, OrchestratorMessageKind kind, string text, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var severity = kind switch
        {
            OrchestratorMessageKind.GiveUp            => "High",
            OrchestratorMessageKind.PermissionBlocked => "High",
            OrchestratorMessageKind.WatchdogTimeout   => "High",
            OrchestratorMessageKind.EmptyFastExit     => "High",
            OrchestratorMessageKind.InfraCrash        => "High",
            OrchestratorMessageKind.AuthRefreshFailed => "High",
            OrchestratorMessageKind.IntegrationConflict => "High",
            OrchestratorMessageKind.IntegrationError  => "High",
            OrchestratorMessageKind.Reissue           => "Warn",
            OrchestratorMessageKind.HeuristicFallback => "Warn",
            OrchestratorMessageKind.SoftIntervention  => "Warn",
            OrchestratorMessageKind.MissingTerminalSentinel => "Warn",
            OrchestratorMessageKind.HeuristicDone     => "Warn",
            OrchestratorMessageKind.OrchestratorInconclusive => "Warn",
            OrchestratorMessageKind.CliLaunchFailed   => "Warn",
            OrchestratorMessageKind.Steer             => "Warn",
            OrchestratorMessageKind.TaskBranchUnpushed => "Warn",
            _                                         => "Info"
        };
        var topic = kind.ToBusTopic();

        var msg = NewMessage(
            participantId: ParticipantOrchestratorFor(info.ProjectName),
            role: "actor",
            kind: "decision",
            severity: severity,
            project: info.ProjectName,
            jobId: info.Id,
            topic: topic,
            summary: TruncateSummary(text),
            body: text,
            artifacts: new[] { LogSliceArtifact(info) },
            tags: new[] { "orchestrator-chat", topic });

        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a supervisor chat-log line (the <c>[supervisor]</c>-stream lines
    /// the chat log writes for user-visible interventions like <c>cancel-run</c>,
    /// <c>force-fail</c>, <c>chat-note</c>, <c>escalate</c>,
    /// <c>cycle-resume-failed</c>). One bus message per line.
    /// </summary>
    public Task EmitSupervisorChatAsync(TaskInfo info, string tag, string text, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var msg = NewMessage(
            participantId: ParticipantSupervisor(info.ProjectName),
            role: "actor",
            kind: tag is "cancel-run" or "force-fail" ? "intervention" : "advisory",
            severity: tag is "cycle-resume-failed" or "escalate" ? "High" : "Info",
            project: info.ProjectName,
            jobId: info.Id,
            topic: tag,
            summary: TruncateSummary($"[{tag}] {text}"),
            body: text,
            artifacts: new[] { LogSliceArtifact(info) },
            tags: new[] { "supervisor-chat", tag });

        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a typed <see cref="SupervisorAdvisory"/> from the hard-health and
    /// soft-reasoning writers. The legacy <c>observations.jsonl</c> record stays
    /// canonical; the bus message references it via an
    /// <c>artifact:supervisor-advisory</c> pointer so a reviewer can pivot from
    /// the timeline back to the originating record.
    /// </summary>
    public Task EmitAdvisoryAsync(SupervisorAdvisory advisory, CancellationToken ct = default)
    {
        if (advisory == null) return Task.CompletedTask;
        var severity = advisory.Severity switch
        {
            SupervisorSeverity.High => "High",
            SupervisorSeverity.Warn => "Warn",
            _                       => "Info"
        };
        var msg = NewMessage(
            participantId: ParticipantSupervisor(advisory.Project),
            role: "evidence",
            kind: "advisory",
            severity: severity,
            project: advisory.Project,
            jobId: advisory.JobId,
            topic: advisory.Topic,
            summary: TruncateSummary(advisory.Message),
            body: advisory.Message,
            createdAt: advisory.CreatedAt,
            artifacts: new[] { SupervisorAdvisoryArtifact(advisory) },
            payload: new { source = advisory.Source.ToString(), advisory.Topic },
            tags: new[] { "supervisor-advisory", advisory.Source.ToString().ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a <see cref="SupervisorIntervention"/>. The intervention only
    /// records intent + reason; the actual side effect was applied by the
    /// runner via <see cref="AgentStudio.Runner.TaskRunnerService.StopJob"/>
    /// or <c>SetMode</c>. We tag the message accordingly so the timeline shows
    /// what was triggered and by whom.
    /// </summary>
    public Task EmitInterventionAsync(SupervisorIntervention intervention, CancellationToken ct = default)
    {
        if (intervention == null) return Task.CompletedTask;
        var severity = intervention.Kind switch
        {
            SupervisorInterventionKind.ForceFail   => "High",
            SupervisorInterventionKind.CancelRun   => "Warn",
            SupervisorInterventionKind.PausePickup => "Warn",
            _                                      => "Info"
        };
        var msg = NewMessage(
            participantId: ParticipantSupervisor(intervention.Project),
            role: "actor",
            kind: "intervention",
            severity: severity,
            project: intervention.Project,
            jobId: intervention.JobId,
            topic: intervention.Kind.ToString(),
            summary: TruncateSummary($"{intervention.Kind}: {intervention.Reason}"),
            body: intervention.Reason,
            createdAt: intervention.CreatedAt,
            artifacts: new[] { SupervisorInterventionArtifact(intervention) },
            payload: new
            {
                source = intervention.Source.ToString(),
                kind = intervention.Kind.ToString(),
                pauseTtlSeconds = intervention.PauseTtl?.TotalSeconds,
            },
            tags: new[] { "supervisor-intervention", intervention.Kind.ToString().ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Mirror a user prompt or follow-up that the runtime persisted to the
    /// CLI output log. Recorded as <c>kind:question</c> from
    /// <see cref="ParticipantUser"/>; this is the message every later orchestrator
    /// or agent reply chains back to via <c>correlationId</c>.
    /// </summary>
    public Task EmitUserPromptAsync(TaskInfo info, string prompt, string mode, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var trimmed = (prompt ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        var msg = NewMessage(
            participantId: ParticipantUser,
            role: "actor",
            kind: "question",
            severity: "Info",
            project: info.ProjectName,
            jobId: info.Id,
            topic: "user-followup",
            summary: TruncateSummary(trimmed.Length == 0 ? "(empty follow-up)" : trimmed),
            body: prompt,
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { mode },
            tags: new[] { "user-prompt", $"mode:{mode}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Run lifecycle: a fresh CLI process started for the job.
    /// </summary>
    public Task EmitRunStartedAsync(TaskInfo info, string cliType, DateTime startedAtUtc, string? sessionId, string intent, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var runId = DeriveRunId(info.Id, startedAtUtc);
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: "Info",
            project: info.ProjectName,
            jobId: info.Id,
            runId: runId,
            cliSessionId: sessionId,
            topic: "RunStarted",
            summary: TruncateSummary($"Run started ({cliType}, intent={intent})"),
            createdAt: startedAtUtc,
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { cliType, intent },
            tags: new[] { "run-lifecycle", "run-started", $"cli:{cliType}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Run lifecycle: the CLI process finished. <paramref name="status"/> is
    /// the <c>CliExecution.Status</c> string (<c>completed</c>,
    /// <c>failed</c>, <c>cancelled</c>, ...).
    /// </summary>
    public Task EmitRunFinishedAsync(TaskInfo info, string cliType, DateTime startedAtUtc, string status, double? durationSeconds, string? agentOutcome, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var severity = status switch
        {
            "failed"    => "High",
            "cancelled" => "Warn",
            _           => "Info"
        };
        var runId = DeriveRunId(info.Id, startedAtUtc);
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: severity,
            project: info.ProjectName,
            jobId: info.Id,
            runId: runId,
            topic: "RunFinished",
            summary: TruncateSummary($"Run {status} ({cliType}{(agentOutcome != null ? $", outcome={agentOutcome}" : string.Empty)})"),
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { status, cliType, durationSeconds, agentOutcome },
            tags: new[] { "run-lifecycle", $"run-{status}", $"cli:{cliType}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Codex silent-completion observation. Emitted from the per-tick runner
    /// detector (<see cref="CodexSilentCompletionDetector"/>) when Codex
    /// stopped after a successful tool call but never sent a closing
    /// <c>turn.completed</c> or sentinel. Distinct kind / topic from
    /// <c>RunStopRequested</c> so pattern-spotting reviews can grep for
    /// silent-completion events directly. The body carries the last command
    /// + output tail so a reviewer can pivot from the bus message back to
    /// what Codex actually did just before going stale.
    /// </summary>
    public Task EmitCodexSilentCompletionAsync(
        TaskInfo info,
        string? lastCommand,
        string? lastOutputTail,
        double silenceSeconds,
        CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var summary = TruncateSummary(
            $"Codex silent-completion: stopped {silenceSeconds:F0}s after a successful tool call without a closing sentinel.");
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "observation",
            severity: "Warn",
            project: info.ProjectName,
            jobId: info.Id,
            topic: "codex-silent-completion",
            summary: summary,
            body: $"last command: {lastCommand ?? "<unknown>"}\noutput tail:\n{lastOutputTail ?? "(empty)"}",
            artifacts: new[] { LogSliceArtifact(info) },
            payload: new { silenceSeconds, lastCommand, lastOutputTail },
            tags: new[] { "codex-silent-completion", "observation" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Run lifecycle: a stop was requested by the user, the watchdog, or an
    /// auto-mode policy. The actual termination still flows through
    /// <c>RunFinished</c>; this records the trigger.
    /// </summary>
    public Task EmitRunStopRequestedAsync(TaskInfo info, RunStopReason reason, string source, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: "Warn",
            project: info.ProjectName,
            jobId: info.Id,
            topic: "RunStopRequested",
            summary: TruncateSummary($"Stop requested ({reason}) by {source}"),
            payload: new { reason = reason.ToString(), source },
            tags: new[] { "run-lifecycle", "run-stop-requested", $"reason:{reason}".ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Job-folder state lane transition (e.g. <c>3-progress -&gt; 4-review</c>,
    /// <c>1-preparation -&gt; 2-ready</c>). One <c>kind:lifecycle</c> message
    /// per actual transition the runtime applies.
    /// </summary>
    public Task EmitJobLifecycleAsync(TaskInfo info, string topic, string? fromState, string? toState, string? reason, CancellationToken ct = default)
    {
        if (info == null) return Task.CompletedTask;
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "lifecycle",
            severity: "Info",
            project: info.ProjectName,
            jobId: info.Id,
            topic: topic,
            summary: TruncateSummary($"{topic}: {fromState ?? "?"} -> {toState ?? "?"}{(reason is null ? string.Empty : $" ({reason})")}"),
            payload: new { fromState, toState, reason },
            tags: new[] { "job-lifecycle", topic.ToLowerInvariant() });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Emits the final failure of a platform-owned repository push. Producers
    /// call this only after their retry budget is exhausted so the operator
    /// feed stays useful instead of receiving one message per retry attempt.
    /// </summary>
    public Task EmitManagedRepoPushFailureAsync(
        string? project,
        string? jobId,
        string repository,
        string branch,
        string status,
        string? error,
        int attempts,
        CancellationToken ct = default)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? "workspace" : project;
        var detail = string.IsNullOrWhiteSpace(error) ? status : $"{status}: {error}";
        var msg = NewMessage(
            participantId: string.IsNullOrWhiteSpace(project)
                ? ParticipantOrchestrator
                : ParticipantOrchestratorFor(project),
            role: "system",
            kind: "error",
            severity: "Warn",
            project: project,
            jobId: jobId,
            topic: "managed-repo-push-failed",
            summary: TruncateSummary($"Push failed for {scope}/{branch} after {attempts} attempt(s): {detail}"),
            body: $"Repository: {repository}\nBranch: {branch}\nStatus: {status}\nError: {error ?? "(none)"}",
            payload: new { repository, branch, status, error, attempts },
            tags: new[] { "managed-repo-push", "push-failed", $"branch:{branch}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Emits one terminal operator-feed event for a host-owned provider sign-in.
    /// Device codes and credentials are intentionally absent from this contract.
    /// </summary>
    public Task EmitProviderSignInAsync(
        string host,
        string provider,
        string actor,
        string outcome,
        CancellationToken ct = default)
    {
        var severity = outcome == "completed" ? "Info" : "Warn";
        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "system",
            kind: "action",
            severity: severity,
            project: null,
            jobId: null,
            topic: "provider_sign_in",
            summary: TruncateSummary($"{provider} sign-in on {host}: {outcome}"),
            payload: new { host, provider, actor, outcome },
            tags: new[] { "provider-sign-in", $"provider:{provider}", $"outcome:{outcome}" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Token-usage attribution for one orchestrator turn or supporting-agent
    /// call. The aggregate rollup view stays in <c>orchestrator.jsonl</c> /
    /// the token summary service; the bus carries one event per recorded
    /// usage so the timeline shows which turn was expensive.
    /// </summary>
    public Task EmitTokenUsageAsync(string? project, string? jobId, string participantId, string? topic, OrchestratorTokenUsage usage, DateTime? createdAt = null, CancellationToken ct = default)
    {
        if (usage == null) return Task.CompletedTask;
        var input  = (long)usage.InputTokens;
        var output = (long)usage.OutputTokens;
        var cacheRead   = (long)usage.CacheReadTokens;
        var cacheWrite  = (long)usage.CacheCreationTokens;

        var tokens = new AgentMessageTokens(
            Input: input,
            Output: output,
            CacheRead: cacheRead == 0 ? null : cacheRead,
            CacheWrite: cacheWrite == 0 ? null : cacheWrite,
            Model: usage.Model,
            Dollars: null);

        var msg = NewMessage(
            participantId: participantId,
            role: "evidence",
            kind: "token-usage",
            severity: "Info",
            project: project,
            jobId: jobId,
            topic: topic ?? "orchestrator-turn",
            summary: TruncateSummary($"tokens: in={input} out={output} model={usage.Model ?? "?"}"),
            createdAt: createdAt,
            tokens: tokens,
            tags: new[] { "token-usage" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Token-usage attribution with optional context-window snapshot and
    /// per-turn latency. Preferred over <see cref="EmitTokenUsageAsync(string, string?, string, string?, OrchestratorTokenUsage, CancellationToken)"/>
    /// when the runner has access to the parsed CLI frame (full
    /// <see cref="ParsedTurnUsage"/>) and timing markers.
    /// </summary>
    public Task EmitTokenUsageRichAsync(
        string project,
        string? jobId,
        string? runId,
        string participantId,
        string? topic,
        ParsedTurnUsage usage,
        AgentMessageLatency? latency = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        if (usage == null) return Task.CompletedTask;

        var tokens = usage.ToBusTokens();
        var pct = usage.ContextWindow?.TotalSize is { } total and > 0
            ? $" ctx={usage.ContextUsed * 100 / total}%"
            : string.Empty;
        var ttfb = latency?.TtfbMs is { } t ? $" ttfb={t}ms" : string.Empty;
        var summary = TruncateSummary(
            $"tokens: in={usage.Input} out={usage.Output} model={usage.Model ?? "?"}{pct}{ttfb}");

        var msg = NewMessage(
            participantId: participantId,
            role: "evidence",
            kind: "token-usage",
            severity: "Info",
            project: project,
            jobId: jobId,
            runId: runId,
            topic: topic ?? "orchestrator-turn",
            summary: summary,
            tokens: tokens,
            latency: latency,
            correlationId: correlationId,
            tags: new[] { "token-usage" });
        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// <summary>
    /// Quota-snapshot observation for one run boundary (AGT-2100). Emitted at
    /// run-start and run-end, this mirrors the currently CACHED quota snapshot
    /// for the CLI that ran - all windows, plus the snapshot's age so a reader
    /// can tell a fresh reading from a stale one. It never forces a fresh probe;
    /// the caller passes whatever <see cref="QuotaService.GetCachedFor"/> holds
    /// (null when nothing is cached). The compact payload is a
    /// <see cref="QuotaSnapshotEvent"/> so downstream cap-forecast tooling reads
    /// one line of stable-named JSON per event.
    /// </summary>
    public Task EmitQuotaSnapshotAsync(
        string? project,
        string? jobId,
        string? runId,
        string cliType,
        string? model,
        string? thinkingLevel,
        string phase,
        QuotaSnapshot? snapshot,
        TimeSpan ttl,
        DateTime? createdAt = null,
        CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var evt = QuotaSnapshotEventBuilder.Build(
            phase, cliType, model, thinkingLevel, snapshot, ttl, now, runId, jobId);

        var msg = NewMessage(
            participantId: ParticipantRuntime,
            role: "evidence",
            kind: "observation",
            severity: "Info",
            project: project,
            jobId: jobId,
            runId: runId,
            topic: "quota-snapshot",
            summary: QuotaSnapshotEventBuilder.Summarize(evt),
            createdAt: createdAt,
            payload: evt,
            tags: new[] { "quota-snapshot", $"phase:{evt.Phase}", $"cli:{cliType}" });
        return EmitAsync(msg, ct);
    }

    /// Free-form structured event emit. Awaitable, so tests and future
    /// producers that want backpressure can chain off the result. Production
    /// callers should discard the task (<c>_ = bridge.EmitAsync(...)</c>) so
    /// the canonical write path is never slowed by bus I/O.
    /// </summary>
    /// <remarks>
    /// Append failures are caught and logged - they never propagate. The bus
    /// is observability; a write failure must not break the producer.
    /// </remarks>
    public async Task EmitAsync(AgentMessage message, CancellationToken ct = default)
    {
        var ws = Workspace();
        if (string.IsNullOrWhiteSpace(ws))
        {
            _logger.LogDebug("Bus emit skipped: TaskRepository not configured ({Kind} {Topic})", message.Kind, message.Topic);
            return;
        }
        try { await _store.AppendAsync(ws!, message, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Bus append failed: kind={Kind} topic={Topic} job={Job}", message.Kind, message.Topic, message.JobId); }
    }

    private AgentMessage NewMessage(
        string participantId,
        string role,
        string kind,
        string? severity,
        string? project,
        string? jobId = null,
        string? runId = null,
        string? cliSessionId = null,
        string? topic = null,
        string? summary = null,
        string? body = null,
        DateTime? createdAt = null,
        object? payload = null,
        AgentMessageTokens? tokens = null,
        AgentMessageLatency? latency = null,
        IReadOnlyList<AgentArtifactRef>? artifacts = null,
        IReadOnlyList<string>? tags = null,
        string? correlationId = null,
        string? replyToId = null)
    {
        var ts = createdAt ?? _time.GetUtcNow().UtcDateTime;
        if (ts.Kind != DateTimeKind.Utc) ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        JsonElement? payloadElement = null;
        if (payload != null)
        {
            var json = JsonSerializer.SerializeToElement(payload, PayloadOptions);
            payloadElement = json;
        }
        return new AgentMessage
        {
            Id = NewId(),
            CreatedAt = ts,
            ParticipantId = participantId,
            Role = role,
            Kind = kind,
            Severity = severity,
            Project = project,
            JobId = jobId,
            RunId = runId,
            CliSessionId = cliSessionId,
            Topic = topic,
            Summary = summary,
            Body = body,
            Tokens = tokens,
            Latency = latency,
            Artifacts = artifacts,
            Payload = payloadElement,
            Tags = tags,
            CorrelationId = correlationId,
            ReplyToId = replyToId,
        };
    }

    private static string TruncateSummary(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= 280 ? s : s[..277] + "...";
    }

    private static AgentArtifactRef LogSliceArtifact(TaskInfo info)
    {
        return new AgentArtifactRef
        {
            Kind = "log-slice",
            Uri = TaskPaths.CliOutputLog(info.FolderPath),
            Label = "cli-output.log",
        };
    }

    private static AgentArtifactRef SupervisorAdvisoryArtifact(SupervisorAdvisory a)
    {
        return new AgentArtifactRef
        {
            Kind = "supervisor-advisory",
            Uri = $"observations.jsonl#{a.Project}",
            Label = a.Topic,
        };
    }

    private static AgentArtifactRef SupervisorInterventionArtifact(SupervisorIntervention i)
    {
        return new AgentArtifactRef
        {
            Kind = "supervisor-intervention",
            Uri = $"interventions.jsonl#{i.Project}",
            Label = i.Kind.ToString(),
        };
    }

    // ---------------------------------------------------------------------
    // Supporting-agent bridge: explicit user-triggered meta workers (roadmap
    // alignment, security audit, docs drift, UX/UI council, source-map skill,
    // architecture review, ...). Each report run produces one bus message
    // referencing the durable artifacts under
    // <c>logs/analysis/&lt;project&gt;/&lt;reportId&gt;.{md,json}</c>. The bus
    // never edits source code; supporting agents are observability + decision
    // records, not coding-agent extensions. See
    // <c>docs/system/architecture/bus/agent-message-bus.md</c> section "Supporting agents" and
    // <c>docs/system/reports/analysis-reports.md</c> section 11.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Stable participant id for a supporting-agent topic.
    /// Convention: <c>support:&lt;topic&gt;</c>, e.g.
    /// <c>support:roadmap-alignment</c>, <c>support:security-audit</c>.
    /// </summary>
    public static string ParticipantSupportingFor(string topic)
    {
        var slug = SafeKebab(topic);
        return $"support:{slug}";
    }

    /// <summary>
    /// Idempotently register a <c>SupportingAgent</c> participant in the
    /// workspace registry. Safe to call before every emit; the store
    /// overwrites on identical id.
    /// </summary>
    public async Task RegisterSupportingAgentAsync(
        string topic,
        string displayName,
        string? cli = null,
        string? skill = null,
        string? project = null,
        CancellationToken ct = default)
    {
        var ws = Workspace();
        if (string.IsNullOrWhiteSpace(ws)) return;
        var participant = new AgentParticipant
        {
            Id = ParticipantSupportingFor(topic),
            Kind = "SupportingAgent",
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? topic : displayName,
            Cli = cli,
            Skill = string.IsNullOrWhiteSpace(skill) ? topic : skill,
            Project = project,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        try { await _store.RegisterParticipantAsync(ws!, participant, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bus participant register skipped for {Id}", participant.Id); }
    }

    /// <summary>
    /// Emit one bus message that mirrors a supporting-agent
    /// <see cref="AnalysisReport"/> run (e.g. roadmap alignment review,
    /// security audit, docs drift). The Markdown sibling and the optional
    /// JSON sidecar remain canonical on disk under
    /// <c>logs/analysis/</c>; this projection lets the timeline see the run
    /// alongside coding-agent activity and lets a reviewer drill from the
    /// timeline back to the report files.
    /// </summary>
    /// <param name="project">Project slug. Workspace-scoped reports pass
    /// <c>null</c>.</param>
    /// <param name="topic">Analysis topic slug (<c>roadmap-alignment</c>,
    /// <c>docs-drift</c>, <c>security-audit</c>, ...). Drives the participant
    /// id and the topic tag.</param>
    /// <param name="reportId">Stable report id (ULID/UUID v7).</param>
    /// <param name="summary">One-line verdict for the timeline. Truncated to
    /// the bus 280-char limit.</param>
    /// <param name="severity">Analysis-report severity
    /// (<c>Info|Warn|High|Critical</c>). The bus envelope's
    /// <c>severity</c> is capped at <c>High</c>; the original analysis value
    /// is preserved verbatim under <c>payload.analysisSeverity</c> so the
    /// Critical badge survives the projection.</param>
    /// <param name="parseStatus">One of <c>Structured</c>,
    /// <c>Unstructured</c>, <c>MalformedJson</c>. Drives the
    /// <c>parse-*</c> tag and is mirrored on the payload so the UI can render
    /// the raw-fallback warning without re-reading the report.</param>
    /// <param name="markdownPath">Absolute path to the Markdown sibling. The
    /// bus stores the reference; no bytes are inlined.</param>
    /// <param name="jsonSidecarPath">Optional absolute path to the JSON
    /// sidecar when <paramref name="parseStatus"/> is <c>Structured</c>.</param>
    /// <param name="jobId">Optional job slug when the report scope is
    /// <c>Task</c> or <c>Run</c>.</param>
    /// <param name="cli">Optional CLI driver that produced the agent reply
    /// (claude / codex / gemini). Recorded on the participant
    /// registry, surfaced as a tag (<c>cli-&lt;name&gt;</c>) on the message.</param>
    /// <param name="skill">Optional skill or workflow id; surfaced as a tag
    /// (<c>skill-&lt;name&gt;</c>) so the UI can filter by reusable workflow.</param>
    /// <param name="participantId">Override participant id. Defaults to
    /// <c>support:&lt;topic&gt;</c>.</param>
    /// <param name="parseError">When <paramref name="parseStatus"/> is
    /// <c>MalformedJson</c>, the parser error text. Surfaced verbatim on the
    /// payload so the user-visible warning ("the agent reply did not parse")
    /// is one bus query away.</param>
    /// <param name="body">Optional longer-form body (the rendered Markdown
    /// verdict, an extracted finding list, etc.). Heavy evidence stays in the
    /// referenced artifacts.</param>
    /// <param name="tokens">Optional per-call token attribution for
    /// supporting-jobs token accounting. Kept distinct from job tokens
    /// (coding agent) and orchestrator tokens; the participant id
    /// (<c>support:*</c>) is the discriminator the rollup view groups on.</param>
    /// <param name="correlationId">Optional correlation id tying this
    /// message to the originating user request or report.</param>
    /// <param name="createdAt">Optional explicit creation time. Defaults to
    /// the injected <see cref="TimeProvider"/>.</param>
    public Task EmitSupportingAgentReportAsync(
        string? project,
        string topic,
        string reportId,
        string summary,
        string severity,
        string parseStatus,
        string markdownPath,
        string? jsonSidecarPath = null,
        string? jobId = null,
        string? cli = null,
        string? skill = null,
        string? participantId = null,
        string? parseError = null,
        string? body = null,
        AgentMessageTokens? tokens = null,
        string? correlationId = null,
        DateTime? createdAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(reportId))
            return Task.CompletedTask;

        var pid = string.IsNullOrWhiteSpace(participantId)
            ? ParticipantSupportingFor(topic)
            : participantId!;
        var topicSlug = SafeKebab(topic);
        var parseSlug = SafeKebab(parseStatus);
        var busSeverity = MapAnalysisSeverityToBusSeverity(severity);

        var artifacts = new List<AgentArtifactRef>();
        if (!string.IsNullOrWhiteSpace(markdownPath))
        {
            artifacts.Add(new AgentArtifactRef
            {
                Kind = "markdown-report",
                Uri = markdownPath,
                Label = $"{topicSlug}/{reportId}.md",
            });
        }
        if (!string.IsNullOrWhiteSpace(jsonSidecarPath))
        {
            artifacts.Add(new AgentArtifactRef
            {
                Kind = "json-document",
                Uri = jsonSidecarPath!,
                Label = $"{topicSlug}/{reportId}.json",
            });
        }

        var tags = new List<string> { "supporting-agent", topicSlug, $"parse-{parseSlug}" };
        if (!string.IsNullOrWhiteSpace(cli)) tags.Add($"cli-{SafeKebab(cli)}");
        if (!string.IsNullOrWhiteSpace(skill)) tags.Add($"skill-{SafeKebab(skill)}");

        // Structured + Unstructured carry a typed verdict, so they map to
        // kind:decision. MalformedJson is an inspection record without a
        // typed verdict the app can act on, so it lands as kind:observation
        // and the warning surfaces in the payload + parse-malformedjson tag.
        var kind = parseStatus.Equals("MalformedJson", StringComparison.OrdinalIgnoreCase)
            ? "observation"
            : "decision";

        var msg = NewMessage(
            participantId: pid,
            role: "evidence",
            kind: kind,
            severity: busSeverity,
            project: project,
            jobId: jobId,
            topic: topicSlug,
            summary: TruncateSummary(summary),
            body: body,
            createdAt: createdAt,
            artifacts: artifacts,
            tokens: tokens,
            correlationId: correlationId,
            payload: new
            {
                reportId,
                topic = topicSlug,
                parseStatus,
                analysisSeverity = severity,
                parseError,
                cli,
                skill,
            },
            tags: tags);

        return EmitAsync(msg, ct);
    }

    /// <summary>
    /// Map the analysis-report severity ladder (Info/Warn/High/Critical) onto
    /// the bus envelope ladder (Info/Warn/High). Critical collapses into
    /// High; the original value is preserved on the payload so the UI badge
    /// can still distinguish the two.
    /// </summary>
    private static string MapAnalysisSeverityToBusSeverity(string severity)
    {
        if (string.IsNullOrWhiteSpace(severity)) return "Info";
        return severity.Trim().ToLowerInvariant() switch
        {
            "critical" => "High",
            "high"     => "High",
            "warn"     => "Warn",
            _           => "Info",
        };
    }

    /// <summary>
    /// Lower-case kebab-case slug for tags. The agent-message schema enforces
    /// <c>^[a-z0-9][a-z0-9-]*$</c> on tag values, so we collapse anything
    /// outside <c>[a-z0-9-]</c> into single dashes and strip leading/trailing
    /// dashes.
    /// </summary>
    private static string SafeKebab(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (ch >= 'a' && ch <= 'z') sb.Append(ch);
            else if (ch >= '0' && ch <= '9') sb.Append(ch);
            else if (ch == '-') sb.Append('-');
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "unknown" : s;
    }
}
