using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Cli;

/// <summary>
/// Shared "make the decision visible" tee for admission paths that do not
/// live inside <see cref="AgentStudio.Runner.ProjectRunner"/> (remote claims,
/// review claims, task-scoped one-shots, and project chat). Writes the same
/// structured log line, task timeline
/// event, chat note, and load-distribution feed entry that
/// <c>ProjectRunner.EmitQuotaAdmissionDecision</c> / the
/// <c>quota_fallback_activated</c> block write for a local launch, so every
/// execution path documents its decision the same way (AGT-2751) even though
/// each path resolves the plan at a different point in its own code.
/// </summary>
public sealed class QuotaAdmissionRecorder
{
    private readonly TimelineLog _timeline;
    private readonly OrchestratorChatLog _chatLog;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly ILogger<QuotaAdmissionRecorder> _logger;
    private readonly ConcurrentDictionary<string, string> _lastDecisionByJob = new();

    public QuotaAdmissionRecorder(
        TimelineLog timeline,
        OrchestratorChatLog chatLog,
        OrchestratorLog orchestratorLog,
        ILogger<QuotaAdmissionRecorder> logger)
    {
        _timeline = timeline;
        _chatLog = chatLog;
        _orchestratorLog = orchestratorLog;
        _logger = logger;
    }

    /// <summary>
    /// Record a pre-launch/pre-claim admission decision for a job not driven
    /// through <see cref="AgentStudio.Runner.ProjectRunner"/>. Mirrors
    /// <c>ProjectRunner.EmitQuotaAdmissionDecision</c>: always a structured log
    /// line, and - for anything other than a silent healthy launch - a task
    /// timeline entry, a chat note, and a load-distribution feed line.
    /// De-duplicated per job so a runner that re-polls the same deferred card
    /// does not spam its timeline.
    /// </summary>
    public void EmitAdmissionDecision(TaskInfo info, QuotaAdmissionPlan plan, string source)
    {
        var warning = plan.ProjectionWarning;
        var logLevel = warning is null ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(
            logLevel,
            "cli_quota_admission_decision source={Source} jobId={JobId} project={Project} outcome={Outcome} cli={Cli} model={Model} isFallback={IsFallback} reason={Reason}",
            source, info.Id, info.ProjectName, plan.Outcome, plan.CliType, plan.Model ?? "<default>", plan.IsFallback, plan.Reason);

        var key = $"{source}|{plan.Outcome}|{plan.CliType}|{plan.Model}|{plan.ThinkingLevel}|{plan.Reason}|{plan.BetterCandidates?.EvidenceSnapshot}";
        var jobKey = $"{info.WatchPath}|{info.Id}";
        if (_lastDecisionByJob.TryGetValue(jobKey, out var prev) && prev == key) return;
        _lastDecisionByJob[jobKey] = key;

        // Keep every admission boundary in the durable decision log. The
        // usage report uses the latest boundary for a job to determine whether
        // a better candidate existed when each token-bearing call ran.
        _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            JobId = info.Id,
            Summary = plan.Reason,
            Reasoning = QuotaAdmissionPlanner.DescribeLoadNumbers(plan),
            BetterCandidates = plan.BetterCandidates,
        });

        if (plan.Outcome == QuotaAdmissionOutcome.LaunchPrimary
            && warning is null
            && plan.BetterCandidates is null) return;

        _chatLog.Append(info, OrchestratorMessageKind.Decision, "[quota-admission] " + plan.Reason);
        _timeline.Append(
            info.FolderPath,
            TimelineEventKinds.QuotaAdmissionDecision,
            TimelineActors.System,
            summary: plan.Reason,
            details: new()
            {
                ["source"] = source,
                ["outcome"] = plan.Outcome.ToString(),
                ["cli"] = plan.CliType,
                ["model"] = plan.Model ?? string.Empty,
                ["isFallback"] = plan.IsFallback ? "true" : "false",
                ["projectionWarning"] = warning?.Reason ?? string.Empty,
                ["betterCandidates"] = SerializeCandidates(plan.BetterCandidates),
                ["matrixUrl"] = plan.BetterCandidates?.MatrixUrl ?? string.Empty,
            });
    }

    internal static string SerializeCandidates(BetterCandidateNote? note)
        => note is null ? string.Empty : JsonSerializer.Serialize(note);

    /// <summary>
    /// Record that a run/claim actually switched CLI families for quota
    /// reasons. Mirrors the <c>cli_quota_fallback_activated</c> block in
    /// <c>ProjectRunner.RunCliAsync</c>.
    /// </summary>
    public void EmitFallbackActivated(TaskInfo info, string? primaryCli, string? primaryModel, CliRouteDecision route, string source)
    {
        var fallbackNote = $"Fallback: {route.CliType}/{route.Model}; reason: quota ({route.Reason})";
        _logger.LogWarning(
            "cli_quota_fallback_activated source={Source} jobId={JobId} primaryCli={PrimaryCli} fallbackCli={FallbackCli} fallbackModel={FallbackModel} reason={Reason}",
            source, info.Id, primaryCli, route.CliType, route.Model, route.Reason);
        _chatLog.Append(info, OrchestratorMessageKind.Decision, "[quota-fallback] " + fallbackNote);
        _timeline.Append(
            info.FolderPath,
            TimelineEventKinds.QuotaFallbackActivated,
            TimelineActors.System,
            summary: fallbackNote,
            details: new()
            {
                ["source"] = source,
                ["primaryCli"] = primaryCli ?? string.Empty,
                ["primaryModel"] = primaryModel ?? string.Empty,
                ["fallbackCli"] = route.CliType,
                ["fallbackModel"] = route.Model ?? string.Empty,
                ["reason"] = "quota",
                ["quotaDetail"] = route.Reason ?? string.Empty,
            });
    }

    /// <summary>
    /// Project-scoped equivalent for chat and one-shot calls that have no task
    /// folder. Load steering is still visible in the shared distribution feed.
    /// </summary>
    public void EmitProjectAdmissionDecision(
        string? watchPath,
        string? projectName,
        QuotaAdmissionPlan plan,
        string source)
    {
        _logger.Log(
            plan.ProjectionWarning is null ? LogLevel.Information : LogLevel.Warning,
            "cli_quota_admission_decision source={Source} project={Project} outcome={Outcome} cli={Cli} model={Model} isFallback={IsFallback} reason={Reason}",
            source, projectName, plan.Outcome, plan.CliType, plan.Model ?? "<default>", plan.IsFallback, plan.Reason);
        if (plan.Outcome == QuotaAdmissionOutcome.LaunchPrimary
            && plan.ProjectionWarning is null
            && plan.BetterCandidates is null)
            return;
        if (string.IsNullOrWhiteSpace(watchPath)) return;

        var key = $"project|{projectName}|{source}|{plan.Outcome}|{plan.CliType}|{plan.Model}|{plan.Reason}";
        if (!_lastDecisionByJob.TryAdd(key, key)) return;
        _orchestratorLog.Append(watchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.LoadDistribution,
            Summary = plan.Reason,
            Reasoning = $"{source}; {QuotaAdmissionPlanner.DescribeLoadNumbers(plan)}",
            BetterCandidates = plan.BetterCandidates,
        });
    }
}
