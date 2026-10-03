namespace AgentRunner;

/// <summary>
/// Keeps the fenced run lease alive for the duration of a run. The server clamps
/// the TTL and raises the fencing token on any takeover, so a heartbeat that is
/// rejected as <c>StaleToken</c> or <c>Expired</c> means this runner has lost the
/// lease to another holder and must abandon the run: it cancels the shared token
/// so the CLI is torn down instead of racing the new owner (the §8.2C split-brain
/// guard, enforced runner-side).
/// </summary>
public sealed class LeaseHeartbeat
{
    private readonly TaskServerClient _client;
    private readonly RunnerOptions _options;
    private readonly RunLeaseInfoDto _lease;
    private readonly Action<string> _log;
    private readonly RunnerProcessInventoryTracker? _inventory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private readonly DurableLeaseAuthority? _authority;
    private string? _startedPromptSha256;

    public LeaseHeartbeat(
        TaskServerClient client,
        RunnerOptions options,
        RunLeaseInfoDto lease,
        Action<string> log,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        RunnerProcessInventoryTracker? inventory = null,
        DurableLeaseAuthority? authority = null,
        Func<DateTime>? utcNow = null)
    {
        _client = client;
        _options = options;
        _lease = lease;
        _log = log;
        _inventory = inventory;
        _delay = delay ?? Task.Delay;
        _authority = authority;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Set when a heartbeat is rejected: the run must stop, the lease is gone.</summary>
    public bool LeaseLost { get; private set; }

    /// <summary>
    /// Set when the server answered a renewal with an operator stop request for
    /// this attempt (AGT-2870). Unlike <see cref="LeaseLost"/> this runner still
    /// owns the lease: it ends the worker, salvages, and hands the outcome back
    /// itself, which is what makes Pause and Pause-and-Send work for a run the
    /// backend cannot reach with a signal.
    /// </summary>
    public RunStopDirectiveDto? StopRequest { get; private set; }

    public void ConfirmWorkerStartedWithPrompt(string? promptSha256)
    {
        if (!string.IsNullOrWhiteSpace(promptSha256))
            Volatile.Write(ref _startedPromptSha256, promptSha256);
    }

    /// <summary>
    /// Renew on a cadence below the TTL until <paramref name="stopRun"/> fires.
    /// Cancels <paramref name="stopRun"/> itself when the lease is lost so the
    /// caller's run tears down promptly.
    /// </summary>
    public async Task RunAsync(CancellationTokenSource stopRun, CancellationToken shutdown)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatSeconds));
        var authorityExpiresAt = _lease.ExpiresAt.ToUniversalTime();
        var uncertaintyMargin = TimeSpan.FromSeconds(Math.Max(1, interval.TotalSeconds));
        var renewalAttempted = false;
        try
        {
            while (!stopRun.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                var stopBefore = _authority?.StopBeforeUtc
                                 ?? authorityExpiresAt - uncertaintyMargin;
                if (renewalAttempted && _utcNow() >= stopBefore)
                {
                    _authority?.Reject($"local autonomy deadline exhausted at {stopBefore:o}");
                    MarkLeaseLost(stopRun,
                        $"renewal safety boundary reached after wake; stop-before={stopBefore:o}");
                    return;
                }
                RunLeaseResponse resp;
                try
                {
                    var inventory = _inventory?.Snapshot();
                    var req = new RunLeaseHeartbeatRequest(
                        _lease.TaskKey, _lease.LeaseId, _lease.FencingToken, _options.RunnerId, _options.TtlSeconds,
                        _lease.AttemptId, _lease.AuthorityEpoch,
                        $"heartbeat:{_lease.AttemptId}:{Guid.NewGuid():N}",
                        inventory,
                        Volatile.Read(ref _startedPromptSha256));
                    renewalAttempted = true;
                    resp = await _client.RenewLeaseAsync(req, shutdown);
                    if (_client.UsesDurableTaskServer && inventory is not null)
                        _inventory!.AcknowledgeReports(inventory);
                }
                catch (TaskServerException ex) when (IsDefinitiveLeaseRejection(ex))
                {
                    if (ex.StatusCode is 404 or 409)
                    {
                        try
                        {
                            var reported = _client.CodingAttemptFor(_lease);
                            var adoption = await _client.ReAdoptAttemptAsync(reported, shutdown);
                            if (adoption is not null)
                            {
                                // Exact-current, expiry-only re-adoption (AGT-W65
                                // D9): the server matched task, runner, lease,
                                // fence, epoch and lease instance and granted a
                                // new expiry. Only that confirmation moves the
                                // stop-before boundary; without it the old
                                // boundary still ends the run below.
                                if (adoption.ExpiresAt is { } adoptedExpiresAt)
                                {
                                    authorityExpiresAt = adoptedExpiresAt.ToUniversalTime();
                                    _authority?.Confirm(
                                        authorityExpiresAt,
                                        "exact-current attempt re-adopted by a new server confirmation");
                                }
                                _log(
                                    $"lease authority re-adopted after HTTP {ex.StatusCode}; " +
                                    $"attempt={reported.AttemptId} fence={reported.Fence}");
                                continue;
                            }
                        }
                        catch (Exception registrationException) when (
                            registrationException is not OperationCanceledException)
                        {
                            _log(
                                $"lease re-adoption failed after HTTP {ex.StatusCode}: " +
                                registrationException.Message);
                        }
                    }
                    _authority?.Reject(
                        $"Task Server rejected lease renewal with HTTP {ex.StatusCode}: {ex.Message}");
                    MarkLeaseLost(
                        stopRun,
                        $"Task Server rejected lease renewal with HTTP {ex.StatusCode}: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    // A transient network error is not proof of a takeover. It
                    // does, however, consume the bounded server-issued authority
                    // window. Stop before the last known expiry minus one renewal
                    // interval so suspend, clock, and transport uncertainty cannot
                    // turn an unreachable Task Server into autonomous execution.
                    _authority?.MarkUncertain(
                        $"lease renewal transport failure: {ex.Message}");
                    var retryStopBefore = _authority?.StopBeforeUtc
                                          ?? authorityExpiresAt - uncertaintyMargin;
                    if (_utcNow() >= retryStopBefore)
                    {
                        _authority?.Reject(
                            $"local autonomy deadline exhausted at {retryStopBefore:o}");
                        MarkLeaseLost(
                            stopRun,
                            "renewal safety boundary reached: task-server-unavailable; " +
                            $"stop-before={retryStopBefore:o}; cancelling and reaping the active process generation: {ex.Message}");
                        return;
                    }

                    _log($"heartbeat error (will retry before {retryStopBefore:o}): {ex.Message}");
                    await _delay(interval, stopRun.Token);
                    continue;
                }

                if (!resp.Granted)
                {
                    _authority?.Reject($"{resp.Outcome} - {resp.Message}");
                    MarkLeaseLost(stopRun, $"{resp.Outcome} - {resp.Message}");
                    return;
                }
                var renewedExpiresAt = resp.Lease?.ExpiresAt.ToUniversalTime() ?? authorityExpiresAt;
                var responseReceivedAt = _utcNow();
                if (responseReceivedAt >= stopBefore ||
                    responseReceivedAt >= renewedExpiresAt - uncertaintyMargin)
                {
                    _authority?.Reject("renewal answer arrived after the local autonomy deadline");
                    MarkLeaseLost(stopRun, "renewal answer arrived after the local autonomy deadline");
                    return;
                }
                authorityExpiresAt = renewedExpiresAt;
                _authority?.Confirm(
                    authorityExpiresAt,
                    "fenced lease renewal reconciled before report replay");
                _inventory?.Apply(resp.ReconciliationActions);
                if (resp.StopRequest is not null)
                {
                    MarkStopRequested(stopRun, resp.StopRequest);
                    return;
                }
                await _delay(interval, stopRun.Token);
            }
        }
        catch (OperationCanceledException) { /* run finished or shutting down */ }
    }

    internal static bool IsDefinitiveLeaseRejection(TaskServerException ex)
        => ex.StatusCode is >= 400 and < 500
           && ex.StatusCode is not 408 and not 429;

    private void MarkStopRequested(CancellationTokenSource stopRun, RunStopDirectiveDto directive)
    {
        StopRequest = directive;
        _log(
            $"operator stop requested task={directive.TaskKey} command={directive.CommandId ?? "unknown"} reason={directive.Reason} " +
            $"requestedAt={directive.RequestedAtUtc:o} by={directive.RequestedBy ?? "unknown"}; " +
            "terminating the worker process tree and handing back");
        stopRun.Cancel();
    }

    private void MarkLeaseLost(CancellationTokenSource stopRun, string reason)
    {
        LeaseLost = true;
        _log($"lease lost; terminating CLI process group: {reason}");
        stopRun.Cancel();
    }
}
