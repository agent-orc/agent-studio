using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class RunnerGitSetupEndpoints
{
    public static void MapRunnerGitSetupEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/runner-git-setups")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapPost("", (HttpContext context, StartRunnerGitSetupRequest request,
            RunnerGitSetupStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Json(store.Start(request), statusCode: StatusCodes.Status201Created); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireStudioRole(StudioUserRoles.Owner);
        management.MapGet("/{id}", (string id, RunnerGitSetupStore store)
            => store.Get(id) is { } setup ? Results.Ok(setup) : Results.NotFound())
            .RequireStudioRole(StudioUserRoles.Owner);
        management.MapPost("/{id}/verify", (string id, RunnerGitSetupStore store)
            => store.Verify(id) ? Results.Accepted() : Results.Conflict())
            .RequireStudioRole(StudioUserRoles.Owner);

        var runners = app.MapGroup("/api/v1/runners/{runnerId}/git-setups")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        runners.MapPost("/claim", (string runnerId, HttpContext context, RunnerGitSetupStore store)
            => Bound(context, runnerId)
                ? store.Claim(runnerId) is { } work ? Results.Ok(work) : Results.NoContent()
                : Results.Forbid());
        runners.MapPost("/{id}/key", (string runnerId, string id, HttpContext context,
            ReportRunnerGitKeyRequest request, RunnerGitSetupStore store) =>
        {
            if (!Bound(context, runnerId)) return Results.Forbid();
            try { return store.Key(runnerId, id, request.PublicKey) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        runners.MapPost("/{id}/configured", (string runnerId, string id, HttpContext context,
            ReportRunnerGitProofRequest request, RunnerGitSetupStore store)
            => Bound(context, runnerId)
                ? store.Configured(runnerId, id, request.FetchVerified) ? Results.NoContent() : Results.NotFound()
                : Results.Forbid());
        runners.MapPost("/{id}/proof", (string runnerId, string id, HttpContext context,
            ReportRunnerGitProofRequest request, RunnerGitSetupStore store)
            => Bound(context, runnerId)
                ? store.Proof(runnerId, id, request) ? Results.NoContent() : Results.NotFound()
                : Results.Forbid());
    }

    private static bool Bound(HttpContext context, string runnerId)
        => context.TaskServerPrincipal() is { } principal
            && principal.Kind == TaskServerPrincipalKinds.Runner
            && principal.RunnerId == runnerId;
}
