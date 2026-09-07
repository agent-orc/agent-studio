namespace AgentStudio.ModelMigrations;

public static class ModelMigrationEndpoints
{
    public static void MapModelMigrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/model-migrations", async (
            string? project,
            string? taskId,
            string? workspaceId,
            ModelMigrationService migrations,
            CancellationToken ct) =>
            Results.Ok(await migrations.GetStatusAsync(project, taskId, workspaceId, ct)));

        app.MapPost("/api/model-migrations/apply", async (
            ApplyModelMigrationRequest request,
            ModelMigrationService migrations,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await migrations.ApplyAsync(request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ModelMigrationConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPut("/api/model-migrations/auto-apply", (
            SetAutoApplyModelMigrationsRequest request,
            ModelMigrationService migrations) =>
        {
            try
            {
                var enabled = migrations.SetAutoApply(
                    request.Enabled,
                    request.WorkspaceId,
                    out var workspaceId);
                return Results.Ok(new
                {
                    autoApplySafe = enabled,
                    workspaceId,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }
}
