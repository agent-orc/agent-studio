using System.Collections.Concurrent;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Cli;

/// <summary>Input shared by coding, review, pipeline, and chat admission.</summary>
public sealed record QuotaAdmissionRequest(
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    string? ProjectName,
    int OccupiedSlots,
    QuotaExpectedCostClass ExpectedCost,
    string ExecutionPath);

/// <summary>
/// Single application boundary around the pure planner. Every caller reads the
/// same cached quota state, cap policy, catalogue route, and project wait
/// policy. Admission never waits for a quota probe. Stale snapshots and elapsed
/// reset boundaries schedule one coalesced background refresh.
/// </summary>
public sealed class QuotaAdmissionService
{
    private readonly QuotaService _quota;
    private readonly CliQuotaCapsService _caps;
    private readonly CliQuotaFallbackService _fallback;
    private readonly CliQuotaWaitPolicyService _waitPolicy;
    private readonly ProjectSettingsService _settings;

    public QuotaAdmissionService(
        QuotaService quota,
        CliQuotaCapsService caps,
        CliQuotaFallbackService fallback,
        CliQuotaWaitPolicyService waitPolicy,
        ProjectSettingsService settings)
    {
        _quota = quota;
        _caps = caps;
        _fallback = fallback;
        _waitPolicy = waitPolicy;
        _settings = settings;
    }

    public QuotaAdmissionPlan Plan(QuotaAdmissionRequest request)
    {
        var now = DateTime.UtcNow;
        var project = string.IsNullOrWhiteSpace(request.ProjectName)
            ? null
            : _settings.Get(request.ProjectName);
        QuotaSnapshot? SnapshotFor(string? cliType)
        {
            if (string.IsNullOrWhiteSpace(cliType)) return null;
            var normalized = cliType.Trim().ToLowerInvariant();
            var snapshot = _quota.GetCachedFor(normalized);
            if (NeedsRefresh(snapshot, now, _quota.Ttl))
                _quota.QueueBackgroundRefresh(normalized);
            return snapshot;
        }

        var primaryCli = string.IsNullOrWhiteSpace(request.CliType)
            ? CliTypes.Claude
            : request.CliType.Trim().ToLowerInvariant();
        var primarySnapshot = SnapshotFor(primaryCli);
        var elapsedBlockingReset = ElapsedBlockingReset(primarySnapshot, _caps, now);
        if (elapsedBlockingReset is { } resetAt)
        {
            var refreshing = new QuotaAdmissionPlan(
                QuotaAdmissionOutcome.Wait,
                primaryCli,
                request.Model,
                request.ThinkingLevel,
                IsFallback: false,
                Reason: $"waiting for fresh {primaryCli} quota after reset {resetAt:HH:mm} UTC",
                NextResetAt: resetAt,
                Projection: QuotaWindowProjection.WorstProjection(primarySnapshot, _caps, now),
                ProjectionWarning: QuotaWindowProjection.FindWarning(primarySnapshot, now),
                ExpectedCost: request.ExpectedCost,
                ExecutionPath: request.ExecutionPath,
                RequestedCliType: primaryCli,
                RequestedModel: request.Model,
                RequestedThinkingLevel: request.ThinkingLevel);
            _fallback.ObserveAdmission(request.CliType, refreshing, now);
            return refreshing;
        }

        var plan = QuotaAdmissionPlanner.Plan(
            request.CliType,
            request.Model,
            request.ThinkingLevel,
            _fallback,
            _caps,
            SnapshotFor,
            now,
            request.OccupiedSlots,
            _waitPolicy.Resolve(project),
            request.ExpectedCost,
            request.ExecutionPath);
        _fallback.ObserveAdmission(request.CliType, plan, now);
        return plan;
    }

