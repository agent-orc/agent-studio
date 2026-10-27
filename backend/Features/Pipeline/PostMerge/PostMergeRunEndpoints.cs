using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public static class PostMergeRunEndpoints
{
    public static void MapPostMergeRunEndpoints(this WebApplication app)
    {
        app.MapGet("/api/develop-states", (HttpContext context, ProjectRegistry registry,
            PostMergeRunStore store) =>
        {
            var allowed = GateEconomicsEndpoints.AccessFilter(context, registry);
            return Results.Ok(registry.List().Where(project => !project.Archived
                && (allowed is null || allowed(project.DisplayName)))
                .Select(project =>
                {
                    var state = store.Read(project.Id);
                    return new DevelopStateSummary(project.DisplayName,
                        state.Runs.Count == 0 && state.ActiveRun is null ? null : state.DevelopState,
                        state.LastGreenSha);
                }).ToArray());
        });
        app.MapGet("/api/runner/post-merge/projects/{project}", (string project,
            ProjectRegistry registry, PostMergeRunStore store, ProjectSettingsService settings) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            var state = store.Read(record.Id);
            var projectSettings = settings.Get(record.DisplayName);
            if (IntegrationTestModes.Resolve(projectSettings) == IntegrationTestModes.PostMerge)
                state = state with
                {
                    WindowMaxMerges = projectSettings.BatchGate.MaximumSize,
                    WindowMinutes = Math.Max(0, projectSettings.BatchGate.LookAheadMinutes),
                };
            return Results.Ok(state);
        });
        app.MapPost("/api/runner/post-merge/projects/{project}/runs", (string project,
            PostMergeRunRecord run, ProjectRegistry registry, PostMergeRunStore store) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            if (run.Project != record.DisplayName || string.IsNullOrWhiteSpace(run.Id)
                || run.Id.Length > 80 || run.TipSha is null || run.TipSha.Length != 40
                || !run.TipSha.All(Uri.IsHexDigit) || run.MergeCount < 0
                || run.Kind is not ("window" or "nightly")
                || run.Status is not ("running" or "green" or "red" or "skipped")
                || run.Commands is null || run.Verdicts is null
                || run.Commands.Count > 64 || run.Verdicts.Count > 64)
                return Results.BadRequest(new { error = "invalid post-merge run" });
            var state = store.Record(record.Id, run);
            return state is null ? Results.Conflict(new { error = "post-merge run already claimed or stale" })
                : Results.Ok(state);
        });
    }
}

public sealed record DevelopStateSummary(string Project, string? DevelopState, string? LastGreenSha);
