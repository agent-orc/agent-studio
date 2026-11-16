using AgentStudio.Security;

namespace AgentStudio.Deploy;

public sealed record DeployReporterTokenRequest(string? Name, DateTime? ExpiresAt);

public sealed record DeployReporterTokenResponse(string Id, string Name, IReadOnlyList<string> Scopes, DateTime CreatedAt,
    string? CreatedBy, DateTime? ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt)
{
    public static DeployReporterTokenResponse From(DeployReporterToken token)
        => new(token.Id, token.Name, [DeployReporterScopes.Report], token.CreatedAt, token.CreatedBy, token.ExpiresAt,
            token.LastUsedAt, token.RevokedAt);
}

/// <summary>The plaintext is returned once; store it in a file the deploy scripts read.</summary>
public sealed record DeployReporterTokenCreatedResponse(DeployReporterTokenResponse Token, string Secret);

/// <summary>
/// Instance deploy record API (AGT-3879). The report routes accept only a
/// deploy reporter token (<c>Authorization: Bearer dpr.…</c>), in every
/// security profile; a Studio session, an API token, or a Runner credential
/// grants nothing there. Issuing and revoking a reporter token is reserved to
/// the installation owner. The project read lives with the deploy routes.
/// </summary>
public static class InstanceDeployEndpoints
{
    public const string RunsPath = "/api/instance/deploy/runs";

    public static void MapInstanceDeployEndpoints(this WebApplication app)
    {
        app.MapPost(RunsPath, (HttpContext context, InstanceDeployStartRequest request,
            InstanceDeployService instance, AccessSecurityStore security) =>
            Reporter(context, security) is { } reporter
                ? Respond(instance.Start(request, reporter.Name)) : Unauthorized());

        app.MapPut(RunsPath + "/{runId}/steps/{stepName}", (HttpContext context, string runId, string stepName,
            InstanceDeployStepRequest request, InstanceDeployService instance, AccessSecurityStore security) =>
            Reporter(context, security) is not null
                ? Respond(instance.ReportStep(runId, stepName, request)) : Unauthorized());

        app.MapPost(RunsPath + "/{runId}/finish", (HttpContext context, string runId,
            InstanceDeployFinishRequest request, InstanceDeployService instance, AccessSecurityStore security) =>
            Reporter(context, security) is not null
                ? Respond(instance.Finish(runId, request)) : Unauthorized());

        app.MapGet("/api/projects/{projectName}/deploy/instance", (string projectName, InstanceDeployService instance) =>
            instance.View(projectName) is { } view ? Results.Ok(view)
                : Results.NotFound(new { error = $"'{projectName}' is not the source project of this Studio instance." }));

        var tokens = app.MapGroup("/api/instance/deploy/reporter-tokens");
        tokens.MapGet("", (AccessSecurityStore security) =>
            Results.Ok(security.ListDeployReporterTokens().Select(DeployReporterTokenResponse.From).ToArray()));
        tokens.MapPost("", (HttpContext context, DeployReporterTokenRequest? request, AccessSecurityStore security) =>
        {
            try
            {
                var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Id;
                var (token, secret) = security.CreateDeployReporterToken(request?.Name, request?.ExpiresAt, actor);
                return Results.Ok(new DeployReporterTokenCreatedResponse(DeployReporterTokenResponse.From(token), secret));
            }
            catch (SecurityOperationException ex)
            {
                return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status);
            }
        });
        tokens.MapDelete("/{tokenId}", (string tokenId, AccessSecurityStore security) =>
            security.RevokeDeployReporterToken(tokenId) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>Report routes the access middleware leaves to <see cref="Reporter"/>.</summary>
    public static bool IsReportRoute(string method, string path)
    {
        if (!path.StartsWith(RunsPath, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[RunsPath.Length..];
        if (rest.Length == 0) return HttpMethods.IsPost(method);
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length switch
        {
            2 => HttpMethods.IsPost(method) && segments[1].Equals("finish", StringComparison.OrdinalIgnoreCase),
            3 => HttpMethods.IsPut(method) && segments[1].Equals("steps", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static DeployReporterPrincipal? Reporter(HttpContext context, AccessSecurityStore security)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        const string scheme = "Bearer ";
        return header is not null && header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? security.AuthenticateDeployReporter(header[scheme.Length..].Trim()) : null;
    }

    private static IResult Unauthorized()
        => Results.Json(new { error = "deploy-reporter-token-required",
                message = "Send a deploy reporter token as the bearer credential." },
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Respond<T>(DeployOutcome<T> outcome) => outcome.Kind switch
    {
        DeployOutcomeKind.Ok => Results.Ok(outcome.Value),
        DeployOutcomeKind.NotFound => Results.NotFound(new { error = outcome.Error }),
        DeployOutcomeKind.Invalid => Results.BadRequest(new { error = outcome.Error }),
        DeployOutcomeKind.Forbidden => Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { error = outcome.Error }),
    };
}
