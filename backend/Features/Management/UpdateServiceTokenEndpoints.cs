using AgentStudio.Security;

namespace AgentStudio.Management;

/// <summary>
/// Hands the local Update Service trigger token to the Update Center, which
/// sends it as <c>X-Update-Token</c> on trigger and rollback. The token is the
/// configured <c>UpdateService:TriggerToken</c> (or <c>ATP_UPDATE_TOKEN</c>),
/// otherwise the owner-only file the service generates on its first start.
/// Mapped in the local profile only: the checkout Update Service binds
/// loopback and belongs to a dev or Stable checkout. Applying an update is an
/// installation change, so the route declares the Owner role.
/// </summary>
public static class UpdateServiceTokenEndpoints
{
    public const string Route = "/api/update-service/token";
    public const string TokenKey = "UpdateService:TriggerToken";
    public const string TokenFileKey = "UpdateService:TriggerTokenFile";

    /// <summary>Same file name the Update Service writes beside its appsettings.json.</summary>
    public const string DefaultFileName = ".update-token";

    public static void MapUpdateServiceTokenEndpoints(this WebApplication app)
    {
        app.MapGet(Route, (HttpContext context, IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var token = ReadToken(configuration, environment.ContentRootPath);
            return token is null
                ? Results.Json(new { error = "update service token not found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(new { token });
        }).RequireStudioRole(StudioRoles.Owner);
    }

    public static string? ReadToken(IConfiguration configuration, string contentRoot)
    {
        var configured = (configuration[TokenKey] ?? Environment.GetEnvironmentVariable("ATP_UPDATE_TOKEN"))?.Trim();
        if (!string.IsNullOrEmpty(configured)) return configured;
        var path = TokenFilePath(configuration, contentRoot);
        if (!File.Exists(path)) return null;
        var stored = File.ReadAllText(path).Trim();
        return stored.Length == 0 ? null : stored;
    }

    /// <summary>
    /// The backend content root is <c>backend/</c> of the same checkout, so the
    /// default resolves to <c>update-service/.update-token</c> beside it.
    /// </summary>
    public static string TokenFilePath(IConfiguration configuration, string contentRoot)
    {
        var configured = configuration[TokenFileKey] ?? Environment.GetEnvironmentVariable("ATP_UPDATE_TOKEN_FILE");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "..", "update-service", DefaultFileName)
            : configured);
    }
}
