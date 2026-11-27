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
                }, result.Message ?? "The audit chain does not verify.", result.ProblemSequence, ct, result.Occurrence, result.OccurredAt);
            else
            {
                // A recovery anchor verifies subsequent entries; it does not repair lost history.
                if (log.LatestRecoveryAnchor is null)
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

        ControlPlaneAuditIntegrity LocalBreak(string problem, long sequence, string message, long witnessedThrough = 0,
            bool establishAnchor = true, string? recoveryMessage = null)
        {
            var through = Math.Max(witnessedThrough,
                Math.Max(chain.ObservedThrough, Math.Max(chain.HighestWritten, chain.ForwardPosition.Sequence)));
            var detectedAt = _clock.GetUtcNow().UtcDateTime;
            var basis = $"{log.LatestRecoveryAnchor?.Hash ?? ControlPlaneAuditLog.GenesisHash}|{sequence}|";
            var open = monitor.IsOpen(ControlPlaneAuditAlertCodes.ChainBroken)
                ? monitor.Raised.LastOrDefault(alert => alert.Code == ControlPlaneAuditAlertCodes.ChainBroken) : null;
            var continuing = open?.Occurrence?.StartsWith(basis, StringComparison.Ordinal) == true;
            var occurrence = continuing ? open!.Occurrence! : basis + Guid.NewGuid().ToString("N");
            var firstObservedAt = continuing ? open!.OccurredAt ?? detectedAt : detectedAt;
            ControlPlaneAuditIntegrity Report(string description, bool pending = false)
                => Failed(problem, sequence, description) with
                {
                    Occurrence = occurrence, OccurredAt = firstObservedAt,
                    RecoveryPending = pending, RecoveryThrough = witnessedThrough, RecoveryMessage = recoveryMessage ?? message,
                };
            var evidence = $"{message} Sequence range {sequence} to {Math.Max(sequence, through)}; "
                + $"detected at {detectedAt:O}; process {ControlPlaneAuditLog.ProcessIdentity}.";
            if (!establishAnchor) return Report(evidence + " Recovery waits until the sink tail can be established.", pending: true);
            try
            {
                var anchored = log.TryAnchorBreak(chain with
                {
                    ProblemSequence = sequence, HighestWritten = Math.Max(chain.HighestWritten, witnessedThrough),
                }, evidence, occurrence, firstObservedAt);
                return Report(evidence + (anchored
                    ? " A new recovery anchor seals the damaged history; subsequent entries can be verified."
                    : " The file prefix changed during recovery; anchoring will be retried at the next verification."), pending: !anchored);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Report(evidence + " Recovery anchor could not be written: "
                    + CredentialRedactor.Redact(exception.Message), pending: true);
            }
        }
        async Task<ControlPlaneAuditIntegrity> RecoverAsync(string problem, long sequence, string message, long knownThrough = 0)
        {
            var through = Math.Max(Math.Max(knownThrough, sequence),
                Math.Max(chain.ObservedThrough, Math.Max(chain.HighestWritten, chain.ForwardPosition.Sequence)));
            if (sink is not null)
            {
                var pending = LocalBreak(problem, sequence, message, through, establishAnchor: false);
                monitor.RecordIntegrity(pending);
                await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken, pending.Message!, sequence, ct,
                    pending.Occurrence, pending.OccurredAt);
                try
                {
                    var pages = 0;
                    while (true)
                    {
                        var previousThrough = through;
                        var tail = await sink.ReadAsync(through + 1, SinkPage, ct);
                        foreach (var raw in tail)
                        {
                            var witness = ControlPlaneAuditLog.Parse(raw);
                            if (witness is null || !ControlPlaneAuditLog.TrySplit(raw, out var body, out var hash)
                                || ControlPlaneAuditLog.Sha256Hex(body) != hash)
                                return LocalBreak(problem, sequence, message + " The sink tail contains an altered entry.",
                                    through, establishAnchor: false, recoveryMessage: message);
                            through = Math.Max(through, witness.Sequence);
                        }
                        if (tail.Count < SinkPage) break;
                        if (through <= previousThrough)
                            return LocalBreak(problem, sequence, message + " The sink tail did not advance.",
                                through, establishAnchor: false, recoveryMessage: message);
                        if (++pages >= 64)
                            return LocalBreak(problem, sequence, message + " The sink tail did not finish within 64 pages.",
                                through, establishAnchor: false, recoveryMessage: message);
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
                {
                    if (ct.IsCancellationRequested) throw;
                    return LocalBreak(problem, sequence,
                        message + " The sink tail could not be read: " + CredentialRedactor.Redact(exception.Message),
                        through, establishAnchor: false, recoveryMessage: message);
                }
            }
            return LocalBreak(problem, sequence, message, through);
        }

        var prior = monitor.Integrity;
        if (prior.RecoveryPending && prior.ProblemSequence is { } pendingSequence)
        {
            var earlier = chain.ProblemSequence is { } currentSequence && currentSequence < pendingSequence;
            return await RecoverAsync(earlier ? chain.Problem! : prior.Problem!,
                earlier ? chain.ProblemSequence!.Value : pendingSequence,
                earlier ? chain.Message! : prior.RecoveryMessage ?? "Previously detected audit loss remains unresolved.",
                prior.RecoveryThrough);
        }
        if (!chain.Ok) return await RecoverAsync(chain.Problem!, chain.ProblemSequence!.Value, chain.Message!);
        var stored = chain.VerifiedThrough;
        if (chain.HighestWritten > stored)
            return await RecoverAsync("truncated", stored + 1,
                $"The snapshot ends at sequence {stored}; this process previously observed sequence {chain.HighestWritten}.");
        var position = chain.ForwardPosition;
        if (position.Sequence > stored)
            return await RecoverAsync("truncated", stored + 1,
                $"The snapshot ends at sequence {stored}; {position.Sequence} were already forwarded.");
        if (position.Sequence > chain.AnchorSequence && chain.HashAt(position.Sequence) != position.Hash)
            return await RecoverAsync("modified", position.Sequence, $"Entry {position.Sequence} differs from the one forwarded to the sink.");

        var anchorNote = chain.AnchorSequence > 0
            ? $" Verified from recovery anchor {chain.AnchorSequence + 1}; earlier history remains broken."
            : "";

        if (sink is null)
            return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
                "Local chain verified; no off-host sink is configured." + anchorNote, false);

        long next = chain.AnchorSequence + 1;
        try
        {
            while (true)
            {
                var page = await sink.ReadAsync(next, SinkPage, ct);
                if (page.Count == 0) break;
                var beyondSnapshot = false;
                foreach (var line in page)
                {
                    var entry = ControlPlaneAuditLog.Parse(line);
                    if (entry is null || !ControlPlaneAuditLog.TrySplit(line, out var body, out var hash)
                        || ControlPlaneAuditLog.Sha256Hex(body) != hash)
                        return Failed("sink-mismatch", next, $"The sink holds an unreadable or altered entry after sequence {next - 1}.", true);
                    if (entry.Sequence > stored)
                    {
                        // The sink page was read before this local read. A legitimate concurrent
                        // append must already be present locally; an absent entry is evidence of loss.
                        var current = log.Read(entry.Sequence, 1).FirstOrDefault();
                        if (current?.Sequence == entry.Sequence)
                        {
                            if (current.Raw != line)
                                return Failed("sink-mismatch", entry.Sequence,
                                    $"Entry {entry.Sequence} differs between the current local file and the sink.", true);
                            beyondSnapshot = true;
                            break;
                        }
                        var sinkThrough = entry.Sequence;
                        foreach (var raw in page)
                        {
                            var witness = ControlPlaneAuditLog.Parse(raw);
                            if (witness is null || !ControlPlaneAuditLog.TrySplit(raw, out var witnessBody, out var witnessHash)
                                || ControlPlaneAuditLog.Sha256Hex(witnessBody) != witnessHash)
                                return Failed("sink-mismatch", entry.Sequence, "The sink tail contains an unreadable or altered entry.", true);
                            sinkThrough = Math.Max(sinkThrough, witness.Sequence);
                        }
                        return await RecoverAsync("truncated", stored + 1,
                            $"The sink holds entries through {sinkThrough}; entry {entry.Sequence} was absent from the later local read.",
                            sinkThrough);
                    }
                    if (entry.Sequence > next && entry.Sequence <= position.Sequence)
                        return Failed("sink-gap", next, $"The sink lacks forwarded entry {next}.", true);
                    if (entry.Sequence < next) continue;
                    if (chain.HashAt(entry.Sequence) != hash)
                        return Failed("sink-mismatch", entry.Sequence, $"Entry {entry.Sequence} differs between the local chain and the sink.", true);
                    next = entry.Sequence + 1;
                }
                if (page.Count < SinkPage || beyondSnapshot) break;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
                "Local chain verified; the sink could not be read: " + CredentialRedactor.Redact(exception.Message) + anchorNote, false);
        }
        if (next <= position.Sequence)
            return Failed("sink-gap", next, $"The sink lacks forwarded entries from {next} to {position.Sequence}.", true);
        var comparison = chain.AnchorSequence == 0
            ? $"Local chain verified and matches the sink through entry {next - 1}."
            : next == chain.AnchorSequence + 1
                ? "Local segment verified; no entries from this segment were read from the sink."
                : $"Local segment verified and matches sink entries {chain.AnchorSequence + 1} to {next - 1}.";
        return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
            comparison + anchorNote, false, SinkCompared: true);
    }
}
