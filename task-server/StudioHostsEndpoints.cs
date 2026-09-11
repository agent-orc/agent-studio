using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio P1 "hosts" bundle (G1_Hosts): remote-host ("client") lifecycle and
/// management-command routes. Mirrors the route-group/scope style of
/// <c>StudioEndpoints.MapRunnerEndpoints</c>/<c>MapTaskLifecycleEndpoints</c>
/// and the existing <c>/api/v1/hosts/{hostId}/runtime-capacity</c> and
/// <c>/api/v1/hosts/{hostId}/project-policy</c> routes in
/// <c>TaskServerEndpoints.cs</c>.
/// </summary>
internal static class StudioHostsEndpoints
{
    internal static void MapStudioHostsEndpoints(this WebApplication app)
    {
        var clients = app.MapGroup("/api/v1/studio/clients")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        clients.MapGet("", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListStudioClientsAsync(ct)));

        clients.MapGet("/{clientId}/defaults", async (
            string clientId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetStudioHostDefaultsAsync(clientId, ct)));

        clients.MapPut("/{clientId}/defaults", async (
            HttpContext context, string clientId, UpdateStudioHostDefaultsRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.UpdateStudioHostDefaultsAsync(clientId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapPost("/{clientId}/drain", async (
            HttpContext context, string clientId, StudioHostDrainRequest request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RequestOperatorHostDrainAsync(
                    clientId, new OperatorHostDrainRequest(request.Reason), TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapDelete("/{clientId}/permanent", async (
            HttpContext context, string clientId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.PermanentlyDeleteStudioHostAsync(clientId, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapPost("/{clientId}/retire", async (
            HttpContext context, string clientId, StudioHostRetireRequest? request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RetireStudioHostAsync(clientId, request?.Reason, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapPost("/{clientId}/revive", async (
            HttpContext context, string clientId, StudioHostReviveRequest? request,
            TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ReviveStudioHostAsync(clientId, request?.Reason, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapPut("/{clientId}/runner-capacity", async (
            HttpContext context, string clientId, UpdateRuntimeCapacitySettingsRequest request,
            RuntimeCapacitySettingsService capacity, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => capacity.UpdateAsync(clientId, request, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapPost("/{clientId}/runner-project-preflights/invalidate", async (
            HttpContext context, string clientId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.InvalidateStudioHostPreflightCacheAsync(clientId, TaskServerEndpoints.Actor(context), ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        clients.MapGet("/{clientId}/telemetry", async (
            string clientId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetStudioHostTelemetryAsync(clientId, ct)));

        var management = app.MapGroup("/api/v1/management")
            .RequireTaskServerScope(TaskServerScopes.Management);

        management.MapPost("/commands", async (
            HttpContext context, StudioManagementCommandRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!StudioManagementCommands.Known.Contains(request.Command))
                return Results.Json(
                    new ApiError("unknown-management-command", $"Unknown management command '{request.Command}'."),
                    statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var result = await store.DispatchStudioManagementCommandAsync(
                    request, TaskServerEndpoints.Actor(context), ct);
                return Results.Ok(result);
            }
            catch (Exception exception)
            {
                return TaskServerEndpoints.MapError(exception);
            }
        });

        management.MapPost("/remote-hosts/provider-auth", async (
            HttpContext context, StudioProviderAuthEventRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.RecordStudioProviderAuthEventAsync(request, TaskServerEndpoints.Actor(context), ct),
                StatusCodes.Status201Created));
    }
}
