namespace AgentStudio.Git;

/// <summary>Mode plus per-class windows as the operator UI reads and writes them.</summary>
public sealed record BranchSweepSettingsResponse(
    string Project,
    string Mode,
    BranchRetentionWindows Windows,
    BranchRetentionWindows Defaults);

/// <summary>Body of <c>PUT /api/git/branch-sweep/settings</c>. Null days keep the shared default.</summary>
public sealed record BranchSweepSettingsRequest(
    string? Mode,
    int? TaskRetentionDays,
    int? SalvageRetentionDays,
    int? QuarantineRetentionDays,
    int? AbandonedRetentionDays);

/// <summary>
/// Operator surface for the stale-branch sweep (AGT-2794), used by the Project
/// Hub Git-Management panel. Reads are the stored latest report and a fresh
/// read-only plan; writes are a sweep run, an operator-confirmed delete of an
/// explicit ref subset, and the per-project mode plus retention overrides.
/// </summary>
public static class BranchSweepEndpoints
{
    public static void MapBranchSweepEndpoints(this WebApplication app)
    {
        // Latest persisted report for a project, or 204 when the project has
        // never been swept. This is what the panel shows on open; it never
        // triggers a sweep by itself.
        app.MapGet("/api/git/branch-sweep/latest", (string project, BranchSweepReportStore reports) =>
        {
            var report = reports.Latest(project);
            return report is null ? Results.NoContent() : Results.Ok(report);
        });

        // Stamps of the stored runs, newest first.
        app.MapGet("/api/git/branch-sweep/history", (string project, int? limit, BranchSweepReportStore reports) =>
            Results.Ok(new { project, runs = reports.History(project, limit ?? 20) }));

        // Fresh read-only classification. Nothing is written or deleted; this is
        // the plan the confirm-and-execute action is checked against.
        app.MapGet("/api/git/branch-sweep/plan", (
            string project, BranchSweepService sweep, CancellationToken ct) =>
        {
            var report = sweep.Plan(project, ct);
            return report.Error is null ? Results.Ok(report) : Results.BadRequest(new { error = report.Error });
        });

        // Runs a sweep now and persists its report. `mode` overrides the stored
        // project mode for this run only; anything other than "reclaim"
        // normalizes to report-only, so a typo cannot start deleting.
        app.MapPost("/api/git/branch-sweep/run", (
            string project, string? mode, BranchSweepService sweep, CancellationToken ct) =>
        {
            var report = sweep.Run(project, mode, ct);
            return report.Error is null ? Results.Ok(report) : Results.BadRequest(new { error = report.Error });
        });

        // Deletes the operator-confirmed subset. Eligibility and tip are
        // re-verified against a fresh plan before each delete, and the refs go
        // out in batches of at most 100 per push.
        app.MapPost("/api/git/branch-sweep/execute", (
            string project, BranchSweepExecuteRequest req, BranchSweepService sweep, CancellationToken ct) =>
        {
            var result = sweep.Execute(project, req ?? new BranchSweepExecuteRequest([]), ct);
            return result.IsRepo
                ? Results.Ok(result)
                : Results.BadRequest(new { error = result.Error ?? "Could not run the branch sweep." });
        });

        app.MapGet("/api/git/branch-sweep/settings", (string project, BranchSweepService sweep) =>
            Results.Ok(new BranchSweepSettingsResponse(
                project,
                sweep.ResolveMode(project),
                sweep.ResolveWindows(project),
                BranchRetentionWindows.Default)));

        app.MapPut("/api/git/branch-sweep/settings", (
            string project,
            BranchSweepSettingsRequest req,
            ProjectSettingsService settings,
            BranchSweepService sweep) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "Project is required." });
            settings.SetBranchSweep(project, new BranchSweepSettings
            {
                Mode = req?.Mode,
                TaskRetentionDays = req?.TaskRetentionDays,
                SalvageRetentionDays = req?.SalvageRetentionDays,
                QuarantineRetentionDays = req?.QuarantineRetentionDays,
                AbandonedRetentionDays = req?.AbandonedRetentionDays,
            });
            return Results.Ok(new BranchSweepSettingsResponse(
                project,
                sweep.ResolveMode(project),
                sweep.ResolveWindows(project),
                BranchRetentionWindows.Default));
        });
    }
}