    internal static bool NeedsRefresh(QuotaSnapshot? snapshot, DateTime nowUtc, TimeSpan ttl)
    {
        if (snapshot is null) return true;
        var observedAt = snapshot.ProbeFailedAt ?? snapshot.FetchedAt;
        if (observedAt == default || nowUtc - observedAt > ttl) return true;
        return snapshot.Windows.Any(window => window.ResetAt is { } resetAt && resetAt <= nowUtc);
    }

    internal static DateTime? ElapsedBlockingReset(
        QuotaSnapshot? snapshot,
        CliQuotaCapsService caps,
        DateTime nowUtc)
    {
        var blocked = caps.Evaluate(snapshot);
        if (!blocked.Blocked || string.IsNullOrWhiteSpace(blocked.WindowLabel)) return null;
        return snapshot?.Windows
            .Where(window =>
                string.Equals(window.Label, blocked.WindowLabel, StringComparison.OrdinalIgnoreCase)
                && window.ResetAt is { } resetAt
                && resetAt <= nowUtc)
            .Select(window => window.ResetAt)
            .OrderByDescending(resetAt => resetAt)
            .FirstOrDefault();
    }

    /// <summary>Current standard-cost routing state for the Studio panel.</summary>
    public IReadOnlyList<ActiveQuotaFallback> RefreshActiveFallbacks()
    {
        foreach (var (cli, profile) in _fallback.GetAll())
        {
            Plan(new QuotaAdmissionRequest(
                cli,
                profile.PrimaryModel,
                profile.PrimaryThinkingLevel,
                ProjectName: null,
                OccupiedSlots: 0,
                ExpectedCost: QuotaExpectedCostClass.Standard,
                ExecutionPath: "quota-panel-preview"));
        }
        return _fallback.GetActiveFallbacks();
    }

