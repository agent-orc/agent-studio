using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Wraps the task-lifecycle and orchestrator-chat mutations on
/// <see cref="TaskServerStore"/> with a durable studio stream event, the
/// same layering <see cref="RetentionManagementService"/> uses over
/// <see cref="TaskServerStore"/>. The store commits the domain mutation
/// first; only once that succeeds does this coordinator append the
/// notification event and publish it live, so a subscriber never observes
/// an event for a mutation that did not happen.
/// </summary>
public sealed class StudioLifecycleCoordinator(TaskServerStore store, IStudioEventPublisher publisher)
{
    public async Task<MoveTaskResponse> MoveTaskAsync(
        string projectId, string taskIdentity, MoveTaskRequest request, string actorId, CancellationToken ct)
    {
        var response = await store.MoveTaskAsync(projectId, taskIdentity, request, actorId, ct);
        await PublishTaskEventAsync(
            StudioStreamEventKinds.TaskMoved, response.Task, ct,
            new { taskId = response.Task.TaskId, to = response.Task.State, response.Position });
        return response;
    }

    public async Task<MoveTaskResponse> MoveTaskToTopAsync(
        string projectId, string taskIdentity, string actorId, CancellationToken ct)
    {
        var response = await store.MoveTaskToTopAsync(projectId, taskIdentity, actorId, ct);
        await PublishTaskEventAsync(
            StudioStreamEventKinds.TaskMoved, response.Task, ct,
            new { taskId = response.Task.TaskId, response.Position });
        return response;
    }

    public async Task<TaskLifecycleResponse> StartTaskAsync(
        string projectId, string taskIdentity, StartTaskRequest request, string actorId, CancellationToken ct)
    {
        var response = await store.StartTaskAsync(projectId, taskIdentity, request, actorId, ct);
        await PublishTaskEventAsync(StudioStreamEventKinds.TaskStarted, response.Task, ct);
        return response;
    }

    public async Task<TaskLifecycleResponse> ContinueTaskAsync(
        string projectId, string taskIdentity, ContinueTaskRequest request, string actorId, CancellationToken ct)
    {
        var response = await store.ContinueTaskAsync(projectId, taskIdentity, request, actorId, ct);
        await PublishTaskEventAsync(StudioStreamEventKinds.TaskContinued, response.Task, ct);
        return response;
    }

    public async Task<TaskLifecycleResponse> StopTaskAsync(
        string projectId, string taskIdentity, StopTaskRequest request, string actorId, CancellationToken ct)
    {
        var response = await store.StopTaskAsync(projectId, taskIdentity, request, actorId, ct);
        await PublishTaskEventAsync(
            StudioStreamEventKinds.TaskStopped, response.Task, ct, new { taskId = response.Task.TaskId, response.Run?.RunId });
        return response;
    }

    public async Task DeleteTaskAsync(string projectId, string taskIdentity, string actorId, CancellationToken ct)
    {
        var existing = await store.GetTaskAsync(projectId, taskIdentity, ct)
            ?? throw new KeyNotFoundException("Task was not found.");
        await store.DeleteTaskAsync(projectId, taskIdentity, actorId, ct);
        var evt = await store.AppendStudioStreamEventAsync(
            StudioStreamEventKinds.TaskDeleted, existing.ProjectId, existing.TaskId,
            new { taskId = existing.TaskId }, ct);
        await publisher.PublishAsync(evt, ct);
    }

    public async Task<OrchestratorChatResponse> SendOrchestratorChatMessageAsync(
        string projectIdentity, StudioOrchestratorChatMessageRequest request, string actorId, CancellationToken ct)
    {
        var project = await store.RequireProjectAsync(projectIdentity, ct);
        var response = await store.SendOrchestratorChatMessageAsync(projectIdentity, request, actorId, ct);
        var evt = await store.AppendStudioStreamEventAsync(
            StudioStreamEventKinds.OrchestratorChatAppended, project.ProjectId, null,
            new { project.ProjectId, response.Turn.TurnId }, ct);
        await publisher.PublishAsync(evt, ct);
        return response;
    }

    private async Task PublishTaskEventAsync(string kind, TaskDto task, CancellationToken ct, object? payload = null)
    {
        var evt = await store.AppendStudioStreamEventAsync(
            kind, task.ProjectId, task.TaskId, payload ?? new { taskId = task.TaskId, state = task.State }, ct);
        await publisher.PublishAsync(evt, ct);
    }
}
