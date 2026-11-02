using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer.AttemptCredentials;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.TaskServer;

/// <summary>
/// Keeps the App connection in the encrypted root store. A Task Server without a
/// configured wrapping key cannot hold one and says so when connecting.
/// </summary>
public sealed class AttemptRootGitHubAppVault(IAttemptRootSecretStore roots) : IGitHubAppVault
{
    private const string ConnectionName = "github-app-connection";
    private const string SecretsName = "github-app-secrets";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GitHubAppConnection?> LoadConnectionAsync(CancellationToken ct)
        => await roots.ReadAsync(ConnectionName, ct) is { } text
            ? JsonSerializer.Deserialize<GitHubAppConnection>(text, Json)
            : null;

    public Task SaveConnectionAsync(GitHubAppConnection connection, CancellationToken ct)
        => roots.WriteAsync(ConnectionName, JsonSerializer.Serialize(connection, Json), ct);

    public Task DeleteConnectionAsync(CancellationToken ct) => roots.DeleteAsync(ConnectionName, ct);

    public async Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct)
        => await roots.ReadAsync(SecretsName, ct) is { } text
            ? JsonSerializer.Deserialize<GitHubAppSecrets>(text, Json)
            : null;

    public Task WriteSecretsAsync(GitHubAppSecrets secrets, string actor, CancellationToken ct)
        => roots.WriteAsync(SecretsName, JsonSerializer.Serialize(secrets, Json), ct);

    public Task DeleteSecretsAsync(string actor, CancellationToken ct) => roots.DeleteAsync(SecretsName, ct);

    public Task EnsureWritableAsync(CancellationToken ct) => roots.EnsureWritableAsync(ct);
}

/// <summary>The repository each project registered with this Task Server.</summary>
public sealed class TaskServerRepositoryCatalog(TaskServerStore store) : IRegisteredRepositoryCatalog
{
    public async Task<IReadOnlyList<RegisteredRepository>> ListAsync(CancellationToken ct)
    {
        var repositories = new List<RegisteredRepository>();
        foreach (var project in await store.ListProjectsAsync(null, ct))
        {
            var registration = await store.GetProjectRepositoryAsync(project.ProjectId, ct);
            repositories.Add(new RegisteredRepository(project.Name, registration?.RepositoryUrl));
        }
        return repositories;
    }
}

/// <summary>The same routes the monolith maps; Studio proxies them here when it uses this Task Server.</summary>
public static class GitHubAppEndpoints
{
    public static void MapGitHubAppEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/management/github-app")
            .RequireTaskServerScope(TaskServerScopes.Management);

        group.MapGet("", (bool? refresh, HttpContext context, GitHubAppBroker broker, CancellationToken ct)
            => Run(context, async () => Results.Ok(await broker.StatusAsync(refresh == true, ct))))
            .RequireStudioRole(StudioUserRoles.Owner);

        group.MapPost("/registration", (StartGitHubAppRegistrationRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.StartRegistrationAsync(request, ct))))
            .RequireStudioRole(StudioUserRoles.Owner);

        group.MapPost("/flow", (AdvanceGitHubAppFlowRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () =>
                Results.Ok(await broker.AdvanceAsync(request, TaskServerEndpoints.Actor(context), ct))))
            .RequireStudioRole(StudioUserRoles.Owner);

        group.MapPost("/installation", (StartGitHubAppStepRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.StartInstallationAsync(request, ct))))
            .RequireStudioRole(StudioUserRoles.Owner);

        group.MapPost("/authorization", (StartGitHubAppStepRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () => Results.Ok(await broker.StartAuthorizationAsync(request, ct))))
            .RequireStudioRole(StudioUserRoles.Owner);

        group.MapPut("/credentials", (ConnectExistingGitHubAppRequest request, HttpContext context, GitHubAppBroker broker,
            CancellationToken ct) => Run(context, async () =>
                Results.Ok(await broker.ConnectExistingAsync(request, TaskServerEndpoints.Actor(context), ct))))
            .RequireStudioRole(StudioUserRoles.Owner);

        group.MapDelete("", (HttpContext context, GitHubAppBroker broker, CancellationToken ct) => Run(context, async () =>
        {
            await broker.DisconnectAsync(TaskServerEndpoints.Actor(context), ct);
            return Results.NoContent();
        })).RequireStudioRole(StudioUserRoles.Owner);

        app.MapPost("/api/v1/runners/{runnerId}/repository-credentials", (string runnerId, RepositoryCredentialsRequest request,
            HttpContext context, GitHubAppBroker broker, TaskServerStore store, CancellationToken ct) => Run(context, async () =>
        {
            var access = RepositoryAccess.Normalize(request.Access);
            if (access is null)
                return Error(StatusCodes.Status400BadRequest, "invalid-access", "Access must be 'read' or 'write'.");
            if (context.TaskServerPrincipal() is not { Kind: TaskServerPrincipalKinds.Runner, RunnerId: { } bound } principal
                || !string.Equals(bound, runnerId, StringComparison.Ordinal))
                return Results.Forbid();
            // Every Runner principal may claim runs, so that scope says nothing about the
            // host's role. A review host's principal is enrolled without this one.
            if (access == RepositoryAccess.Write && !principal.Scopes.Contains(TaskServerScopes.RepositoriesWrite))
                return Error(StatusCodes.Status403Forbidden, "write-access-denied", "This host may only read repositories.");
            // SM-9: a host receives tokens only for the repositories of its bound projects.
            var binding = await RunnerRouteAuthorization.BindingAsync(context, principal, store);
            if (binding.Projects.IsEmpty) return Results.NoContent();
            var repositories = await store.RunnerRepositoryUrlsAsync(binding.Projects, ct);
            var credentials = await broker.IssueAllAsync(access, runnerId, ct,
                admits: repository => repository.RepositoryUrl is { } url && repositories.Contains(url));
            return credentials.Count == 0
                ? Results.NoContent()
                : Results.Ok(new RepositoryCredentialsResponse(credentials, GitHubAppBroker.HostRefreshSeconds));
        })).RequireAnyTaskServerScope(TaskServerScopes.RunsClaim, TaskServerScopes.ReviewsClaim);
    }

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
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException
                                              or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "root-store-unavailable",
                "The Task Server's encrypted root store is unavailable. Configure TaskServer:AttemptCredentialWrappingKeyFile.");
        }
    }

    private static IResult Error(int status, string code, string message)
        => Results.Json(new { error = code, message }, statusCode: status);
}
