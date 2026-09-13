

namespace AgentStudio.Projects;

/// <summary>
/// Body for <c>POST /api/watch-paths</c>. Only the display name is
/// configurable today; the resolved path is derived from
/// <c>TaskRepository</c> + slug(name) by
/// <see cref="WorkspaceManagementService"/>.
/// </summary>
public sealed record CreateWorkspaceRequest(string? Name);

/// <summary>
/// Workspace-wide read surfaces under <c>/api/workspace</c>: views that
/// fold across every watched project. Today the only entry is the
/// token-usage timeline that powers the workspace token view; further
/// workspace-level views (drift, audits, design council) will land
/// here over time.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspace");

        // Workspace token-usage timeline. Returns one cell per
        // (project, time bucket) with input/output/cache/total token
        // counts plus a theoretical dollar estimate when the model is
        // priced. windowHours defaults to 24 and accepts {1, 6, 24, 168};
        // bucketMinutes defaults to 60 and accepts {5, 15, 60}. Out-of-
        // range values snap to the defaults rather than failing - the
        // status-bar entry into this view should always render.
        group.MapGet("/tokens/timeline",
            (int? windowHours, int? bucketMinutes, TaskScannerService scanner, ITokenAggregator tokens, BetterCandidateUsageReportService candidateUsage, WorkspaceTokensCacheStore cache) =>
            {
                var projects = scanner.GetWatchPaths()
                    .Select(e => (e.Name, e.Path))
                    .ToList();
                var resolvedWindowHours = windowHours ?? WorkspaceTokensTimelineService.DefaultWindowHours;
                var resolvedBucketMinutes = bucketMinutes ?? WorkspaceTokensTimelineService.DefaultBucketMinutes;
                var timeline = tokens.WorkspaceTimeline(projects, resolvedWindowHours, resolvedBucketMinutes);
                var result = timeline with
                {
                    BetterCandidateUsage = candidateUsage.Build(
                        projects,
                        DateTime.Parse(timeline.WindowStart, null, System.Globalization.DateTimeStyles.RoundtripKind),
                        DateTime.Parse(timeline.WindowEnd, null, System.Globalization.DateTimeStyles.RoundtripKind)),
                };
                // Persist the snapshot so the next hover renders before the
                // live aggregator has finished. Snapshot files are keyed by
                // (windowHours, bucketMinutes) so the 24h and 7d views don't
                // overwrite each other.
                cache.WriteTimeline(result.WindowHours, result.BucketMinutes, result);
                return Results.Ok(result);
            });

        // Cache-only timeline read: returns the persisted snapshot for
        // the (windowHours, bucketMinutes) combo without re-folding the
        // bus. The hover panel calls this on first paint so historical
        // numbers appear instantly; 204 No Content means no cache yet.
        group.MapGet("/tokens/timeline/cached",
            (int? windowHours, int? bucketMinutes, WorkspaceTokensCacheStore cache) =>
            {
                var resolvedWindowHours = windowHours ?? WorkspaceTokensTimelineService.DefaultWindowHours;
                var resolvedBucketMinutes = bucketMinutes ?? WorkspaceTokensTimelineService.DefaultBucketMinutes;
                var snap = cache.ReadTimeline(resolvedWindowHours, resolvedBucketMinutes);
                return snap == null ? Results.NoContent() : Results.Ok(snap);
            });

        group.MapGet("/tokens/expensive-jobs",
            (int? limit, TaskScannerService scanner, ITokenAggregator tokens, WorkspaceTokensCacheStore cache) =>
            {
                var perProjectLimit = Math.Clamp(limit ?? ProjectTokenUsageService.DefaultExpensiveLimit, 1, 50);
                var jobs = scanner.GetWatchPaths()
                    .SelectMany(entry => tokens.ProjectExpensiveJobs(entry.Name, entry.Path, perProjectLimit)
                        .Select(job => new WorkspaceExpensiveJobDto(
                            Project: entry.Name,
                            JobId: job.JobId,
                            Title: job.Title,
                            State: job.State,
                            Category: job.Category,
                            TotalTokens: job.TotalTokens,
                            Calls: job.Calls,
                            LastActivity: job.LastActivity,
                            LastModel: job.LastModel)))
                    .OrderByDescending(job => job.TotalTokens)
                    .Take(perProjectLimit)
                    .ToList();
                var response = new WorkspaceExpensiveJobsResponse(jobs);
                cache.WriteExpensiveJobs(response);
                return Results.Ok(response);
            });

        // Cache-only expensive-jobs read.
        group.MapGet("/tokens/expensive-jobs/cached",
            (WorkspaceTokensCacheStore cache) =>
            {
                var snap = cache.ReadExpensiveJobs();
                return snap == null ? Results.NoContent() : Results.Ok(snap);
            });

        // Workspace-wide visual evidence reel. Folds the per-job
        // results/ scan over every watched job touched in the
        // requested window, ordered newest-first. windowHours
        // defaults to 72 (three days) and is clamped to >= 1; an
        // optional projectFilter (project display name) narrows the
        // result. Drives the "Visual evidence" reel overlay.
        group.MapGet("/screenshots",
            (int? windowHours, string? projectFilter, ScreenshotIndexService screenshots) =>
            {
                var hours = Math.Max(1, windowHours ?? 72);
                var entries = screenshots.ListWorkspaceScreenshots(hours, projectFilter);
                return Results.Ok(new WorkspaceScreenshotsResponse
                {
                    WindowHours = hours,
                    ProjectFilter = projectFilter,
                    Screenshots = entries.ToList()
                });
            });

        // Create a new (empty) workspace. The Name is the only knob the
        // caller chooses; the resolved path is derived under
        // TaskRepository/projects/{slug}. Validation lives in the
        // service: empty name, name length, slug uniqueness, name
        // uniqueness (case-insensitive). 201 on success, 400 for
        // validation failures, 409 when the name or slug collides.
        app.MapPost("/api/watch-paths", (CreateWorkspaceRequest req, WorkspaceManagementService workspaces) =>
        {
            var result = workspaces.Create(req?.Name);
            return result.Outcome switch
            {
                WorkspaceManagementOutcome.Created =>
                    Results.Created($"/api/watch-paths/{Uri.EscapeDataString(result.Entry!.Name)}", result.Entry),
                WorkspaceManagementOutcome.BadRequest =>
                    Results.BadRequest(new { error = result.Error }),
                WorkspaceManagementOutcome.Conflict =>
                    Results.Conflict(new { error = result.Error }),
                _ => Results.Problem("Unexpected outcome from workspace create.")
            };
        });

        // Delete a workspace by name. Refuses (409) when the workspace
        // still contains job folders so the user cannot orphan work by
        // mistake; the on-disk folder is left in place so a re-create
        // with the same name is reversible.
        app.MapDelete("/api/watch-paths/{name}", (string name, WorkspaceManagementService workspaces) =>
        {
            var result = workspaces.Delete(name);
            return result.Outcome switch
            {
                WorkspaceManagementOutcome.Ok =>
                    Results.Ok(new { name = result.Entry!.Name }),
                WorkspaceManagementOutcome.NotFound =>
                    Results.NotFound(new { error = result.Error }),
                WorkspaceManagementOutcome.Conflict =>
                    Results.Conflict(new { error = result.Error, jobCount = result.TaskCount }),
                WorkspaceManagementOutcome.BadRequest =>
                    Results.BadRequest(new { error = result.Error }),
                _ => Results.Problem("Unexpected outcome from workspace delete.")
            };
        });

        // Workspace executive summary. Folds per-project activity
        // (job moves, supervisor advisories, repository commits) plus
        // workspace-level crash records and any open
        // human-decision-needed-* tasks into one snapshot covering the
        // requested window. windowHours defaults to 24 and accepts
        // {1, 6, 24, 168}; out-of-range values snap to the default
        // so the page always renders. Schema:
        // docs/app/schemas/executive-summary.schema.json.
        group.MapGet("/summary",
            (int? windowHours, WorkspaceSummaryService summary) =>
            {
                var result = summary.Build(windowHours ?? WorkspaceSummaryService.DefaultWindowHours);
                return Results.Ok(result);
            });
    }
}

public sealed record WorkspaceExpensiveJobsResponse(IReadOnlyList<WorkspaceExpensiveJobDto> Jobs);

public sealed record WorkspaceExpensiveJobDto(
    string Project,
    string JobId,
    string Title,
    string? State,
    string Category,
    long TotalTokens,
    int Calls,
    string? LastActivity,
    string? LastModel);
