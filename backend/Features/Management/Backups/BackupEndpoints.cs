using AgentStudio.Security;

namespace AgentStudio.Management;

/// <summary>
/// Backup state for operator agents (AGT-3574). Operator credentials never
/// reach the Task Server plane under <c>/api/v1</c>, so the same overview as
/// <c>GET /api/v1/management/backups</c> is readable here; <c>studio-op
/// backups</c> prints it. A caller without installation-wide operate
/// permission does not see where the off-host copies go, the key
/// fingerprints, or host paths in error text (AGT-3737).
/// </summary>
public static class BackupEndpoints
{
    public static void MapOperatorBackupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operator/backups", (HttpContext context, StudioBackupService backups) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var overview = backups.Overview();
            var scoped = context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
                         && !access.Allows(IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide);
            return Results.Ok(scoped ? Scoped(overview) : overview);
        }).RequirePermission(IamPermissions.ProjectRead);
    }

    /// <summary>The overview for a reader without installation-wide operate permission.</summary>
    public static BackupOverview Scoped(BackupOverview overview) => overview with
    {
        KeyId = null,
        SecretKeyId = null,
        LastRun = overview.LastRun is { } run ? run with { Error = BackupAudit.WithoutHostPaths(run.Error) } : null,
        LastVerification = overview.LastVerification is { } verification
            ? verification with { Detail = BackupAudit.WithoutHostPaths(verification.Detail) }
            : null,
        OffHost = overview.OffHost with { Target = null, LastError = BackupAudit.WithoutHostPaths(overview.OffHost.LastError) },
        Warnings = overview.Warnings.Select(warning => warning with { Message = BackupAudit.WithoutHostPaths(warning.Message)! }).ToArray(),
    };
}
