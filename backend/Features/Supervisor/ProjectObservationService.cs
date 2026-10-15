
using AgentStudio.Runner;

namespace AgentStudio.Supervisor;

/// <summary>
/// Read-only observation surface for the per-project supervisor. Returns a
/// <see cref="SupervisorObservation"/> snapshot for one project; calling this
/// cannot mutate runner state, job state, or any file. The hard health checks
/// and the soft reasoner consume the same observation each tick.
/// </summary>
/// <remarks>
/// Quota is intentionally null in this first cut. Wiring it up means depending
/// on <c>QuotaService</c>'s background refresh and adds load to a path that
/// runs every 10 s; we wait until the cadence numbers settle before adding it.
/// </remarks>
public sealed class ProjectObservationService
{
    private readonly TaskRunnerService _taskRunner;
    private readonly TaskScannerService _jobScanner;
    private readonly ILogger<ProjectObservationService> _logger;
    private readonly TimeProvider _time;
    private readonly OrchestratorDecisionStore? _decisionReceipts;

    public ProjectObservationService(
        TaskRunnerService taskRunner,
        TaskScannerService jobScanner,
        ILogger<ProjectObservationService> logger,
        TimeProvider? time = null,
        OrchestratorDecisionStore? decisionReceipts = null)
    {
        _taskRunner = taskRunner;
        _jobScanner = jobScanner;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _decisionReceipts = decisionReceipts;
    }

    public Task<SupervisorObservation> ObserveAsync(string project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ct.ThrowIfCancellationRequested();

        var status = _taskRunner.GetStatus();
        var capturedAt = _time.GetUtcNow().UtcDateTime;

        if (status.Projects == null || !status.Projects.TryGetValue(project, out var projectStatus) || projectStatus == null)
        {
            return Task.FromResult(IdleObservation(project, capturedAt));
        }

        var activeJobId = projectStatus.ActiveJobId;
        var runnerStatus = (projectStatus.Mode ?? "unknown") + (activeJobId != null ? " (active)" : string.Empty);

        if (string.IsNullOrEmpty(activeJobId))
        {
            return Task.FromResult(new SupervisorObservation(
                CapturedAt: capturedAt,
                Project: project,
                RunnerStatus: runnerStatus,
                CurrentJobId: null,
                CurrentRunState: null,
                LastProgressAt: null,
                Quota: null,
                RecentDecisions: Array.Empty<SupervisorRecentDecision>(),
                RecentAgentSamples: Array.Empty<string>(),
                ErrorCounts: new SupervisorErrorCounts(0, 0, 0)));
        }

        var info = SafeFindJob(activeJobId);

        // Cycle 2c: LastProgressAt must reflect the union of every file the
        // runner may append to during a live session, not just cli-output.log.
        // Sessions whose CLI primarily emits tool-use events write
        // logs/tool-calls.jsonl while cli-output.log stays nearly empty; the
        // pre-Cycle-2 path read only cli-output.log, classified those sessions
        // as no-progress (the supervisor wrote 1830 false no-progress
        // advisories on 2026-05-09 against a session that was actively
        // streaming tool calls), and looped the bus + observations.jsonl with
        // duplicate "stalled" warnings every 10 s. The union pattern lives in
        // StaleProgressArchiver.MeasureFolder for the same reason; this
        // mirrors it for the live observation path.
        DateTime? lastProgressAt = null;
        if (info != null)
        {
            var folderMtime = MaxMtimeAcrossActivityFiles(info.FolderPath);
            if (folderMtime != null && (lastProgressAt == null || folderMtime > lastProgressAt))
            {
                lastProgressAt = folderMtime;
            }
        }

        // A decision observation uses typed runner state and file metadata.
        // Raw CLI lines are not part of the supervision model input.
        IReadOnlyList<SupervisorRecentDecision> recentDecisions = _decisionReceipts?
            .ListReceipts(activeJobId, 5, project)
            .Select(receipt => new SupervisorRecentDecision(receipt.EndedAt, receipt.Profile,
                receipt.ChosenAction, receipt.EvidenceRef)).ToArray() ?? [];
        IReadOnlyList<string> recentSamples = [];
        var errorCounts = new SupervisorErrorCounts(0, 0, 0);

        return Task.FromResult(new SupervisorObservation(
            CapturedAt: capturedAt,
            Project: project,
            RunnerStatus: runnerStatus,
            CurrentJobId: activeJobId,
            CurrentRunState: info?.State,
            LastProgressAt: lastProgressAt,
            Quota: null,
            RecentDecisions: recentDecisions,
            RecentAgentSamples: recentSamples,
            ErrorCounts: errorCounts)
        {
            Omissions = ["recentAgentSamples:raw-stream", "errorCounts:raw-log"],
        });
    }

    private TaskInfo? SafeFindJob(string jobId)
    {
        try { return _jobScanner.FindJob(jobId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProjectObservationService FindJob failed for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Returns the latest mtime across every file the runner writes to
    /// during an active session: <c>task.json</c>, anything under
    /// <c>logs/</c> (cli-output.log, tool-calls.jsonl, session-events.jsonl,
    /// future log files). Mirrors
    /// <c>StaleProgressArchiver.MeasureFolder</c>'s "any append counts"
    /// heuristic so the supervisor's progress detection stays aligned with
    /// the boot-time orphan classifier and never disagrees on whether a
    /// folder is making progress. Best-effort: an unreadable file or
    /// missing folder is just skipped.
    /// </summary>
    private static DateTime? MaxMtimeAcrossActivityFiles(string jobFolder)
    {
        var maxStamp = DateTime.MinValue;
        try
        {
            var jobJson = Path.Combine(jobFolder, "task.json");
            if (File.Exists(jobJson))
            {
                var stamp = File.GetLastWriteTimeUtc(jobJson);
                if (stamp > maxStamp) maxStamp = stamp;
            }
            var logsDir = Path.Combine(jobFolder, "logs");
            if (Directory.Exists(logsDir))
            {
                foreach (var file in Directory.EnumerateFiles(logsDir))
                {
                    try
                    {
                        var stamp = File.GetLastWriteTimeUtc(file);
                        if (stamp > maxStamp) maxStamp = stamp;
                    }
                    catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectObservationService: skip unreadable"); /* skip unreadable */ }
                }
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "ProjectObservationService: best-effort"); /* best-effort */ }
        return maxStamp == DateTime.MinValue ? null : maxStamp;
    }

    private static SupervisorObservation IdleObservation(string project, DateTime capturedAt) => new(
        CapturedAt: capturedAt,
        Project: project,
        RunnerStatus: "unknown",
        CurrentJobId: null,
        CurrentRunState: null,
        LastProgressAt: null,
        Quota: null,
        RecentDecisions: Array.Empty<SupervisorRecentDecision>(),
        RecentAgentSamples: Array.Empty<string>(),
        ErrorCounts: new SupervisorErrorCounts(0, 0, 0));
}
