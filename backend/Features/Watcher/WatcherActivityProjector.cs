using AgentStudio.Bus;
using AgentStudio.Runner;
using AgentStudio.Tasks;

namespace AgentStudio.Watcher;

/// <summary>
/// Projects Watcher case transitions into the two surfaces the dossier's §4
/// visibility model actually has today: the per-project
/// <see cref="OrchestratorLog"/> that "Activity across projects" merges
/// (real UI surface), and the central event bus (§4a contract, for future
/// and out-of-process consumers). A workspace-wide finding (e.g. quota probe
/// silence, not attributable to one project) projects to the workspace-level
/// log under <see cref="WorkspaceWatchPath"/> and to the bus with
/// <c>project: null</c>, per §4a "workspace health events alone use project: null".
/// </summary>
public sealed class WatcherActivityProjector
{
    private readonly OrchestratorLog _log;
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _configuration;
    private readonly AgentMessageBusBridge? _bus;
    private readonly ILogger<WatcherActivityProjector> _logger;

    public WatcherActivityProjector(
        OrchestratorLog log,
        TaskScannerService scanner,
        IConfiguration configuration,
        ILogger<WatcherActivityProjector> logger,
        AgentMessageBusBridge? bus = null)
    {
        _log = log;
        _scanner = scanner;
        _configuration = configuration;
        _logger = logger;
        _bus = bus;
    }

    /// <summary>Where workspace-wide (non-project-attributable) findings project to. Merged into the feed as <c>project: null</c> by the orchestrator-feed endpoint.</summary>
    public string? WorkspaceWatchPath()
    {
        var repo = _configuration["TaskRepository"];
        return string.IsNullOrWhiteSpace(repo) ? null : Path.Combine(repo, ".watcher");
    }

    public void ProjectFindingRaised(WatcherCase watcherCase, WatcherEvidencePack pack)
    {
        Append(watcherCase, OrchestratorLogKinds.Alert, "watcher-finding-raised", watcherCase.LastSummary);
        Emit("observation", "watcher-finding-raised", watcherCase, watcherCase.LastSummary, "Warn",
            new { caseId = watcherCase.Id, watcherCase.Fingerprint, watcherCase.DetectorClass, evidenceDigest = pack.DigestSha256 });
    }

    public void ProjectAnalysisComplete(WatcherCase watcherCase, WatcherAnalysisReceipt analysis)
    {
        var summary = analysis.Ok
            ? $"Watcher analysis for {watcherCase.Id}: {analysis.Summary}"
            : $"Watcher analysis for {watcherCase.Id} unavailable: {analysis.Error}";
        Append(watcherCase, OrchestratorLogKinds.Observation, "watcher-analysis-complete", summary);
        Emit("observation", "watcher-analysis-complete", watcherCase, summary, "Info",
            new { caseId = watcherCase.Id, analysis.Model, analysis.ThinkingLevel, analysis.InputTokens, analysis.OutputTokens, analysis.Ok });
    }

    public void ProjectDecisionRequired(WatcherCase watcherCase, WatcherProposal proposal)
    {
        var summary = proposal.IsComment
            ? $"Watcher case {watcherCase.Id} recurred against open proposal card {proposal.CommentedJobId}."
            : $"Watcher proposal {proposal.Id}: {proposal.Title}";
        Append(watcherCase, OrchestratorLogKinds.Decision, "watcher-decision-required", summary, jobId: proposal.JobId ?? proposal.CommentedJobId);
        Emit("decision", "watcher-decision-required", watcherCase, summary, "Info",
            new { caseId = watcherCase.Id, proposalId = proposal.Id, proposal.IsComment, jobId = proposal.JobId ?? proposal.CommentedJobId },
            jobId: proposal.JobId ?? proposal.CommentedJobId);
    }

    public void ProjectResolved(WatcherCase watcherCase)
    {
        Append(watcherCase, OrchestratorLogKinds.Observation, "watcher-case-resolved",
            $"Watcher case {watcherCase.Id} cleared: the fingerprint stopped recurring.");
        Emit("observation", "watcher-case-resolved", watcherCase, $"Watcher case {watcherCase.Id} resolved.", "Info",
            new { caseId = watcherCase.Id });
    }

    public void ProjectGaveUp(WatcherCase watcherCase, string reason)
    {
        Append(watcherCase, OrchestratorLogKinds.Alert, "watcher-case-gave-up",
            $"Watcher case {watcherCase.Id} gave up: {reason}");
        Emit("observation", "watcher-case-gave-up", watcherCase, $"Watcher case {watcherCase.Id} gave up: {reason}", "Warn",
            new { caseId = watcherCase.Id, reason });
    }

    private void Append(WatcherCase watcherCase, string kind, string topic, string summary, string? jobId = null)
    {
        var watchPath = ResolveWatchPath(watcherCase.Project);
        if (string.IsNullOrWhiteSpace(watchPath)) return;
        _log.Append(watchPath, new OrchestratorLogEntry
        {
            Ts = DateTime.UtcNow,
            Kind = kind,
            Topic = topic,
            Summary = summary,
            JobId = jobId ?? watcherCase.AffectedCards.FirstOrDefault(),
            ParticipantId = AgentMessageBusBridge.ParticipantWatcher,
        });
    }

    private void Emit(string kind, string topic, WatcherCase watcherCase, string summary, string severity, object payload, string? jobId = null)
    {
        try
        {
            var project = watcherCase.Project == QuotaProbeSignalProbe.WorkspaceProject ? null : watcherCase.Project;
            _ = _bus?.EmitWatcherEventAsync(kind, topic, project, jobId ?? watcherCase.AffectedCards.FirstOrDefault(), summary, severity, payload, correlationId: watcherCase.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bus mirror of watcher event failed for case {CaseId}", watcherCase.Id);
        }
    }

    private string? ResolveWatchPath(string project)
    {
        if (string.Equals(project, QuotaProbeSignalProbe.WorkspaceProject, StringComparison.Ordinal))
            return WorkspaceWatchPath();
        return _scanner.GetWatchPaths()
            .FirstOrDefault(w => string.Equals(w.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;
    }
}
