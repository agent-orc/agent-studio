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
        var recovery = FindLocalBreak(chain, monitor.Integrity);
        if (recovery is not null) return await RecoverAsync(chain, now, recovery, ct);

        return await CompareSinkAsync(chain, now, ct);
    }

    private static RecoveryRequest? FindLocalBreak(ChainCheck chain, ControlPlaneAuditIntegrity prior)
    {
        if (prior.RecoveryPending && prior.ProblemSequence is { } pendingSequence)
        {
            if (chain.ProblemSequence is { } currentSequence && currentSequence < pendingSequence)
                return new(chain.Problem!, currentSequence, chain.Message!, prior.RecoveryThrough);
            return new(prior.Problem!, pendingSequence,
                prior.RecoveryMessage ?? "Previously detected audit loss remains unresolved.", prior.RecoveryThrough);
        }
        if (!chain.Ok) return new(chain.Problem!, chain.ProblemSequence!.Value, chain.Message!);
        var stored = chain.VerifiedThrough;
        if (chain.HighestWritten > stored)
            return new("truncated", stored + 1,
                $"The snapshot ends at sequence {stored}; this process previously observed sequence {chain.HighestWritten}.");
        var position = chain.ForwardPosition;
        if (position.Sequence > stored)
            return new("truncated", stored + 1,
                $"The snapshot ends at sequence {stored}; {position.Sequence} were already forwarded.");
        if (position.Sequence > chain.AnchorSequence && chain.HashAt(position.Sequence) != position.Hash)
            return new("modified", position.Sequence, $"Entry {position.Sequence} differs from the one forwarded to the sink.");
        return null;
    }

    private async Task<ControlPlaneAuditIntegrity> RecoverAsync(
        ChainCheck chain, DateTime checkedAt, RecoveryRequest request, CancellationToken ct)
    {
        var attempt = CreateRecoveryAttempt(chain, request);
        if (sink is not null)
        {
            // Publish the proven local break before attempting fallible sink reads.
            var pending = RecoveryResult(chain, checkedAt, attempt,
                " Recovery waits until the sink tail can be established.", pending: true);
            monitor.RecordIntegrity(pending);
            await monitor.RaiseAsync(ControlPlaneAuditAlertCodes.ChainBroken, pending.Message!, request.Sequence, ct,
                attempt.Occurrence, attempt.FirstObservedAt);

            var tail = await ReadSinkTailAsync(sink, attempt.Through, ct);
            attempt = attempt with { Through = tail.Through };
            if (tail.Problem is not null)
                return RecoveryResult(chain, checkedAt, attempt,
                    tail.Problem + " Recovery waits until the sink tail can be established.", pending: true);
        }
        return AnchorRecovery(chain, checkedAt, attempt);
    }

    private RecoveryAttempt CreateRecoveryAttempt(ChainCheck chain, RecoveryRequest request)
    {
        var through = Math.Max(Math.Max(request.KnownThrough, request.Sequence),
            Math.Max(chain.ObservedThrough, Math.Max(chain.HighestWritten, chain.ForwardPosition.Sequence)));
        var detectedAt = _clock.GetUtcNow().UtcDateTime;
        var basis = $"{log.LatestRecoveryAnchor?.Hash ?? ControlPlaneAuditLog.GenesisHash}|{request.Sequence}|";
        var open = monitor.IsOpen(ControlPlaneAuditAlertCodes.ChainBroken)
            ? monitor.Raised.LastOrDefault(alert => alert.Code == ControlPlaneAuditAlertCodes.ChainBroken) : null;
        var continuing = open?.Occurrence?.StartsWith(basis, StringComparison.Ordinal) == true;
        return new(request, through, detectedAt,
            continuing ? open!.Occurrence! : basis + Guid.NewGuid().ToString("N"),
            continuing ? open!.OccurredAt ?? detectedAt : detectedAt);
    }

    private static async Task<SinkTail> ReadSinkTailAsync(IControlPlaneAuditSink sink, long through, CancellationToken ct)
    {
        try
        {
            for (var page = 0; page < 64; page++)
            {
                var previousThrough = through;
                var tail = await sink.ReadAsync(through + 1, SinkPage, ct);
                foreach (var raw in tail)
                {
                    var witness = ControlPlaneAuditLog.Parse(raw);
                    if (witness is null || !ControlPlaneAuditLog.TrySplit(raw, out var body, out var hash)
                        || ControlPlaneAuditLog.Sha256Hex(body) != hash)
                        return new(through, " The sink tail contains an altered entry.");
                    through = Math.Max(through, witness.Sequence);
                }
                if (tail.Count < SinkPage) return new(through);
                if (through <= previousThrough) return new(through, " The sink tail did not advance.");
            }
            return new(through, " The sink tail did not finish within 64 pages.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(through, " The sink tail could not be read: " + CredentialRedactor.Redact(exception.Message));
        }
    }

    private ControlPlaneAuditIntegrity AnchorRecovery(ChainCheck chain, DateTime checkedAt, RecoveryAttempt attempt)
    {
        try
        {
            var anchored = log.TryAnchorBreak(chain with
            {
                ProblemSequence = attempt.Request.Sequence,
                HighestWritten = Math.Max(chain.HighestWritten, attempt.Through),
            }, attempt.Evidence, attempt.Occurrence, attempt.FirstObservedAt);
            return RecoveryResult(chain, checkedAt, attempt, anchored
                ? " A new recovery anchor seals the damaged history; subsequent entries can be verified."
                : " The file prefix changed during recovery; anchoring will be retried at the next verification.", pending: !anchored);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return RecoveryResult(chain, checkedAt, attempt,
                " Recovery anchor could not be written: " + CredentialRedactor.Redact(exception.Message), pending: true);
        }
    }

    private static ControlPlaneAuditIntegrity RecoveryResult(
        ChainCheck chain, DateTime checkedAt, RecoveryAttempt attempt, string outcome, bool pending)
        => Failed(chain, checkedAt, attempt.Request.Problem, attempt.Request.Sequence, attempt.Evidence + outcome) with
        {
            Occurrence = attempt.Occurrence, OccurredAt = attempt.FirstObservedAt,
            RecoveryPending = pending, RecoveryThrough = attempt.Through, RecoveryMessage = attempt.Request.Message,
        };

    private static ControlPlaneAuditIntegrity Failed(
        ChainCheck chain, DateTime checkedAt, string problem, long? sequence, string message, bool sinkCompared = false)
        => new(ControlPlaneAuditIntegrityStates.Failed, checkedAt, chain.VerifiedThrough, message, true, sinkCompared, problem, sequence);

    private sealed record RecoveryRequest(string Problem, long Sequence, string Message, long KnownThrough = 0);

    private sealed record RecoveryAttempt(
        RecoveryRequest Request, long Through, DateTime DetectedAt, string Occurrence, DateTime FirstObservedAt)
    {
        public string Evidence => $"{Request.Message} Sequence range {Request.Sequence} to {Through}; "
            + $"detected at {DetectedAt:O}; process {ControlPlaneAuditLog.ProcessIdentity}.";
    }

    private sealed record SinkTail(long Through, string? Problem = null);

    private async Task<ControlPlaneAuditIntegrity> CompareSinkAsync(ChainCheck chain, DateTime now, CancellationToken ct)
    {
        var stored = chain.VerifiedThrough;
        var position = chain.ForwardPosition;
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
                        return Failed(chain, now, "sink-mismatch", next, $"The sink holds an unreadable or altered entry after sequence {next - 1}.", true);
                    if (entry.Sequence > stored)
                    {
                        // The sink page was read before this local read. A legitimate concurrent
                        // append must already be present locally; an absent entry is evidence of loss.
                        var current = log.Read(entry.Sequence, 1).FirstOrDefault();
                        if (current?.Sequence == entry.Sequence)
                        {
                            if (current.Raw != line)
                                return Failed(chain, now, "sink-mismatch", entry.Sequence,
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
                                return Failed(chain, now, "sink-mismatch", entry.Sequence, "The sink tail contains an unreadable or altered entry.", true);
                            sinkThrough = Math.Max(sinkThrough, witness.Sequence);
                        }
                        return await RecoverAsync(chain, now, new("truncated", stored + 1,
                            $"The sink holds entries through {sinkThrough}; entry {entry.Sequence} was absent from the later local read.",
                            sinkThrough), ct);
                    }
                    if (entry.Sequence > next && entry.Sequence <= position.Sequence)
                        return Failed(chain, now, "sink-gap", next, $"The sink lacks forwarded entry {next}.", true);
                    if (entry.Sequence < next) continue;
                    if (chain.HashAt(entry.Sequence) != hash)
                        return Failed(chain, now, "sink-mismatch", entry.Sequence, $"Entry {entry.Sequence} differs between the local chain and the sink.", true);
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
            return Failed(chain, now, "sink-gap", next, $"The sink lacks forwarded entries from {next} to {position.Sequence}.", true);
        var comparison = chain.AnchorSequence == 0
            ? $"Local chain verified and matches the sink through entry {next - 1}."
            : next == chain.AnchorSequence + 1
                ? "Local segment verified; no entries from this segment were read from the sink."
                : $"Local segment verified and matches sink entries {chain.AnchorSequence + 1} to {next - 1}.";
        return new(ControlPlaneAuditIntegrityStates.Verified, now, stored,
            comparison + anchorNote, false, SinkCompared: true);
    }
}
