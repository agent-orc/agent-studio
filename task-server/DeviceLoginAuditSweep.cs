using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class DeviceLoginAuditSweep(DeviceLoginSessions sessions, TimeProvider clock,
    ILogger<DeviceLoginAuditSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await sessions.SweepAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Device login timeout audit failed; retrying next sweep.");
            }
        }
    }
}
