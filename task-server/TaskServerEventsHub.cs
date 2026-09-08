using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.TaskServer;

public sealed class TaskServerEventsHub : Hub;

public sealed record TaskServerOperationalEvent(
    string Kind,
    DateTime OccurredAt,
    string ActorId,
    object Payload);

public interface ITaskServerEventPublisher
{
    Task PublishAsync(TaskServerOperationalEvent message, CancellationToken ct);
}

public sealed class SignalRTaskServerEventPublisher(IHubContext<TaskServerEventsHub> hub) : ITaskServerEventPublisher
{
    public Task PublishAsync(TaskServerOperationalEvent message, CancellationToken ct)
        => hub.Clients.All.SendAsync("taskServerEvent", message, ct);
}
