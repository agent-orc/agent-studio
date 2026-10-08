using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Metadata-only operation journal. Host adapters remain in their existing custody boundary.</summary>
public static class CredentialRunbookEndpoints
{
    public static void MapCredentialRunbookEndpoints(this WebApplication app)
    {
        var management = app.MapGroup("/api/v1/management/credential-runbooks")
            .RequireTaskServerScope(TaskServerScopes.Management);
        management.MapPost("", async (HttpContext context, BeginCredentialRunbookRequest request,
            TaskServerStore store, CancellationToken ct) =>
        {
            var actor = context.TaskServerPrincipal();
            if (actor is null) return Results.Forbid();
            return await TaskServerEndpoints.InvokeAsync(() => store.BeginCredentialRunbookAsync(
                request, actor.PrincipalId, ct), StatusCodes.Status201Created);
        });
        management.MapGet("/{operationId}", async (string operationId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetCredentialRunbookAsync(operationId, ct)));
        management.MapGet("/{operationId}/receipts", async (string operationId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.ListCredentialRunbookReceiptsAsync(operationId, ct)));
        management.MapPost("/{operationId}/human/{stepId}", async (
            HttpContext context, string operationId, string stepId, CompleteCredentialRunbookStepRequest request,
            TaskServerStore store, CancellationToken ct) =>
        {
            var actor = context.TaskServerPrincipal();
            if (actor is null) return Results.Forbid();
            return await TaskServerEndpoints.InvokeAsync(() => store.CompleteCredentialRunbookHumanStepAsync(
                operationId, stepId, request, actor.PrincipalId, ct));
        });

        var runner = app.MapGroup("/api/v1/runners/{runnerId}/credential-runbooks")
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
        runner.MapPost("/{operationId}/claim", async (HttpContext context, string runnerId,
            string operationId, string hostId, string expectedGeneration, string instanceId, TaskServerStore store,
            CancellationToken ct) =>
        {
            if (!await RunnerOwnsAsync(context, runnerId, operationId, instanceId, store, ct))
                return Results.Forbid();
            return await TaskServerEndpoints.InvokeAsync(() => store.ClaimCredentialRunbookStepAsync(
                operationId, hostId, expectedGeneration, ct));
        });
        runner.MapPost("/{operationId}/steps/{stepId}/complete", async (
            HttpContext context, string runnerId, string operationId, string stepId,
            CompleteCredentialRunbookStepRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!await RunnerOwnsAsync(context, runnerId, operationId, request.InstanceId, store, ct))
                return Results.Forbid();
            return await TaskServerEndpoints.InvokeAsync(() => store.CompleteCredentialRunbookStepAsync(
                operationId, stepId, request, runnerId, ct));
        });
    }

    private static async Task<bool> RunnerOwnsAsync(HttpContext context, string runnerId,
        string operationId, string? instanceId, TaskServerStore store, CancellationToken ct)
        => context.TaskServerPrincipal() is { Kind: TaskServerPrincipalKinds.Runner, RunnerId: { } bound }
           && bound == runnerId
           && await store.RunnerOwnsCredentialRunbookHostAsync(runnerId, operationId, instanceId, ct);
}
