using AgentStudio.Diagnostics;

namespace AgentStudio.Bus;

/// <summary>Seeds workspace participants without letting writes outlive the host.</summary>
public sealed class AgentMessageBusSeedHostedService(
    AgentMessageBusBridge bridge,
    CrashRecorder crashRecorder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await bridge.SeedBuiltInParticipantsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            crashRecorder.Record("AgentMessageBusBridge.Seed", ex);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
        // Finish the active participant file before host services are disposed.
        => base.StopAsync(CancellationToken.None);
}
