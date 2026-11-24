using AgentStudio.Registry;
using AgentStudio.Tasks;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public static class PostMergeRunEndpoints
{
    public static void MapPostMergeRunEndpoints(this WebApplication app)
    {
        app.MapGet("/api/develop-states", (HttpContext context, ProjectRegistry registry,
            PostMergeRunStore store, TimeProvider clock) =>
        {
            var allowed = GateEconomicsEndpoints.AccessFilter(context, registry);
            return Results.Ok(registry.List().Where(project => !project.Archived
                && (allowed is null || allowed(project.DisplayName)))
                .Select(project =>
                {
                    var state = store.Read(project.Id);
                    return new DevelopStateSummary(project.DisplayName,
                        state.Runs.Count == 0 && state.ActiveRun is null ? null : state.DevelopState,
                        state.LastGreenSha, state.PendingWindow, state.FlakyCount,
                        (state.FlakyHistory ?? []).Where(item => item.AtUtc >= clock.GetUtcNow().AddDays(-14)
                            && item.AtUtc <= clock.GetUtcNow())
                            .GroupBy(item => item.Test, StringComparer.Ordinal)
                            .Select(group => new PostMergeFlakyTest(group.Key,
                                group.Select(item => item.RunId).Distinct().Count(), group.Max(item => item.AtUtc)))
                            .OrderByDescending(item => item.Runs).ThenBy(item => item.Test, StringComparer.Ordinal)
                            .ToArray());
                }).ToArray());
        });
        app.MapGet("/api/runner/post-merge/projects", (ProjectRegistry registry,
            ProjectSettingsService settings) => Results.Ok(registry.List()
            .Where(project => !project.Archived
                && IntegrationTestModes.Resolve(settings.Get(project.DisplayName)) == IntegrationTestModes.PostMerge)
            .Select(project => (Project: project, Repository: RemoteProjectRepositoryResolver.Resolve(
                project, settings.Get(project.DisplayName).IntegrationBranch)))
            .Where(item => item.Repository is not null)
            .Select(item => new PostMergeProjectTarget(item.Project.Id, item.Project.DisplayName,
                item.Repository!.RepositoryUrl, item.Repository.DefaultBranch)).ToArray()));
        app.MapPost("/api/runner/post-merge/projects/{project}/window", (string project,
            PostMergeWindowObservation observation, ProjectRegistry registry, PostMergeRunStore store) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            if (observation.TipSha is not { Length: 40 } || !observation.TipSha.All(Uri.IsHexDigit)
                || observation.WindowMinutes < 0 || observation.AdmissionReason?.Length > 256)
                return Results.BadRequest(new { error = "invalid post-merge window observation" });
            store.ObserveWindow(record.Id, observation);
            return Results.NoContent();
        });
        app.MapGet("/api/runner/post-merge/projects/{project}", (string project,
            ProjectRegistry registry, PostMergeRunStore store, ProjectSettingsService settings) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            var state = store.Read(record.Id);
            var projectSettings = settings.Get(record.DisplayName);
            var strategy = TestingStrategies.Resolve(projectSettings);
            if (TestingStrategies.ExpectsPostMergeRuns(strategy))
                state = state with
                {
                    WindowMaxMerges = projectSettings.BatchGate.MaximumSize,
                    WindowMinutes = Math.Max(0, projectSettings.BatchGate.LookAheadMinutes),
                    // AGT-3965: fast selects the affected routine suites, balanced every routine suite.
                    WindowSuites = TestingStrategies.WindowSuites(strategy),
                };
            return Results.Ok(state);
        });
        app.MapPost("/api/runner/post-merge/projects/{project}/escalation-check", (string project,
            ProjectRegistry registry, PostMergeIncidentService incidents) =>
        {
            var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
            if (record is null) return Results.NotFound();
            incidents.AskOwnerWhenOverdue(record);
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
                || run.StartOccupiedSlots < 0
                || run.StartLoadPerCore is { } load && (!double.IsFinite(load) || load < 0)
                || run.Commands.Count > 64 || run.Verdicts.Count > 64
                || run.Verdicts.Any(verdict => verdict.OutputTail?.Length > 2000
                    || verdict.FlakyTests?.Count > 64
                    || verdict.FlakyTests?.Any(test => string.IsNullOrWhiteSpace(test) || test.Length > 256) == true
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

public sealed record DevelopStateSummary(string Project, string? DevelopState, string? LastGreenSha,
    PostMergeWindowObservation? PendingWindow = null, int FlakyCount = 0,
    IReadOnlyList<PostMergeFlakyTest>? MostFlakyTests14Days = null);
