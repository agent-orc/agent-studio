namespace AgentStudio.Runner;

/// <summary>
/// Operator gate for boot-time crash recovery. The boot sweep may discover
/// orphan working-tree changes, but commits happen only through these explicit
/// confirmation endpoints.
/// </summary>
public static class CrashRecoveryEndpoints
{
    public static void MapCrashRecoveryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/crash-recovery");

        group.MapGet("/pending", (CrashRecoveryService recovery) =>
        {
            return Results.Ok(new { pending = recovery.GetPendingOrphanRecoveries() });
        });

        group.MapPost("/pending/{id}/commit", (string id, bool? confirmLarge, CrashRecoveryService recovery) =>
        {
            var result = recovery.CommitPendingOrphanRecovery(id, confirmLarge == true);
            return result.Status switch
            {
                CrashRecoveryActionStatuses.NotFound => Results.NotFound(result),
                CrashRecoveryActionStatuses.Failed => Results.BadRequest(result),
                _ => Results.Ok(result),
            };
        });

        group.MapPost("/pending/{id}/dismiss", (string id, CrashRecoveryService recovery) =>
        {
            var result = recovery.DismissPendingOrphanRecovery(id);
            return result.Status == CrashRecoveryActionStatuses.NotFound
                ? Results.NotFound(result)
                : Results.Ok(result);
        });
    }
}