    public ActiveQuotaFallback? ActiveFor(string? primaryCliType)
        => _fallback.GetActiveFallbacks().FirstOrDefault(active =>
            string.Equals(active.PrimaryCliType, primaryCliType, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Shared, deduplicated audit writer for quota admission decisions.</summary>
public sealed class QuotaAdmissionRecorder
{
    private readonly TimelineLog _timeline;
    private readonly OrchestratorChatLog _chat;
    private readonly OrchestratorLog _orchestrator;
    private readonly ILogger<QuotaAdmissionRecorder> _logger;
    private readonly ConcurrentDictionary<string, string> _lastDecision = new(StringComparer.OrdinalIgnoreCase);

    public QuotaAdmissionRecorder(
        TimelineLog timeline,
        OrchestratorChatLog chat,
        OrchestratorLog orchestrator,
        ILogger<QuotaAdmissionRecorder> logger)
    {
        _timeline = timeline;
        _chat = chat;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public void Record(
        TaskInfo task,
        QuotaAdmissionPlan plan,
        bool committedLaunch = false,
        string? attemptId = null,
        DateTime? startedAt = null)
    {
        var projection = plan.Projection;
        var warning = plan.ProjectionWarning;
        _logger.Log(
            warning is null ? LogLevel.Information : LogLevel.Warning,
            "cli_quota_admission_decision jobId={JobId} project={Project} path={Path} outcome={Outcome} cli={Cli} model={Model} thinking={Thinking} cost={Cost} isFallback={IsFallback} projectedPct={Projected} burnPctPerHour={Burn} resetAt={ResetAt} reason={Reason}",
            task.Id,
            task.ProjectName,
            plan.ExecutionPath,
            plan.Outcome,
            plan.CliType,
            plan.Model ?? "<default>",
            plan.ThinkingLevel ?? "<default>",
            plan.ExpectedCost,
            plan.IsFallback,
            projection?.ProjectedUsedPct ?? warning?.ProjectedUsedPct,
            projection?.BurnRatePctPerHour,
            projection?.ResetAt ?? warning?.ResetAt ?? plan.NextResetAt,
            plan.Reason);

        if (committedLaunch)
        {
            QuotaWaitMarker.Clear(task.FolderPath, _logger);
            if (plan.IsFallback)
            {
                QuotaFallbackMarker.Write(
                    task.FolderPath,
                    QuotaFallbackMarker.FromPlan(plan, attemptId, startedAt ?? DateTime.UtcNow),
                    _logger);
            }
            else
            {
                QuotaFallbackMarker.Clear(task.FolderPath, _logger);
            }
        }

        if (plan.Outcome == QuotaAdmissionOutcome.LaunchPrimary && warning is null) return;

        var key = $"{plan.Outcome}|{plan.CliType}|{plan.Model}|{plan.ThinkingLevel}|{plan.Reason}|{committedLaunch}";
        var taskKey = $"{task.ProjectName}|{task.Id}";
        if (_lastDecision.TryGetValue(taskKey, out var previous) && previous == key) return;
        _lastDecision[taskKey] = key;

        _chat.Append(task, OrchestratorMessageKind.Decision, "[quota-admission] " + plan.Reason);
        _timeline.Append(
            task.FolderPath,
            TimelineEventKinds.QuotaAdmissionDecision,
            TimelineActors.System,
            plan.Reason,
            runId: attemptId,
            details: new Dictionary<string, string>
            {
                ["outcome"] = plan.Outcome.ToString(),
                ["executionPath"] = plan.ExecutionPath ?? string.Empty,
                ["primaryCli"] = plan.RequestedCliType ?? string.Empty,
                ["primaryModel"] = plan.RequestedModel ?? string.Empty,
                ["cli"] = plan.CliType,
                ["model"] = plan.Model ?? string.Empty,
                ["thinkingLevel"] = plan.ThinkingLevel ?? string.Empty,
                ["expectedCost"] = plan.ExpectedCost.ToString(),
                ["isFallback"] = plan.IsFallback ? "true" : "false",
                ["nextReset"] = plan.NextResetAt?.ToString("o") ?? string.Empty,
                ["load"] = QuotaAdmissionPlanner.DescribeLoadNumbers(plan),
            });
        _orchestrator.Append(task.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            JobId = task.Id,
            Summary = plan.Reason,
            Reasoning = QuotaAdmissionPlanner.DescribeLoadNumbers(plan),
        });

        if (!committedLaunch || !plan.IsFallback) return;

        var fallbackNote =
            $"Switched to {plan.CliType}/{plan.Model ?? "<default>"} because {plan.Reason}";
        _chat.Append(task, OrchestratorMessageKind.Decision, "[quota-fallback] " + fallbackNote);
        _timeline.Append(
            task.FolderPath,
            TimelineEventKinds.QuotaFallbackActivated,
            TimelineActors.System,
            fallbackNote,
            runId: attemptId,
            details: new Dictionary<string, string>
            {
                ["primaryCli"] = plan.RequestedCliType ?? string.Empty,
                ["primaryModel"] = plan.RequestedModel ?? string.Empty,
                ["fallbackCli"] = plan.CliType,
                ["fallbackModel"] = plan.Model ?? string.Empty,
                ["fallbackThinkingLevel"] = plan.ThinkingLevel ?? string.Empty,
                ["reason"] = "quota",
                ["quotaDetail"] = plan.Reason,
                ["executionPath"] = plan.ExecutionPath ?? string.Empty,
            });
    }

    public void RecordProject(
        string watchPath,
        string? projectName,
        string source,
        QuotaAdmissionPlan plan)
    {
        if (plan.Outcome == QuotaAdmissionOutcome.LaunchPrimary && plan.ProjectionWarning is null) return;
        var key = $"project|{projectName}|{source}";
        var value = $"{plan.Outcome}|{plan.CliType}|{plan.Model}|{plan.Reason}";
        if (_lastDecision.TryGetValue(key, out var previous) && previous == value) return;
        _lastDecision[key] = value;
        _orchestrator.Append(watchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            Summary = plan.Reason,
            Reasoning = $"{source}; {QuotaAdmissionPlanner.DescribeLoadNumbers(plan)}",
        });
    }
}
