using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.HostSetup;

/// <summary>
/// The monolith's device sign-in and Git setup routes, with the standalone Task
/// Server's paths and wire shapes. The Credentials page and the first-run
/// checklist start a session through the Owner management routes; the claiming
/// host polls the runner routes with its enrolled credential. Without these
/// routes a networked install could not reach a host at all: the monolith
/// answered every host poll with 401, later with 404 route-not-mapped (AGT-3432).
/// With a standalone Task Server the same paths are proxied there.
/// </summary>
public static class HostSetupEndpoints
{
    public static void MapHostSetupEndpoints(this WebApplication app)
    {
        // The public demo signs in no provider and configures no repository.
        if (SecurityProfiles.IsPublicDemo(app.Configuration)) return;
        MapDeviceLogins(app);
        MapGitSetups(app);
    }

    private static void MapDeviceLogins(WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/device-logins").RequireStudioRole(StudioRoles.Owner);
        management.MapPost("", (HttpContext context, StartDeviceLoginRequest request,
            DeviceLoginSessions sessions, V1ReviewExecutorRegistry hosts) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!hosts.HasHost(request.HostId)) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(request.TargetRunnerId)
                && hosts.HostIdOf(request.TargetRunnerId) != request.HostId)
                return Results.BadRequest(new ApiError("invalid-login",
                    "The selected runner is not an active runner of this host."));
            try { return Results.Ok(sessions.Start(request.HostId, request.Provider, Actor(context), request.TargetRunnerId)); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-login", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ApiError("login-active", ex.Message)); }
        });
        management.MapGet("/{handle}", (HttpContext context, string handle, DeviceLoginSessions sessions) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var key = context.Request.Headers["X-Device-Login-Session"].ToString();
            return sessions.Get(handle, key) is { } session ? Results.Ok(session) : Results.NotFound();
        });
        management.MapPost("/{handle}/cancel", async (HttpContext context, string handle,
            DeviceLoginSessions sessions, CancellationToken ct) =>
        {
            var key = context.Request.Headers["X-Device-Login-Session"].ToString();
            return await sessions.CancelAsync(handle, key, ct) ? Results.Ok() : Results.NotFound();
        });
        management.MapPost("/{handle}/code", (HttpContext context, string handle,
            DeviceLoginCodeRequest request, DeviceLoginSessions sessions) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var key = context.Request.Headers["X-Device-Login-Session"].ToString();
            try { return sessions.SubmitCode(handle, key, request) ? Results.Ok() : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-code", ex.Message)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new ApiError("code-not-expected", ex.Message)); }
        });

        // AccessSecurityMiddleware binds the path's runner id to the credential and checks its scope.
        var runner = app.MapGroup("/api/v1/runners/{runnerId}/device-login");
        runner.MapPost("/claim", (HttpContext context, string runnerId,
            DeviceLoginSessions sessions, V1ReviewExecutorRegistry hosts) =>
        {
            if (!RunnerMatches(context, runnerId)) return Results.Forbid();
            // An unregistered identity has no host to sign in yet; it registers on start.
            return hosts.HostIdOf(runnerId) is { } host && sessions.Claim(host, runnerId) is { } claim
                ? Results.Ok(claim) : Results.NoContent();
        });
        runner.MapGet("/{handle}", (HttpContext context, string runnerId, string handle, DeviceLoginSessions sessions)
            => !RunnerMatches(context, runnerId) ? Results.Forbid()
                : sessions.RunnerState(handle, runnerId) is { } state
                    ? Results.Ok(new DeviceLoginRunnerStateDto(state)) : Results.NotFound());
        runner.MapPost("/{handle}/instructions", (HttpContext context, string runnerId, string handle,
            DeviceLoginInstructionsRequest request, DeviceLoginSessions sessions) =>
        {
            if (!RunnerMatches(context, runnerId)) return Results.Forbid();
            try { return sessions.Instructions(handle, runnerId, request) ? Results.Ok() : Results.Conflict(); }
            catch (ArgumentException ex) { return Results.BadRequest(new ApiError("invalid-instructions", ex.Message)); }
        });
        runner.MapPost("/{handle}/code", (HttpContext context, string runnerId, string handle,
            DeviceLoginSessions sessions) =>
        {
            if (!RunnerMatches(context, runnerId)) return Results.Forbid();
            context.Response.Headers.CacheControl = "no-store";
            if (!sessions.TakeCode(handle, runnerId, out var code)) return Results.NotFound();
            return code is null ? Results.NoContent() : Results.Ok(new DeviceLoginCodeDto(code));
        });
        runner.MapPost("/{handle}/completion", async (HttpContext context, string runnerId, string handle,
            DeviceLoginCompletionRequest request, DeviceLoginSessions sessions, CancellationToken ct)
            => !RunnerMatches(context, runnerId) ? Results.Forbid()
                : await sessions.CompleteAsync(handle, runnerId, request, ct) ? Results.Ok() : Results.Conflict());
    }

    private static void MapGitSetups(WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/runner-git-setups").RequireStudioRole(StudioRoles.Owner);
        management.MapPost("", (HttpContext context, StartRunnerGitSetupRequest request, RunnerGitSetupStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Json(store.Start(request), statusCode: StatusCodes.Status201Created); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        management.MapGet("/{id}", (string id, RunnerGitSetupStore store)
            => store.Get(id) is { } setup ? Results.Ok(setup) : Results.NotFound());
        management.MapPost("/{id}/verify", (string id, RunnerGitSetupStore store)
            => store.Verify(id) ? Results.Accepted() : Results.Conflict());

        var runners = app.MapGroup("/api/v1/runners/{runnerId}/git-setups");
        runners.MapPost("/claim", (HttpContext context, string runnerId, RunnerGitSetupStore store)
            => !RunnerMatches(context, runnerId) ? Results.Forbid()
                : store.Claim(runnerId) is { } work ? Results.Ok(work) : Results.NoContent());
        runners.MapPost("/{id}/key", (HttpContext context, string runnerId, string id,
            ReportRunnerGitKeyRequest request, RunnerGitSetupStore store) =>
        {
            if (!RunnerMatches(context, runnerId)) return Results.Forbid();
            try { return store.Key(runnerId, id, request.PublicKey) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        // The host reports either step on one path shape: /{id}/configured or /{id}/proof.
        runners.MapPost("/{id}/{step}", (HttpContext context, string runnerId, string id, string step,
            ReportRunnerGitProofRequest request, RunnerGitSetupStore store) =>
        {
            if (!RunnerMatches(context, runnerId)) return Results.Forbid();
            var recorded = step switch
            {
                "configured" => store.Configured(runnerId, id, request.FetchVerified),
                "proof" => store.Proof(runnerId, id, request),
                _ => false,
            };
            return recorded ? Results.NoContent() : Results.NotFound();
        });
    }

    /// <summary>
    /// The networked profile admits only the path's own runner credential. The
    /// local profile has no runner credential and trusts its loopback callers.
    /// </summary>
    private static bool RunnerMatches(HttpContext context, string runnerId)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal
           || string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal);

    private static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            ? human.User.Id : AgentStudio.Shared.DefaultClientIdentity.Id;
}
