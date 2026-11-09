namespace AgentStudio.Management;

/// <summary>
/// Runs the backup schedule inside the product: after a startup delay it
/// checks once a minute whether a backup, an off-host upload retry or the
/// daily restore verification is due (<see cref="BackupSchedulePolicy"/>).
/// Without a configured backup key every tick is a no-op and the Operations
/// page shows backups as off.
/// </summary>
public sealed class BackupHostedService(
    StudioBackupService backups, TimeProvider clock, ILogger<BackupHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(backups.Options.StartupDelay, clock, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await backups.RunDueAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("backup-schedule-tick-failed {Reason}", ex.Message);
                }
                await Task.Delay(Tick, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "Backup schedule stopped with the host");
        }
    }
}
