using AgentStudio.Deploy;

namespace AgentStudio.Projects;

public static class HostedVolumeBackupEndpoints
{
    public static void MapHostedVolumeBackupEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectName}/environments/{environmentId}/volumes");
        group.MapGet("", async (string projectName, string environmentId,
            ProjectEnvironmentService environments, HostedVolumeBackupService backups,
            IHostedVolumeBackupEngine engine, CancellationToken ct) =>
        {
            var declaration = environments.DeclarationFiles(projectName)?
                .FirstOrDefault(item => item.EnvironmentId == environmentId)?.Declaration;
            if (declaration is null) return Results.NotFound();
            var state = await backups.ReadAsync(projectName, ct);
            return Results.Ok(new { declaration.Data.Database, declaration.Data.Volumes,
                Schedules = state?.Schedules.Where(item => item.Environment == environmentId).ToArray() ?? [],
                Audit = state?.Audit.Where(item => item.Environment == environmentId).TakeLast(100).ToArray() ?? [],
                AdapterReady = engine is not UnboundHostedVolumeBackupEngine });
        });

        group.MapPut("/{volume}/schedule", async (HttpContext context, string projectName, string environmentId,
            string volume, HostedVolumeScheduleRequest request, HostedVolumeBackupService backups, CancellationToken ct) =>
        {
            if (!DeployEndpoints.Actor(context, projectName).IsApprover) return ApprovalRequired();
            try { return Results.Ok(await backups.SetScheduleAsync(projectName, environmentId, volume,
                request.HourUtc, DeployEndpoints.Actor(context, projectName).Id, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        group.MapPost("/{volume}/backups", async (HttpContext context, string projectName, string environmentId,
            string volume, HostedVolumeBackupService backups, CancellationToken ct) =>
        {
            if (!DeployEndpoints.Actor(context, projectName).IsApprover) return ApprovalRequired();
            try { return Results.Ok(await backups.BackupAsync(projectName, environmentId, volume,
                DeployEndpoints.Actor(context, projectName).Id, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 503); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        group.MapPost("/{volume}/backups/{backupId}/restore", async (HttpContext context, string projectName,
            string environmentId, string volume, string backupId, HostedVolumeBackupService backups, CancellationToken ct) =>
        {
            if (!DeployEndpoints.Actor(context, projectName).IsApprover) return ApprovalRequired();
            try
            {
                await backups.RestoreAsync(projectName, environmentId, volume, backupId,
                    DeployEndpoints.Actor(context, projectName).Id, ct);
                return Results.Ok();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 503); }
            catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
    }

    private static IResult ApprovalRequired() => Results.Json(
        new { error = "Hosted volume changes require a project Owner or Maintainer." }, statusCode: 403);
}
