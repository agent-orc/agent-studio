using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The G7 "task run/attempt history" P1 bundle: task run/attempt history
/// projections, mostly read-only reshapes of durable state already exposed
/// by the P0 core-attach bundle (<see cref="TaskServerStore.GetTaskHistoryAsync"/>,
/// <see cref="TaskServerStore.ListAttemptsAsync"/>). Routed under the same
/// unscoped-project-aware task group shape as
/// <c>StudioEndpoints.MapTaskLifecycleEndpoints</c>: every handler resolves
/// the task via <see cref="TaskServerStore.GetTaskAsync"/> first so the
/// connector's reserved unscoped-project literal ("-") keeps working.
/// </summary>
internal static class StudioTaskHistoryEndpoints
{
    internal static void MapStudioTaskHistoryEndpoints(this WebApplication app)
    {
        var tasks = app.MapGroup("/api/v1/projects/{projectId}/tasks/{taskIdentity}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        tasks.MapGet("/agent-work-detail", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetAgentWorkDetailAsync(task.ProjectId, task.TaskId, ct))));

        tasks.MapGet("/agent-work-summary", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetAgentWorkSummaryAsync(task.ProjectId, task.TaskId, ct))));

        tasks.MapGet("/claude/session-info", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetClaudeSessionInfoAsync(task.TaskId, ct))));

        tasks.MapGet("/plan", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetTaskPlanAsync(task.TaskId, ct))));

        tasks.MapGet("/runs", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.ListAttemptsAsync(task.ProjectId, task.TaskId, ct))));

        tasks.MapGet("/runs/{runIndex:int}/commits", async (
            string projectId, string taskIdentity, int runIndex, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeNullableAsync(
                    () => store.GetRunCommitsAsync(task.ProjectId, task.TaskId, runIndex, ct))));

        tasks.MapGet("/runs/{runIndex:int}/context", async (
            string projectId, string taskIdentity, int runIndex, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeNullableAsync(
                    () => store.GetRunContextAsync(task.ProjectId, task.TaskId, runIndex, ct))));

        tasks.MapGet("/runs/{runIndex:int}/diff", async (
            string projectId, string taskIdentity, int runIndex, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeNullableAsync(
                    () => store.GetRunDiffAsync(task.ProjectId, task.TaskId, runIndex, ct))));

        tasks.MapGet("/runs/{runIndex:int}/files", async (
            string projectId, string taskIdentity, int runIndex, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeNullableAsync(
                    () => store.GetRunFilesAsync(task.ProjectId, task.TaskId, runIndex, ct))));

        tasks.MapGet("/session-events", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetSessionEventsAsync(task.ProjectId, task.TaskId, ct))));

        tasks.MapGet("/step-prompts", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetStepPromptsAsync(task.ProjectId, ct))));

        tasks.MapGet("/timeline", async (
            string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await WithTaskAsync(store, projectId, taskIdentity, ct, task
                => TaskServerEndpoints.InvokeAsync(() => store.GetTaskTimelineAsync(task.ProjectId, task.TaskId, ct))));
    }

    /// <summary>
    /// Resolves the task via <see cref="TaskServerStore.GetTaskAsync"/>
    /// first (handling the connector's unscoped-project literal), then
    /// dispatches to <paramref name="handler"/> with the resolved
    /// <see cref="TaskDto"/> so downstream store calls use the task's real
    /// <c>ProjectId</c>/<c>TaskId</c> instead of re-deriving them.
    /// </summary>
    private static async Task<IResult> WithTaskAsync(
        TaskServerStore store,
        string projectId,
        string taskIdentity,
        CancellationToken ct,
        Func<TaskDto, Task<IResult>> handler)
    {
        try
        {
            var task = await store.GetTaskAsync(projectId, taskIdentity, ct);
            if (task is null) return Results.NotFound(new ApiError("not-found", "Task was not found."));
            return await handler(task);
        }
        catch (Exception exception)
        {
            return TaskServerEndpoints.MapError(exception);
        }
    }
}
