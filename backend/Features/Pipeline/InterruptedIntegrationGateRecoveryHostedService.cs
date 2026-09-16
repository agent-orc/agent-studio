namespace AgentStudio.Pipeline;

/// <summary>
/// Runs <see cref="InterruptedIntegrationGateRecoveryService"/> once per process
/// start (AGT-2849).
///
/// <para>
/// The timing is the whole point. An un-gated merge can only be rolled back
/// while nothing has been merged on top of it, so the repair has to happen
/// before the first new delivery enters the merge gate. It therefore runs once,
/// early, and then stops: from that moment on every merge either reaches a
/// verdict or leaves a fresh journal entry for the next start.
/// </para>
/// </summary>
public sealed class InterruptedIntegrationGateRecoveryHostedService : BackgroundService
{
    private readonly InterruptedIntegrationGateRecoveryService _recovery;
    private readonly ILogger<InterruptedIntegrationGateRecoveryHostedService> _logger;

    public InterruptedIntegrationGateRecoveryHostedService(
        InterruptedIntegrationGateRecoveryService recovery,
        ILogger<InterruptedIntegrationGateRecoveryHostedService> logger)
    {
        _recovery = recovery;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep the disk walk and the git reads off the host startup path without
        // giving up the ordering: the merge gate is only reachable through the
        // queues and sweeps that start after this yield.
        await Task.Yield();
        try
        {
            var report = _recovery.RunOnce(stoppingToken);
            if (report.Branches > 0)
            {
                _logger.LogWarning(
                    "interrupted-integration-gate startup recovery repaired branches={Branches} rolledBack={RolledBack} requeued={Requeued} escalated={Escalated}",
                    report.Branches,
                    report.RolledBack,
                    report.Requeued,
                    report.Escalated);
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "interrupted-integration-gate recovery stopped with the host");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "interrupted-integration-gate startup recovery failed");
        }
    }
}
