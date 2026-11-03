namespace AgentStudio.Security;

/// <summary>
/// The scheduled chain verification (security model SM-1). It recomputes the
/// stored chain, checks it against the highest sequence this process wrote and
/// the recorded forward position, and compares every forwarded entry with the
/// off-host sink. A changed, deleted, inserted, or reordered entry, a sink that
/// lacks a forwarded entry, or a sink entry that differs raises an acute alert.
/// Runs every <c>ControlPlaneAudit:VerifyIntervalMinutes</c> (default 15) and
/// on demand from the activity view.
/// </summary>
public sealed class ControlPlaneAuditVerifier(
    ControlPlaneAuditLog log,
    ControlPlaneAuditMonitor monitor,
    IControlPlaneAuditSink? sink,
    TimeProvider? clock = null,
    IConfiguration? configuration = null,
    ILogger<ControlPlaneAuditVerifier>? logger = null) : BackgroundService
{
    private const int SinkPage = 1000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _single = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration?.GetValue<double?>("ControlPlaneAudit:VerifyIntervalMinutes") ?? 15, 0.1, 24 * 60));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await VerifyAsync(stoppingToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger?.LogError(exception, "Control plane audit verification failed to run");
                }
                await Task.Delay(interval, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger?.LogDebug("Control plane audit verification stopped");
        }
    }

    public async Task<ControlPlaneAuditIntegrity> VerifyAsync(CancellationToken ct)
    {
        await _single.WaitAsync(ct);
        try
        {
            var result = await CheckAsync(ct);
            monitor.RecordIntegrity(result);
            if (result.State == ControlPlaneAuditIntegrityStates.Failed)
                await monitor.RaiseAsync(result.Problem switch
                {
                    "sink-gap" => ControlPlaneAuditAlertCodes.SinkGap,
                    "sink-mismatch" => ControlPlaneAuditAlertCodes.SinkMismatch,
                    _ => ControlPlaneAuditAlertCodes.ChainBroken,
                }, result.Message ?? "The audit chain does not verify.", result.ProblemSequence, ct);
            else
            {
                monitor.Clear(ControlPlaneAuditAlertCodes.ChainBroken);
                monitor.Clear(ControlPlaneAuditAlertCodes.SinkGap);
                if (result.SinkCompared) monitor.Clear(ControlPlaneAuditAlertCodes.SinkMismatch);
            }
            return monitor.Integrity with { Alert = result.State == ControlPlaneAuditIntegrityStates.Failed };
        }
        finally
        {
            _single.Release();
        }
    }

    private async Task<ControlPlaneAuditIntegrity> CheckAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var chain = log.VerifyChain();
        ControlPlaneAuditIntegrity Failed(string problem, long? sequence, string message, bool sinkCompared = false)
            => new(ControlPlaneAuditIntegrityStates.Failed, now, chain.VerifiedThrough, message, true, sinkCompared, problem, sequence);

        if (!chain.Ok) return Failed(chain.Problem!, chain.ProblemSequence, chain.Message!);
        var stored = chain.VerifiedThrough;
        if (log.HighestWritten > stored)
            return Failed("truncated", stored + 1,
                $"Entries after sequence {stored} were removed; this process wrote up to {log.HighestWritten}.");
        var position = log.ReadForwardPosition();
        if (position.Sequence > stored)
            return Failed("truncated", stored + 1,
                $"Entries after sequence {stored} were removed; {position.Sequence} were already forwarded.");
        if (position.Sequence > 0 && chain.Hashes[(int)position.Sequence - 1] != position.Hash)
            return Failed("modified", position.Sequence, $"Entry {position.Sequence} differs from the one forwarded to the sink.");

        if (sink is null)
            return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
                "Local chain verified; no off-host sink is configured.", false);

        long next = 1;
        try
        {
            while (true)
            {
                var page = await sink.ReadAsync(next, SinkPage, ct);
                if (page.Count == 0) break;
                foreach (var line in page)
                {
                    var entry = ControlPlaneAuditLog.Parse(line);
                    if (entry is null || !ControlPlaneAuditLog.TrySplit(line, out var body, out var hash)
                        || ControlPlaneAuditLog.Sha256Hex(body) != hash)
                        return Failed("sink-mismatch", next, $"The sink holds an unreadable or altered entry after sequence {next - 1}.", true);
                    if (entry.Sequence > next && entry.Sequence <= position.Sequence)
                        return Failed("sink-gap", next, $"The sink lacks forwarded entry {next}.", true);
                    if (entry.Sequence < next) continue;
                    if (entry.Sequence > stored)
                        return Failed("truncated", stored + 1,
                            $"The sink holds entry {entry.Sequence}, which the local chain no longer has.", true);
                    if (chain.Hashes[(int)entry.Sequence - 1] != hash)
                        return Failed("sink-mismatch", entry.Sequence, $"Entry {entry.Sequence} differs between the local chain and the sink.", true);
                    next = entry.Sequence + 1;
                }
                if (page.Count < SinkPage) break;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
                "Local chain verified; the sink could not be read: " + CredentialRedactor.Redact(exception.Message), false);
        }
        if (next <= position.Sequence)
            return Failed("sink-gap", next, $"The sink lacks forwarded entries from {next} to {position.Sequence}.", true);
        return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
            $"Local chain verified and matches the sink through entry {next - 1}.", false, SinkCompared: true);
    }
}
