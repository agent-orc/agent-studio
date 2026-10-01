using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// I05 routes (docs/deployment-story/index.html, D5 option A). Identity
/// administration and project registration require an owner human session
/// on every authenticated installation, in addition to the edge principal's
/// scope. <c>X-Client-Id</c> and <c>X-Actor-Id</c> are never consulted here:
/// actors come from the resolved session or principal only.
/// </summary>
internal static class IdentityBootstrapEndpoints
{
    public static void MapIdentityBootstrapEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // Open: lets a host confirm which installation it is about to join.
        api.MapGet("/installation", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetInstallationIdentityAsync(ct)));

        // Open: the one-time enrolment code is the only credential.
        api.MapPost("/enrolments/exchange", async (
            ExchangeEnrolmentRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ExchangeEnrolmentAsync(request, ct), StatusCodes.Status201Created));

        MapHumanIdentityEndpoints(app);
        MapEnrolmentEndpoints(api);
        MapProjectRegistryEndpoints(api);
    }

    private static void MapHumanIdentityEndpoints(WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/studio/auth");

        auth.MapPost("/recover", async (
            HttpContext context, StudioRecoverRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.RecoverStudioUserAsync(request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        auth.MapPost("/recovery-code", async (
            HttpContext context, StudioReissueRecoveryCodeRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () => new
            {
                recoveryCode = await store.ReissueStudioRecoveryCodeAsync(
                    StudioEndpoints.StudioSessionToken(context), request, ct),
            }))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        auth.MapGet("/users", async (
            HttpContext context, TaskServerStore store, TaskServerBootstrapOptions bootstrap, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await RequireOwnerAsync(context, store, bootstrap, ct);
                return await store.ListStudioUsersAsync(ct);
            }))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        auth.MapPost("/users", async (
            HttpContext context, StudioCreateUserRequest request, TaskServerStore store,
            TaskServerBootstrapOptions bootstrap, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var owner = await RequireOwnerAsync(context, store, bootstrap, ct);
                return await store.CreateStudioUserAsync(request, ActorOf(context, owner), ct);
            }, StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapEnrolmentEndpoints(RouteGroupBuilder api)
    {
        var enrolments = api.MapGroup("/management/enrolments")
            .RequireTaskServerScope(TaskServerScopes.Management);

        enrolments.MapGet("", async (
            HttpContext context, TaskServerStore store, TaskServerBootstrapOptions bootstrap, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await RequireOwnerAsync(context, store, bootstrap, ct);
                return await store.ListEnrolmentsAsync(ct);
            }));

        enrolments.MapPost("", async (
            HttpContext context, CreateEnrolmentRequest request, TaskServerStore store,
            TaskServerBootstrapOptions bootstrap, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var owner = await RequireOwnerAsync(context, store, bootstrap, ct);
                return await store.CreateEnrolmentAsync(request, ActorOf(context, owner), ct);
            }, StatusCodes.Status201Created));

        enrolments.MapPost("/{enrolmentId}/revoke", async (
            HttpContext context, string enrolmentId, TaskServerStore store,
            TaskServerBootstrapOptions bootstrap, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var owner = await RequireOwnerAsync(context, store, bootstrap, ct);
                await store.RevokeEnrolmentAsync(enrolmentId, ActorOf(context, owner), ct);
                return new { enrolmentId, revoked = true };
            }));
    }

    private static void MapProjectRegistryEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/projects/registrations", async (
            HttpContext context, RegisterProjectRepositoryRequest request, TaskServerStore store,
            TaskServerBootstrapOptions bootstrap, CancellationToken ct) =>
        {
            try
            {
                var owner = await RequireOwnerAsync(context, store, bootstrap, ct);
                var (registration, created) = await store.RegisterProjectRepositoryAsync(
                    request, ActorOf(context, owner), ct);
                return created
                    ? Results.Json(registration, statusCode: StatusCodes.Status201Created)
                    : Results.Ok(registration);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        }).RequireTaskServerScope(TaskServerScopes.Management);

        api.MapGet("/projects/{projectId}/repository", async (
            string projectId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(async () =>
            {
                var registration = await store.GetProjectRepositoryAsync(projectId, ct);
                return registration is null
                    ? null
                    : new ProjectRepositoryStatusDto(
                        registration, await store.ListProjectRepositoryProbesAsync(projectId, ct));
            }))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        // The runner id comes from the route; the authentication middleware
        // binds a runner principal to its own route runner id.
        api.MapPost("/runners/{runnerId}/project-probes/{projectId}", async (
            string runnerId, string projectId, ProjectRepositoryProbeRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RecordProjectRepositoryProbeAsync(projectId, runnerId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
    }

    /// <summary>
    /// Authenticated installations always require an owner session. A
    /// loopback AUTH=none development store accepts the call without a
    /// session, but a presented non-owner session is still denied.
    /// </summary>
    private static async Task<StudioAuthUserDto?> RequireOwnerAsync(
        HttpContext context, TaskServerStore store, TaskServerBootstrapOptions bootstrap, CancellationToken ct)
    {
        var token = StudioEndpoints.StudioSessionToken(context);
        if (token is null && !bootstrap.RequiresAuthentication) return null;
        return await store.RequireStudioOwnerAsync(token, ct);
    }

    private static string ActorOf(HttpContext context, StudioAuthUserDto? owner)
        => owner?.UserId
           ?? context.TaskServerPrincipal()?.PrincipalId
           ?? "local-loopback";
}
