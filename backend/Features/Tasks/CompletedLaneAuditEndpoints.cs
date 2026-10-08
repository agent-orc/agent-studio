
namespace AgentStudio.Tasks;

/// <summary>
/// Project-scoped routes for the completed-lane audit (Part 2 of the
/// consolidation/audit task):
///
/// <list type="bullet">
/// <item><c>POST /api/projects/{id}/completed-lane/audit</c> - kick off an async run.</item>
/// <item><c>GET  /api/projects/{id}/completed-lane/report</c> - markdown for the latest run.</item>
/// <item><c>GET  /api/audits/{runId}</c> - per-run progress + entries.</item>
/// </list>
///
/// Project lookup accepts the canonical <c>PROJ-NNN</c> id, the display
/// name, or a raw watch-path - the service normalises through
/// <see cref="AgentStudio.Registry.ProjectRegistry"/> first
/// and falls back to <c>TaskScannerService.GetWatchPaths</c> for legacy
/// callers.
/// </summary>
public static class CompletedLaneAuditEndpoints
{
    public static void MapCompletedLaneAuditEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{projectId}/completed-lane/audit",
            (string projectId, HttpContext ctx, CompletedLaneAuditService audit) =>
        {
            var who = ctx.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "system";
            var outcome = audit.StartAudit(projectId, who);
            return outcome.Status switch
            {
                AuditRunStartStatus.Success => Results.Accepted(
                    $"/api/audits/{outcome.RunId}",
                    new { runId = outcome.RunId }),
                AuditRunStartStatus.ProjectNotFound => Results.NotFound(new { error = outcome.Message }),
                _ => Results.Problem(outcome.Message ?? "audit failed to start"),
            };
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);

        app.MapGet("/api/projects/{projectId}/completed-lane/report",
            (string projectId, CompletedLaneAuditService audit) =>
        {
            var report = audit.BuildReport(projectId);
            if (report == null)
                return Results.NotFound(new { error = "No audit has been run for this project yet." });
            return Results.Ok(report);
        });

        app.MapGet("/api/audits/{runId}",
            (string runId, AuditRunStore store) =>
        {
            var status = store.Get(runId);
            return status == null ? Results.NotFound() : Results.Ok(status);
        });

        // AGT-2202: accepted-but-not-integrated listing. Re-derives the live git
        // integration verdict for every completed/archived card tagged
        // integrationpending, self-heals resolved ones, and returns the ones
        // whose work is still not in develop.
        app.MapGet("/api/projects/{projectId}/completed-lane/integration-pending",
            (string projectId, CompletedLaneAuditService audit) =>
                Results.Ok(audit.ListIntegrationPending(projectId)));

        // Read-only legacy inventory. Historical claims without a recorded
        // brief/content receipt stay visible for operator re-check; this route
        // never moves or rewrites a card.
        app.MapGet("/api/projects/{projectId}/completed-lane/content-review-gaps",
            (string projectId, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var project = projectId == "*" ? null : projects.FindByIdOrDisplayName(projectId);
            if (projectId != "*" && project is null)
                return Results.NotFound(new { error = $"Project '{projectId}' was not found." });
            var rows = scanner.ScanAllAutomationJobsWithArchive()
                .Where(card => card.State is TaskStates.Completed or TaskStates.Archive)
                .Where(card => project is null || string.Equals(card.WatchPath, project.StorageLocation,
                    StringComparison.OrdinalIgnoreCase))
                .Where(card => CompletionContentPolicy.NeedsHistoricalReview(card.CompletionClaim, card.Mode))
                .Select(card => new { TaskKey = card.Key ?? card.Id, card.Title, card.State,
                    card.CompletionClaim?.RecordedAtUtc,
                    card.CompletionClaim?.CurrentBriefVersion,
                    card.CompletionClaim?.DeliveryBriefVersion,
                    card.CompletionClaim?.ReviewBriefVersion,
                    card.CompletionClaim?.ContentAspect,
                    card.CompletionClaim?.ContentStatus,
                    card.CompletionClaim?.ContentSummary,
                    card.CompletionClaim?.RanAspects,
                    card.CompletionClaim?.SkippedAspects,
                    card.CompletionClaim?.Basis })
                .OrderBy(row => row.TaskKey)
                .ToArray();
            return Results.Ok(new { project = projectId, scannedAtUtc = DateTime.UtcNow,
                count = rows.Length, rows });
        });
    }
}
