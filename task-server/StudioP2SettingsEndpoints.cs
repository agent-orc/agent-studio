using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Group G4 of the P2 "operations and insight" bundle: global CLI/quota
/// settings, the crash-recovery operator queue, and watch paths. None of
/// these routes are project-scoped.
/// </summary>
public static class StudioP2SettingsEndpoints
{
    public static void MapStudioP2SettingsEndpoints(this WebApplication app)
    {
        var cli = app.MapGroup("/api/v1/studio/cli")
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        cli.MapPut("/model-routing/economy-mode", async (
            HttpContext context, SetEconomyModeRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetEconomyModeAsync(request, TaskServerEndpoints.Actor(context), ct)));
        cli.MapPut("/quota/caps", async (
            HttpContext context, SetQuotaCapsRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetQuotaCapsAsync(request, TaskServerEndpoints.Actor(context), ct)));
        cli.MapPut("/quota/model-routes", async (
            HttpContext context, SetQuotaModelRoutesRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetQuotaModelRoutesAsync(request, TaskServerEndpoints.Actor(context), ct)));
        cli.MapPut("/quota/wait-policy", async (
            HttpContext context, SetCliQuotaWaitPolicyRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetQuotaWaitPolicyAsync(request, TaskServerEndpoints.Actor(context), ct)));

        var crashRecovery = app.MapGroup("/api/v1/studio/crash-recovery");
        crashRecovery.MapGet("/pending", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListCrashRecoveryPendingAsync(ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        crashRecovery.MapPost("/pending/{id}/commit", async (
            HttpContext context, string id, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CommitCrashRecoveryPendingAsync(id, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        crashRecovery.MapPost("/pending/{id}/dismiss", async (
            HttpContext context, string id, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.DismissCrashRecoveryPendingAsync(id, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        var watchPaths = app.MapGroup("/api/v1/studio/watch-paths");
        watchPaths.MapPost("", async (
            HttpContext context, CreateWatchPathRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CreateWatchPathAsync(request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
        watchPaths.MapDelete("/{name}", async (
            HttpContext context, string name, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(async () =>
            {
                await store.DeleteWatchPathAsync(name, TaskServerEndpoints.Actor(context), ct);
                return new { deleted = true, name };
            }))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }
}
