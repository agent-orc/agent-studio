using AgentStudio.Registry;
using AgentStudio.Security;
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
            ProjectRegistry registry, PostMergeRunStore store) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            return record is null ? Results.NotFound() : Results.Ok(store.Read(record.Id));
        });
        app.MapPost("/api/runner/post-merge/projects/{project}/escalation-check", (string project,
            ProjectRegistry registry, PostMergeIncidentService incidents) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            incidents.EscalateOverdue(record);
            return Results.NoContent();
        });
        app.MapPost("/api/runner/post-merge/projects/{project}/runs", (string project,
            PostMergeRunRecord run, ProjectRegistry registry, PostMergeRunStore store,
            PostMergeIncidentService incidents) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            if (run.Project != record.DisplayName || string.IsNullOrWhiteSpace(run.Id)
                || run.Id.Length > 80 || run.TipSha is null || run.TipSha.Length != 40
                || !run.TipSha.All(Uri.IsHexDigit) || run.MergeCount < 0
                || run.Kind is not ("window" or "nightly")
                || run.Status is not ("running" or "green" or "red" or "skipped")
                || run.Commands is null || run.Verdicts is null
                || run.Commands.Count > 64 || run.Verdicts.Count > 64
                || run.Verdicts.Any(verdict => verdict.OutputTail?.Length > 2000
                    || verdict.FailedTests?.Count > 64
                    || verdict.FailedTests?.Any(test => test.Length > 256) == true)
                || run.Failure is { } failure && (string.IsNullOrWhiteSpace(failure.Fingerprint)
                    || failure.Fingerprint.Length > 64 || failure.LogExcerpt is null
                    || failure.LogExcerpt.Length > 2000 || failure.FailingTests is null
                    || failure.FailingTests.Count > 64 || failure.FailingTests.Any(test => test.Length > 256)
                    || failure.FailingPaths is null
                    || failure.FailingPaths.Count > 128 || failure.SuspectMerges is null
                    || failure.SuspectMerges.Count > 512))
                return Results.BadRequest(new { error = "invalid post-merge run" });
            var safe = run with
            {
                Verdicts = run.Verdicts.Select(verdict => verdict with
                { OutputTail = verdict.OutputTail is null ? null : CredentialRedactor.Redact(verdict.OutputTail) }).ToArray(),
                Failure = run.Failure is null ? null : run.Failure with
                { LogExcerpt = CredentialRedactor.Redact(run.Failure.LogExcerpt) },
            };
            var state = store.Record(record.Id, safe);
            if (state is not null) incidents.Apply(record, safe);
            return state is null ? Results.Conflict(new { error = "post-merge run already claimed or stale" })
                : Results.Ok(state);
        });
    }
}

public sealed record DevelopStateSummary(string Project, string? DevelopState, string? LastGreenSha);
