namespace AgentStudio.Pipeline;

/// <summary>
/// Repairs startup integration custody before repository admission (AGT-4115).
///
/// <para>
/// Merge and push callers await an explicit repository barrier. Failed
/// repositories remain held and are retried independently of ready repositories.
/// </para>
/// </summary>
public sealed class InterruptedIntegrationGateRecoveryHostedService : BackgroundService
{
    internal Func<CancellationToken, Task> RetryDelayAsync { get; set; }
        = ct => Task.Delay(TimeSpan.FromSeconds(30), ct);
    private readonly TaskScannerService _scanner;
    private readonly InterruptedIntegrationGateRecoveryService _recovery;
    private readonly ILogger<InterruptedIntegrationGateRecoveryHostedService> _logger;

    public InterruptedIntegrationGateRecoveryHostedService(
        TaskScannerService scanner,
        InterruptedIntegrationGateRecoveryService recovery,
        ILogger<InterruptedIntegrationGateRecoveryHostedService> logger)
    {
        _scanner = scanner;
        _recovery = recovery.RequireStartupRecovery();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Admission is already closed by repair worker construction. Yield only
        // keeps disk and Git work off the host startup path.
        await Task.Yield();
        try
        {
            await _scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var report = _recovery.RunOnce(stoppingToken);
                    if (report.Branches > 0)
                        _logger.LogWarning(
                            "interrupted-integration-gate startup recovery branches={Branches} rolledBack={RolledBack} requeued={Requeued} escalated={Escalated}",
                            report.Branches, report.RolledBack, report.Requeued, report.Escalated);
                    if (!_recovery.HasPendingStartupRecovery) return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "integration-recovery-held startup inventory or repair failed; repair will retry");
                }
                await RetryDelayAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "interrupted-integration-gate recovery stopped with the host");
        }
    }
}
