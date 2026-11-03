namespace AgentStudio.Security;

/// <summary>
/// Copies every control plane audit entry to the off-host sink and records the
/// forwarded position (security model SM-1). It wakes on each append, so an
/// entry normally leaves within a second. The local chain is the queue: the
/// position advances only after the sink acknowledged a batch, so an
/// unreachable sink loses nothing; once the oldest unforwarded entry is older
/// than <c>ControlPlaneAudit:Sink:AlertAfterSeconds</c> (default 60) an acute
/// alert is raised, and it clears when the sink has caught up.
/// </summary>
public sealed class AuditSinkForwarder(
    ControlPlaneAuditLog log,
    ControlPlaneAuditMonitor monitor,
    IControlPlaneAuditSink? sink,
    TimeProvider? clock = null,
    IConfiguration? configuration = null,
    ILogger<AuditSinkForwarder>? logger = null) : BackgroundService
{
    public const int BatchSize = 500;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(Math.Clamp(
        configuration?.GetValue<double?>("ControlPlaneAudit:Sink:PollSeconds") ?? 5, 0.05, 300));
    private readonly TimeSpan _alertAfter = TimeSpan.FromSeconds(Math.Clamp(
        configuration?.GetValue<double?>("ControlPlaneAudit:Sink:AlertAfterSeconds") ?? 60, 1, 3600));
    private int _failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sink is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ForwardPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger?.LogError(exception, "Control plane audit forwarding failed unexpectedly");
            }
            try
            {
                if (_failures == 0)
                    await log.WaitForAppendAsync(_poll, stoppingToken);
                else
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, _poll.TotalSeconds * Math.Pow(2, Math.Min(_failures, 10)))),
                        _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends every entry after the recorded position, in batches. Returns the
    /// number forwarded; a sink failure leaves the position where it was.
    /// </summary>
    public async Task<int> ForwardPendingAsync(CancellationToken ct)
    {
        if (sink is null) return 0;
        var forwarded = 0;
        while (true)
        {
            var position = log.ReadForwardPosition();
            var pending = log.Read(position.Sequence + 1, BatchSize);
            if (pending.Count == 0)
            {
                Recovered();
                return forwarded;
            }
            if (pending[0].PreviousHash != position.Hash)
            {
                // The stored chain no longer continues from what the sink holds.
                await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.SinkMismatch,
                    $"Entry {pending[0].Sequence} does not continue the chain already forwarded to the sink.",
                    pending[0].Sequence, ct);
                return forwarded;
            }
            try
            {
                await sink.AppendAsync(pending, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _failures++;
                var oldest = pending[0].At;
                monitor.RecordForwardFailure(CredentialRedactor.Redact(exception.Message), oldest);
                logger?.LogWarning(exception, "Audit sink refused or did not answer; {Count} entries stay queued from {Sequence}",
                    pending.Count, pending[0].Sequence);
                if (_clock.GetUtcNow().UtcDateTime - oldest >= _alertAfter)
                    await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.SinkUnreachable,
                        $"The audit sink has not accepted entries since sequence {pending[0].Sequence}; they stay queued locally.",
                        pending[0].Sequence, ct);
                return forwarded;
            }
            var last = pending[^1];
            log.WriteForwardPosition(new ControlPlaneAuditForwardPosition(last.Sequence, last.Hash, _clock.GetUtcNow().UtcDateTime));
            forwarded += pending.Count;
            _failures = 0;
            monitor.RecordForwardProgress(null);
        }
    }

    private void Recovered()
    {
        _failures = 0;
        monitor.RecordForwardProgress(null);
        monitor.Clear(ControlPlaneAuditAlertCodes.SinkUnreachable);
    }
}
