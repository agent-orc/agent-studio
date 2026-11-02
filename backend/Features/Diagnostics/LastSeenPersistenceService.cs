using AgentStudio.Clients;
using AgentStudio.Security;

namespace AgentStudio.Diagnostics;

/// <summary>Coalesces identity and security last-seen writes away from API requests.</summary>
public sealed class LastSeenPersistenceService(
    ClientIdentityStore clients,
    AccessSecurityStore security,
    ILogger<LastSeenPersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) Flush();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Flush();
    }

    private void Flush()
    {
        try { clients.FlushLastSeen(); }
        catch (Exception ex) { logger.LogError(ex, "Client last-seen flush failed"); }
        try { security.FlushLastSeen(); }
        catch (Exception ex) { logger.LogError(ex, "Security last-seen flush failed"); }
    }
}
