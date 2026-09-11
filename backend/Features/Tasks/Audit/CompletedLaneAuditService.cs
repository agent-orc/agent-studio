using System.Text;

namespace AgentStudio.Tasks;

public enum ReEvaluateStatus
{
    Success,
    TaskNotFound,
    WrongLane,
    Failure,
}

public record ReEvaluateOutcome(ReEvaluateStatus Status, ReEvaluateResponse? Response = null, string? Message = null);

public enum AuditRunStartStatus
{
    Success,
    ProjectNotFound,
    Failure,
}

public record AuditRunStartOutcome(AuditRunStartStatus Status, string? RunId = null, string? Message = null);

/// <summary>
/// Owns the completed-lane audit flow described in Part 2 of the
/// consolidation/audit task. Three entry points:
///
/// <list type="bullet">
/// <item><see cref="ReEvaluate"/> - one card, synchronous.</item>
/// <item><see cref="StartAudit"/> - whole project, async; returns a runId.</item>
/// <item><see cref="BuildReport"/> - markdown for the latest run.</item>
/// </list>
///
/// The detector lives in <see cref="AcceptanceEvidenceDetector"/>; this
/// class is the orchestration layer that walks the lane, persists the
/// verdicts, and triggers the lane flip on <see cref="AuditVerdicts.NotReallyDone"/>.
/// </summary>
public sealed class CompletedLaneAuditService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly TimelineLog _timeline;
    private readonly AcceptanceEvidenceDetector _detector;
    private readonly AuditRunStore _runStore;
    private readonly ProjectRegistry _projects;
    private readonly ILogger<CompletedLaneAuditService> _logger;
    private readonly TaskIntegrationStatusService? _integrationStatus;
    private readonly FailureInterventionService? _failureInterventions;

    public CompletedLaneAuditService(
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskMutationService mutations,
        TimelineLog timeline,
        AcceptanceEvidenceDetector detector,
        AuditRunStore runStore,
        ProjectRegistry projects,
        ILogger<CompletedLaneAuditService> logger,
        TaskIntegrationStatusService? integrationStatus = null,
        FailureInterventionService? failureInterventions = null)
    {
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _timeline = timeline;
        _detector = detector;
        _runStore = runStore;
        _projects = projects;
        _logger = logger;
        _integrationStatus = integrationStatus;
        _failureInterventions = failureInterventions;
    }

    /// <summary>
    /// AGT-2202 — lists the accepted cards (6-completed / 7-archive) whose work is
    /// still not in the integration branch. Re-derives the live git integration
    /// verdict for every card carrying the <c>integrationpending</c> tag, clears
    /// the tag from any that have since become integrated (the marker self-heals),
    /// and returns the ones still pending / conflicted. Project scope accepts the
    /// PROJ-NNN id, the display name, or a watch path; a null/blank project scans
    /// the whole workspace. Read-only apart from clearing resolved tags.
    /// </summary>
    public IntegrationPendingListing ListIntegrationPending(string? projectIdOrName)
    {
        var project = string.IsNullOrWhiteSpace(projectIdOrName)
            ? null
            : _projects.FindByIdOrDisplayName(projectIdOrName) ?? _projects.FindByStorageLocation(projectIdOrName);
        var watchPath = project?.StorageLocation
            ?? (string.IsNullOrWhiteSpace(projectIdOrName)
                ? null
                : _scanner.GetWatchPaths()
                    .FirstOrDefault(w => string.Equals(w.Name, projectIdOrName, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(w.Path, projectIdOrName, StringComparison.OrdinalIgnoreCase))?.Path);

        var candidates = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(j => j.State == TaskStates.Completed || j.State == TaskStates.Archive)
            .Where(j => watchPath == null || string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase))
            .Where(j => (j.Tags ?? []).Any(IntegrationStatuses.IsPendingTag))
            .ToList();

        var statusByKey = _integrationStatus?.BuildLookup(candidates)
            ?? new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);

        var items = new List<IntegrationPendingItem>();
        var cleared = 0;
        foreach (var job in candidates.OrderByDescending(j => j.EnteredLaneAt))
        {
            statusByKey.TryGetValue(job.TaskKey, out var status);
            var verdict = status?.Status ?? IntegrationStatuses.Pending;

            if (!IntegrationStatuses.IsNotIntegrated(verdict))
            {
                // Now integrated (or nothing to integrate): drop the stale tag.
                var tags = (job.Tags ?? [])
                    .Where(t => !IntegrationStatuses.IsPendingTag(t))
                    .ToList();
                _mutations.SetJobTags(job.Id, tags, job.WatchPath);
                cleared++;
                continue;
            }

            items.Add(new IntegrationPendingItem
            {
                JobId = job.Id,
                Key = job.Key,
                Title = job.Title,
                State = job.State,
                IntegrationStatus = verdict,
                IntegrationBranch = status?.IntegrationBranch ?? "develop",
                Detail = status?.Detail,
            });
        }

        return new IntegrationPendingListing
        {
            ProjectId = project?.Id ?? projectIdOrName ?? "",
            ProjectName = project?.DisplayName ?? projectIdOrName ?? "(all projects)",
            GeneratedAt = DateTime.UtcNow,
            Items = items,
            Cleared = cleared,
        };
    }

    public ReEvaluateOutcome ReEvaluate(string jobId, string? watchPath, string actorEmail)
    {
        var job = _scanner.FindJob(jobId, watchPath);
        if (job == null) return new ReEvaluateOutcome(ReEvaluateStatus.TaskNotFound);
        if (job.State != TaskStates.Completed && job.State != TaskStates.Archive)
        {
            return new ReEvaluateOutcome(ReEvaluateStatus.WrongLane,
                Message: $"Job is in {job.State}; re-evaluate only applies to {TaskStates.Completed} / {TaskStates.Archive}.");
        }

        var (verdict, diagnostics) = _detector.Evaluate(job);
        var newState = job.State;
        if (verdict == AuditVerdicts.NotReallyDone)
        {
            // Reopen for another pass at 2-ready (the retired 1b-needs-human-review
            // lane is gone; QualityLoopReopened always means a re-attempt now).
            var outcome = _states.MoveJob(
                jobId, TaskStates.Ready, watchPath,
                transitionCause: LaneChangeCauses.QualityLoop, transitionDetail: AuditReopenCause);
            if (outcome.Status == MoveJobStatus.Success)
            {
                newState = TaskStates.Ready;
                var refreshed = _scanner.FindJob(jobId, watchPath);
                var folder = refreshed?.FolderPath ?? job.FolderPath;
                AppendQualityLoopReopened(folder, job, diagnostics, actorEmail);
            }
            else
            {
                _logger.LogWarning(
                    "Re-evaluate verdict was NotReallyDone but move failed for {Job}: {Status}",
                    jobId, outcome.Status);
            }
        }
        else if (verdict == AuditVerdicts.Inconclusive)
        {
            var tags = (job.Tags ?? []).ToList();
            if (!tags.Any(t => string.Equals(t, "needs-fresh-look", StringComparison.OrdinalIgnoreCase)))
            {
                tags.Add("needs-fresh-look");
                _mutations.SetJobTags(jobId, tags, watchPath);
            }
        }

        return new ReEvaluateOutcome(ReEvaluateStatus.Success, new ReEvaluateResponse
        {
            JobId = jobId,
            Verdict = verdict,
            NewState = newState,
            Diagnostics = diagnostics,
        });
    }

    public AuditRunStartOutcome StartAudit(string projectIdOrName, string actorEmail)
    {
        // Accept either the canonical PROJ-NNN id, the display name, or
        // the project's WatchPath. ProjectRegistry handles the first two;
        // a raw watch path falls through to a scanner-driven match.
        var project = _projects.FindByIdOrDisplayName(projectIdOrName)
                      ?? _projects.FindByStorageLocation(projectIdOrName);
        var watchPath = project?.StorageLocation
                        ?? _scanner.GetWatchPaths()
                            .FirstOrDefault(w => string.Equals(w.Name, projectIdOrName, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(w.Path, projectIdOrName, StringComparison.OrdinalIgnoreCase))?.Path;
        if (string.IsNullOrWhiteSpace(watchPath))
        {
            return new AuditRunStartOutcome(AuditRunStartStatus.ProjectNotFound,
                Message: $"No project found for '{projectIdOrName}'.");
        }
        var projectName = project?.DisplayName
                          ?? _scanner.GetWatchPaths().FirstOrDefault(w =>
                              string.Equals(w.Path, watchPath, StringComparison.OrdinalIgnoreCase))?.Name
                          ?? projectIdOrName;

        var runId = _runStore.Create(project?.Id ?? projectIdOrName, projectName, watchPath);

        // Snapshot the candidate set now so a card moving mid-run does not
        // produce a confusing "processed > total" report.
        var candidates = _scanner.ScanAllAutomationJobs()
            .Where(j => string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase))
            .Where(j => j.State == TaskStates.Completed || j.State == TaskStates.Archive)
            .OrderBy(j => j.LastActivity)
            .Select(j => j.Id)
            .ToList();

        _runStore.Update(runId, s => s with { Total = candidates.Count });

        _ = Task.Run(() => RunAuditAsync(runId, watchPath, candidates, actorEmail));

        return new AuditRunStartOutcome(AuditRunStartStatus.Success, RunId: runId);
    }

    private async Task RunAuditAsync(string runId, string watchPath, List<string> candidates, string actorEmail)
    {
        try
        {
            foreach (var jobId in candidates)
            {
                var current = _scanner.FindJob(jobId, watchPath);
                if (current == null || (current.State != TaskStates.Completed && current.State != TaskStates.Archive))
                {
                    // Moved out of scope between snapshot and processing;
                    // count it but skip the evaluation.
                    _runStore.Update(runId, s => s with
                    {
                        Processed = s.Processed + 1,
                        Inconclusive = s.Inconclusive + 1,
                        Entries = s.Entries.Concat(new[]
                        {
                            new CompletedLaneAuditEntry
                            {
                                JobId = jobId,
                                Title = current?.Title ?? "",
                                Key = current?.Key,
                                Verdict = AuditVerdicts.Inconclusive,
                                Reason = "Card moved out of completed/archive mid-run.",
                                EvaluatedAt = DateTime.UtcNow,
                            }
                        }).ToList(),
                    });
                    continue;
                }

                var (verdict, diagnostics) = _detector.Evaluate(current);
                var reason = diagnostics.Count == 0
                    ? "No issues found."
                    : string.Join("; ", diagnostics.Select(d => d.Detail));

                if (verdict == AuditVerdicts.NotReallyDone)
                {
                    // Reopen for another pass at 2-ready (the retired
                    // 1b-needs-human-review lane is gone).
                    var outcome = _states.MoveJob(
                        jobId, TaskStates.Ready, watchPath,
                        transitionCause: LaneChangeCauses.QualityLoop, transitionDetail: AuditReopenCause);
                    if (outcome.Status == MoveJobStatus.Success)
                    {
                        var refreshed = _scanner.FindJob(jobId, watchPath);
                        var folder = refreshed?.FolderPath ?? current.FolderPath;
                        AppendQualityLoopReopened(folder, current, diagnostics, actorEmail);
                    }
                }
                else if (verdict == AuditVerdicts.Inconclusive)
                {
                    var tags = (current.Tags ?? []).ToList();
                    if (!tags.Any(t => string.Equals(t, "needs-fresh-look", StringComparison.OrdinalIgnoreCase)))
                    {
                        tags.Add("needs-fresh-look");
                        _mutations.SetJobTags(jobId, tags, watchPath);
                    }
                }

                _runStore.Update(runId, s => s with
                {
                    Processed = s.Processed + 1,
                    TrulyDone = s.TrulyDone + (verdict == AuditVerdicts.Ok ? 1 : 0),
                    NotReallyDone = s.NotReallyDone + (verdict == AuditVerdicts.NotReallyDone ? 1 : 0),
                    Inconclusive = s.Inconclusive + (verdict == AuditVerdicts.Inconclusive ? 1 : 0),
                    Entries = s.Entries.Concat(new[]
                    {
                        new CompletedLaneAuditEntry
                        {
                            JobId = jobId,
                            Key = current.Key,
                            Title = current.Title,
                            Verdict = verdict,
                            Reason = reason,
                            EvaluatedAt = DateTime.UtcNow,
                        }
                    }).ToList(),
                });

                // Cooperative yield so a large run does not starve other
                // requests on the same thread pool.
                await Task.Yield();
            }

            _runStore.Update(runId, s => s with
            {
                FinishedAt = DateTime.UtcNow,
                Status = "finished",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Completed-lane audit run {RunId} failed", runId);
            _runStore.Update(runId, s => s with
            {
                FinishedAt = DateTime.UtcNow,
                Status = "failed",
                Error = ex.Message,
            });
        }
    }

    public CompletedLaneAuditReport? BuildReport(string projectIdOrName)
    {
        var project = _projects.FindByIdOrDisplayName(projectIdOrName)
                      ?? _projects.FindByStorageLocation(projectIdOrName);
        var projectKey = project?.Id ?? projectIdOrName;
        var latest = _runStore.GetLatestForProject(projectKey);
        if (latest == null) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"# Completed-lane audit ({project?.DisplayName ?? projectIdOrName}, run {latest.RunId}, {latest.StartedAt:yyyy-MM-dd HH:mm} UTC)");
        sb.AppendLine();

        if (_failureInterventions is not null)
        {
            var interventions = _failureInterventions.List(latest.WatchPath);
            var interventionReport = FailureInterventionReporting.Summarize(interventions);
            sb.AppendLine("## Orchestrator interventions");
            sb.AppendLine($"- count: {interventionReport.Count}");
            sb.AppendLine($"- open: {interventionReport.Open}");
            sb.AppendLine($"- closed: {interventionReport.Closed}");
            foreach (var item in interventions)
            {
                sb.AppendLine("- " + FailureInterventionReporting.Line(item));
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Verdict counts");
        sb.AppendLine($"- truly done: {latest.TrulyDone}");
        sb.AppendLine($"- not really done: {latest.NotReallyDone}");
        sb.AppendLine($"- inconclusive: {latest.Inconclusive}");
        sb.AppendLine($"- processed: {latest.Processed}/{latest.Total}");
        sb.AppendLine($"- status: {latest.Status}");
        sb.AppendLine();

        var notDone = latest.Entries.Where(e => e.Verdict == AuditVerdicts.NotReallyDone).ToList();
        if (notDone.Count > 0)
        {
            sb.AppendLine("## Not really done (action required)");
            foreach (var e in notDone)
            {
                var keyOrId = string.IsNullOrEmpty(e.Key) ? e.JobId : $"{e.Key} ({e.JobId})";
                sb.AppendLine($"- [{keyOrId}] {Truncate(e.Title, 80)} - {Truncate(e.Reason, 200)}");
            }
            sb.AppendLine();
        }

        var inconclusive = latest.Entries.Where(e => e.Verdict == AuditVerdicts.Inconclusive).ToList();
        if (inconclusive.Count > 0)
        {
            sb.AppendLine("## Inconclusive (tagged needs-fresh-look)");
            foreach (var e in inconclusive)
            {
                var keyOrId = string.IsNullOrEmpty(e.Key) ? e.JobId : $"{e.Key} ({e.JobId})";
                sb.AppendLine($"- [{keyOrId}] {Truncate(e.Title, 80)} - {Truncate(e.Reason, 200)}");
            }
            sb.AppendLine();
        }

        return new CompletedLaneAuditReport
        {
            ProjectId = projectKey,
            ProjectName = project?.DisplayName ?? projectIdOrName,
            RunId = latest.RunId,
            GeneratedAt = DateTime.UtcNow,
            Markdown = sb.ToString(),
        };
    }

    /// <summary>Quality-loop cause id of a completed-lane audit reopen, on the reopen row and the lane row alike.</summary>
    internal const string AuditReopenCause = "completed-lane-audit";

    private void AppendQualityLoopReopened(string folderPath, TaskInfo job, List<EvidenceDiagnostic> diagnostics, string actorEmail)
    {
        var details = new Dictionary<string, string>
        {
            ["cause"] = AuditReopenCause,
            ["fromState"] = job.State,
            ["toState"] = TaskStates.Ready,
            ["diagnosticCount"] = diagnostics.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var hardFails = diagnostics.Where(d => d.Level == AuditSignalLevels.Fail).ToList();
        if (hardFails.Count > 0)
        {
            details["failKinds"] = string.Join(",", hardFails.Select(d => d.Kind));
        }

        _timeline.Append(folderPath, new TimelineEvent
        {
            Ts = DateTime.UtcNow,
            Kind = TimelineEventKinds.QualityLoopReopened,
            Actor = TimelineActors.QualityLoop,
            Summary = hardFails.Count > 0
                ? $"Reopened by completed-lane audit: {hardFails[0].Detail}"
                : "Reopened by completed-lane audit.",
            Details = details,
        });
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "...";
    }
}
