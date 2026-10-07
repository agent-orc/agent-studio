using AgentStudio.Pipeline;

namespace AgentStudio.Tasks;

public sealed record ApplyProjectModelMigrationRequest(string? From);

public sealed record ApplyProjectModelMigrationResult(
    string From,
    string To,
    IReadOnlyList<string> UpdatedTaskIds,
    IReadOnlyList<string> FailedTaskIds);

/// <summary>
/// AGT-2903: project-wide acceptance of a catalogue migration proposal. The
/// operator accepts one proposal (for example gpt-5.6-sol -> gpt-6-sol) for
/// every eligible card of a project in one action; per-card acceptance stays
/// on <c>PUT /api/tasks/{id}/model</c>. Nothing here runs without that
/// explicit request, so a proposal-only migration never changes a pin silently.
/// </summary>
public static class ModelMigrationProjectEndpoints
{
    public static void MapModelMigrationProjectEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{project}/model-migrations/apply", (
            string project,
            ApplyProjectModelMigrationRequest request,
            ModelMigrationCatalogRegistry catalog,
            TaskScannerService scanner,
            TaskMutationService mutations,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var migration = catalog.FindMigration(request?.From);
            if (migration == null)
                return Results.BadRequest(new { error = $"No catalogue migration exists for '{request?.From}'." });
            var watchPath = TaskEndpointHelpers.ResolveWatchPath(projects, project, watchPath: null);
            if (string.IsNullOrWhiteSpace(watchPath))
                return Results.NotFound(new { error = $"Unknown project '{project}'." });

            var updated = new List<string>();
            var failed = new List<string>();
            foreach (var task in scanner.ScanAllJobs())
            {
                if (!string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ModelMigrationPolicy.AppliesToCardOnProjectAcceptance(
                        migration, task.State, task.ModelExplicit, task.Model)) continue;
                (mutations.SetJobModel(task.Id, migration.To, task.WatchPath) ? updated : failed).Add(task.Id);
            }

            return Results.Ok(new ApplyProjectModelMigrationResult(migration.From, migration.To, updated, failed));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }
}
