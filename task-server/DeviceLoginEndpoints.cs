using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The only device sign-in routes. The first-run checklist and the Access and Execution
/// Hosts actions use the management group; every runner polls the runner group.
/// </summary>
public static class DeviceLoginEndpoints
{
    public static void MapDeviceLoginEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/device-logins")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapPost("", async (HttpContext context, StartDeviceLoginRequest request,
            DeviceLoginSessions sessions, TaskServerStore store, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await store.DeviceLoginHostExistsAsync(request.HostId, ct)) return Results.NotFound();
            try { return Results.Ok(sessions.Start(request.HostId, request.Provider,
                TaskServerEndpoints.Actor(context))); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-login", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ApiError("login-active", ex.Message)); }
        }).RequireStudioRole(StudioUserRoles.Operator);
        management.MapGet("/{handle}", (HttpContext context, string handle,
            DeviceLoginSessions sessions) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var key = context.Request.Headers["X-Device-Login-Session"].ToString();
            return sessions.Get(handle, key) is { } session ? Results.Ok(session) : Results.NotFound();
        }).RequireStudioRole(StudioUserRoles.Operator);
        management.MapPost("/{handle}/cancel", async (HttpContext context, string handle,
            DeviceLoginSessions sessions, CancellationToken ct) =>
        {
            var key = context.Request.Headers["X-Device-Login-Session"].ToString();
            return await sessions.CancelAsync(handle, key, ct) ? Results.Ok() : Results.NotFound();
        }).RequireStudioRole(StudioUserRoles.Operator);

        var runner = app.MapGroup("/api/v1/runners/{runnerId}/device-login")
            .RequireTaskServerScope(TaskServerScopes.RunsClaim);
        runner.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var runnerId = context.Request.RouteValues["runnerId"]?.ToString();
            var principal = context.TaskServerPrincipal();
            if (principal is not { Kind: TaskServerPrincipalKinds.Runner, RunnerId: { } boundRunner }
                || !string.Equals(boundRunner, runnerId, StringComparison.Ordinal))
                return Results.Forbid();
            return await next(invocation);
        });
        runner.MapPost("/claim", async (string runnerId, TaskServerStore store,
            DeviceLoginSessions sessions, CancellationToken ct) =>
        {
            var host = await store.ResolveDeviceLoginHostAsync(runnerId, ct);
            return host is null ? Results.NotFound() : Results.Ok(sessions.Claim(host, runnerId));
        });
        runner.MapPost("/{handle}/instructions", (string runnerId, string handle,
            DeviceLoginInstructionsRequest request, DeviceLoginSessions sessions) =>
        {
            try { return sessions.Instructions(handle, runnerId, request) ? Results.Ok() : Results.Conflict(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-instructions", ex.Message)); }
        });
        runner.MapPost("/{handle}/completion", async (string runnerId, string handle,
            DeviceLoginCompletionRequest request, DeviceLoginSessions sessions, CancellationToken ct) =>
            await sessions.CompleteAsync(handle, runnerId, request, ct) ? Results.Ok() : Results.Conflict());
    }
}
