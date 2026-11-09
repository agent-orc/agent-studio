namespace AgentStudio.Security;

/// <summary>
/// The read-only installation security view (AGT-3575). The Operations page
/// and operator agents read the same answer; it carries metadata only.
/// </summary>
public static class SecurityPostureEndpoints
{
    public const string Route = "/api/security/posture";

    public static IServiceCollection AddSecurityPosture(this IServiceCollection services)
        => services.AddSingleton<SecurityPostureService>();

    public static void MapSecurityPostureEndpoints(this WebApplication app)
    {
        // The public demo has no users, hosts, or credentials to report.
        if (SecurityProfiles.IsPublicDemo(app.Configuration)) return;
        app.MapGet(Route, async (HttpContext context, SecurityPostureService posture, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await posture.ReadAsync(ct));
        }).RequirePermission(IamPermissions.SecurityPostureRead);
    }
}
