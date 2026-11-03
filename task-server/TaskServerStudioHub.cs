using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.TaskServer;

/// <summary>
/// The replayable Studio event stream at <c>/hubs/v1/studio</c>. A client
/// that supplies <c>?cursor=</c> on connect receives every missed event
/// before any live event, so reconnecting after a Studio-detached period
/// catches up deterministically instead of relying on a full board re-pull.
/// </summary>
public sealed class TaskServerStudioHub(TaskServerStore store) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var sessionToken = Context.GetHttpContext() is { } http ? StudioEndpoints.StudioSessionToken(http) : null;
        if (sessionToken is not null)
        {
            var status = await store.GetStudioAuthStatusAsync(sessionToken, Context.ConnectionAborted);
            if (!status.Authenticated || status.SecondFactorEnrollmentRequired)
            {
                Context.Abort();
                return;
            }
            _ = MonitorSessionAsync(Context, sessionToken);
        }
        var cursorText = Context.GetHttpContext()?.Request.Query["cursor"].FirstOrDefault();
        if (long.TryParse(cursorText, out var cursor) && cursor >= 0)
        {
            var missed = await store.ListStudioStreamEventsSinceAsync(cursor, Context.ConnectionAborted);
            foreach (var item in missed)
                await Clients.Caller.SendAsync("studioEvent", item, Context.ConnectionAborted);
        }
        await base.OnConnectedAsync();
    }

    private async Task MonitorSessionAsync(HubCallerContext connection, string sessionToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(connection.ConnectionAborted))
            {
                var status = await store.GetStudioAuthStatusAsync(sessionToken, connection.ConnectionAborted, touch: false);
                if (status.Authenticated && !status.SecondFactorEnrollmentRequired) continue;
                connection.Abort();
                return;
            }
        }
        catch (OperationCanceledException) when (connection.ConnectionAborted.IsCancellationRequested) { }
        catch { connection.Abort(); }
    }
}

public interface IStudioEventPublisher
{
    Task PublishAsync(StudioStreamEventDto message, CancellationToken ct);
}

public sealed class SignalRStudioEventPublisher(IHubContext<TaskServerStudioHub> hub) : IStudioEventPublisher
{
    public Task PublishAsync(StudioStreamEventDto message, CancellationToken ct)
        => hub.Clients.All.SendAsync("studioEvent", message, ct);
}
