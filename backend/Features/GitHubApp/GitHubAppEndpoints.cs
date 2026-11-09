using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

/// <summary>
/// The monolith's GitHub App routes. With a standalone Task Server the same paths
/// are proxied there, so the settings page calls one surface in every profile.
/// </summary>
public static class GitHubAppEndpoints
{
    private const string LocalOperator = "local-operator";

    public static void MapGitHubAppEndpoints(this WebApplication app)
    {
        // The public demo has no repositories to reach and no owner to connect them.
        if (SecurityProfiles.IsPublicDemo(app.Configuration)) return;
        var group = app.MapGroup("/api/v1/management/github-app").RequireStudioRole(StudioRoles.Owner);

        app.MapGet("/api/v1/management/credential-inventory", (HttpContext context, GitHubAppBroker broker,
            GitHubAppHostKeys hostKeys, ISecretProvider secrets, AccessSecurityStore security,
            AgentStudio.Runner.V1ReviewExecutorRegistry hosts, AgentStudio.Clients.ClientIdentityStore clients,
            TimeProvider clock, CancellationToken ct) => Run(context, async () => Results.Ok(await LocalCredentialInventory.ReadAsync(
                broker, hostKeys, secrets, security, hosts, clients, clock.GetUtcNow().UtcDateTime, ct))))
            .RequireStudioRole(StudioRoles.Owner);

        group.MapGet("", (bool? refresh, HttpContext context, GitHubAppBroker broker, CancellationToken ct)
            => Run(context, async () => Results.Ok(await broker.StatusAsync(refresh == true, ct))));

        group.MapPost("/registration", (StartGitHubAppRegistrationRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.StartRegistrationAsync(request, ct))));

        group.MapPost("/flow", (AdvanceGitHubAppFlowRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.AdvanceAsync(request, Actor(context), ct))));

        group.MapPost("/installation", (StartGitHubAppStepRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.StartInstallationAsync(request, ct))));

        group.MapPost("/authorization", (StartGitHubAppStepRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.StartAuthorizationAsync(request, ct))));

        group.MapPut("/credentials", (ConnectExistingGitHubAppRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.ConnectExistingAsync(request, Actor(context), ct))));

        group.MapDelete("", (HttpContext context, GitHubAppBroker broker, CancellationToken ct) => Run(context, async () =>
        {
            await broker.DisconnectAsync(Actor(context), ct);
            return Results.NoContent();
        }));

        app.MapPost("/api/v1/runners/{runnerId}/repository-credentials", (string runnerId, RepositoryCredentialsRequest request,
            HttpContext context, GitHubAppBroker broker, GitHubAppHostKeys hostKeys, AccessSecurityStore security,
            IConfiguration configuration, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) => Run(context, async () =>
        {
            var access = RepositoryAccess.Normalize(request.Access);
            if (access is null)
                return Error(StatusCodes.Status400BadRequest, "invalid-access", "Access must be 'read' or 'write'.");
            // Fail closed in every profile, as for secret values: the local API has
            // no sign-in, so only a host key or a service credential gets a token.
            var granted = Granted(context, runnerId, security, hostKeys, configuration);
            if (granted is null)
                return Error(StatusCodes.Status401Unauthorized, "host-key-required", "A host key or Runner service credential is required.");
            if (access == RepositoryAccess.Write && granted != RepositoryAccess.Write)
                return Error(StatusCodes.Status403Forbidden, "write-access-denied", "This host may only read repositories.");
            // SM-9: a runner credential receives tokens only for the repositories of its bound projects.
            var runner = SecurityProfiles.IsLocal(configuration) ? null : BoundRunner(context, security);
            var credentials = await broker.IssueAllAsync(access, runnerId, ct,
                admits: runner is null ? null : repository => RunnerProjectBinding.Allows(runner, repository.Project, projects));
            return credentials.Count == 0
                ? Results.NoContent()
                : Results.Ok(new RepositoryCredentialsResponse(credentials, GitHubAppBroker.HostRefreshSeconds));
        }));
    }

    /// <summary>
    /// What the presented credential grants this host. A credential of another host
    /// grants nothing. Locally only a host key counts: anyone who reaches the open
    /// API could mint a Runner credential, but nobody can place a key on this machine.
    /// </summary>
    private static string? Granted(HttpContext context, string runnerId, AccessSecurityStore security,
        GitHubAppHostKeys hostKeys, IConfiguration configuration)
    {
        var bearer = ReadBearer(context);
        if (SecurityProfiles.IsLocal(configuration)) return hostKeys.Granted(runnerId, bearer);
        var runner = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal
                     ?? security.AuthenticateRunner(bearer);
        if (runner is null || !string.Equals(runner.RunnerId, runnerId, StringComparison.Ordinal)) return null;
        if (runner.Scopes.Contains(RunnerScopes.RepositoryWrite)) return RepositoryAccess.Write;
        return runner.Scopes.Contains(RunnerScopes.RepositoryRead) ? RepositoryAccess.Read : null;
    }

    private static RunnerPrincipal? BoundRunner(HttpContext context, AccessSecurityStore security)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal
           ?? security.AuthenticateRunner(ReadBearer(context));

    private static string? ReadBearer(HttpContext context)
        => context.Request.Headers.Authorization.FirstOrDefault() is { } header
           && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;

    private static string Actor(HttpContext context)
        => AccessSecurityMiddleware.ActorId(context, LocalOperator);

    private static async Task<IResult> Run(HttpContext context, Func<Task<IResult>> operation)
    {
        // Every answer here can hold tokens or describes who may get one.
        context.Response.Headers.CacheControl = "no-store";
        try { return await operation(); }
        catch (GitHubAppException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
        catch (HttpRequestException) { return Error(StatusCodes.Status502BadGateway, "github-unreachable", "GitHub could not be reached."); }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Error(StatusCodes.Status504GatewayTimeout, "github-timeout", "GitHub did not answer in time.");
        }
        catch (SecretAccessDeniedException exception) { return Error(StatusCodes.Status403Forbidden, exception.Code, exception.Message); }
        catch (SecretKeyUnavailableException exception) { return Error(StatusCodes.Status503ServiceUnavailable, "secret-key-unavailable", exception.Message); }
        catch (SecretBackendException exception) { return Error(StatusCodes.Status503ServiceUnavailable, exception.Code, exception.Message); }
    }

    private static IResult Error(int status, string code, string message)
        => Results.Json(new { error = code, message }, statusCode: status);
}
