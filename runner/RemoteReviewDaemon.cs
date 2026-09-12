using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Bounded daemon loop for the separately registered review service. Persisted
/// attempts are continued before load-aware admission may claim new work.
/// </summary>
public sealed class RemoteReviewDaemon
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;
    private readonly Func<int, TaskServerConnectivitySnapshot?, HostTelemetrySample?>? _telemetryProbe;

    /// <param name="telemetryProbe">
    /// Test seam: deterministic host telemetry (active slots, connectivity ->
    /// sample) for the load-aware admission gate. The real
    /// <see cref="HostTelemetrySampler"/> reads <c>/proc/loadavg</c>, so
    /// admission genuinely depends on host state: on Windows there is no load
    /// average at all (the gate stays closed), and on an idle Linux box Load1
    /// can be exactly 0.00 (a gate that should close never does). Null keeps
    /// the production sampler.
    /// </param>
    public RemoteReviewDaemon(
        RunnerOptions options,
        TaskServerClient client,
        Action<string> log,
        Func<int, TaskServerConnectivitySnapshot?, HostTelemetrySample?>? telemetryProbe = null)
    {
        _options = options;
        _client = client;
        _log = log;
        _telemetryProbe = telemetryProbe;
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        await using var idleWatchdog = new DaemonIdleWatchdog(
            _log,
            TimeSpan.FromMinutes(_options.IdleWatchdogMinutes));
        using var daemonStop = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown,
            idleWatchdog.AbortToken);
        shutdown = daemonStop.Token;
        var state = new ReviewStateStore(_options.StateDir);
        // A drain request remains authoritative across daemon generations. It
        // is cleared only by an explicit start/replacement after the service is
        // confirmed stopped, so an accidental relaunch cannot reopen claims.
        var persistedAtStartup = state.LoadAll();
        var reconciler = new ReviewSlotReconciler(state, _client.GetReviewAttemptAsync, log: _log);
        Task<string> RegisterAsync(CancellationToken ct) => _client.RegisterAsync(
            _options.RunnerName,
            "review-executor",
            ct,
            RunnerActiveAttemptReporter.Review(state.LoadAll()));
        var active = new List<(Task<int> Run, string AttemptId, string ResourceNamespace)>();
        var drainedToStop = false;
        string? startupControlRequestId = null;
        idleWatchdog.RecordActiveSlots(persistedAtStartup.Count(slot =>
            ReviewRestartGuardPolicy.IsBusyPhase(slot.Phase)));
        var connectivity = new TaskServerConnectivityMonitor(_log);
        var telemetry = new HostTelemetrySampler();
        HostTelemetrySample? latestTelemetry = null;
        var nextSlotHygieneLog = DateTime.MinValue;
        var nextSlotReconciliation = DateTime.MinValue;

        void LogSlotHygiene(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && now < nextSlotHygieneLog) return;
            var hygiene = state.GetHygieneSnapshot(now);
            _log(
                $"review-slot-hygiene total={hygiene.Total} " +
                $"reportPending={hygiene.ReportPending} " +
                $"oldestReportPendingSeconds={(hygiene.OldestReportPendingAge?.TotalSeconds ?? 0):0} " +
                $"terminalCleanupPending={hygiene.TerminalCleanupPending}");
            nextSlotHygieneLog = now.AddMinutes(1);
        }

        HostTelemetrySample? TakeTelemetry(bool force = false)
        {
            try
            {
                if (_telemetryProbe is not null)
                {
                    latestTelemetry = _telemetryProbe(active.Count, connectivity.Snapshot);
                    return latestTelemetry;
                }
                latestTelemetry = force
                    ? telemetry.SampleNow(active.Count, connectivity.Snapshot)
                    : telemetry.SampleIfDue(active.Count, connectivity.Snapshot) ?? latestTelemetry;
                return latestTelemetry;
            }
            catch (Exception exception)
            {
                _log(
                    $"review host telemetry sample failed error={exception.GetType().Name} " +
                    $"message={exception.Message}");
                return null;
            }
        }

        void StartContinuations(ReviewSlotReconciliation reconciliation, string scope)
        {
            foreach (var continuation in reconciliation.Continuations)
            {
                var slot = continuation.Slot;
                var executor = new RemoteReviewExecutor(_options, _client, state, _log);
                if (continuation.Kind == ReviewSlotContinuationKind.Reattach)
                {
                    _log(
                        $"persisted review accepted attempt={slot.AttemptId} " +
                        $"fence={slot.Claim.Lease!.Fence} verification={continuation.Reason}");
                    active.Add((
                        executor.ReattachAsync(slot, shutdown),
                        slot.AttemptId,
                        slot.Claim.Lease.ResourceNamespace));
                }
                else
                {
                    _log(
                        $"persisted review has valid lease but no live worker " +
                        $"attempt={slot.AttemptId} fence={slot.Claim.Lease!.Fence}; " +
                        $"settling restart loss: {continuation.Reason}");
                    active.Add((
                        executor.ReportNonAdoptableAsync(
                            slot,
                            continuation.Reason,
                            shutdown),
                        slot.AttemptId,
                        slot.Claim.Lease.ResourceNamespace));
                }
            }

            if (scope == "startup"
                || reconciliation.Purged > 0
                || reconciliation.Deferred > 0
                || reconciliation.Continuations.Count > 0)
            {
                _log(reconciliation.JournalLine(scope));
            }
            if (scope == "startup" || reconciliation.AgedPurged > 0)
            {
                _log(reconciliation.AgingJournalLine(
                    scope,
                    ReviewSlotReconciler.MaximumDormantAge));
            }
        }

        async Task HonorControlBeforeClaimsAsync(CancellationToken ct)
        {
            var request = ReviewDrainGuard.ReadDrainRequest(_options.StateDir);
            if (request is null)
            {
                if (startupControlRequestId is not null)
                {
                    _log("review startup control request withdrawn; resuming Task Server startup");
                    startupControlRequestId = null;
                }
                return;
            }

            var snapshot = ReviewDrainGuard.Inspect(_options.StateDir);
            var liveActive = active.Count(slot => !slot.Run.IsCompleted);
            var workAtRisk = Math.Max(liveActive, snapshot.Busy + snapshot.Unreadable);
            ReviewDrainGuard.AcknowledgeDrain(
                _options.StateDir,
                request,
                workAtRisk);
            if (!string.Equals(
                    startupControlRequestId,
                    request.RequestId,
                    StringComparison.Ordinal))
            {
                startupControlRequestId = request.RequestId;
                _log(
                    $"review startup admission closed mode={request.Mode ?? ReviewDrainGuard.DrainMode} " +
                    $"request={request.RequestId} activeSlots={workAtRisk}");
            }

            if (workAtRisk > 0)
                return;

            if (!ReviewDrainGuard.IsRestartGuard(request))
            {
                drainedToStop = true;
                _log(
                    "review daemon drained before Task Server startup: no persisted reviews remain; " +
                    "stopping without registration");
                throw new ReviewStartupControlException();
            }

            // A replacement guard differs from a drain: it holds an idle
            // generation alive but claim-free until the helper either
            // withdraws the request or sends SIGTERM.
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var current = ReviewDrainGuard.ReadDrainRequest(_options.StateDir);
                if (!string.Equals(current?.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    _log("review restart admission barrier withdrawn; resuming Task Server startup");
                    startupControlRequestId = null;
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }

        async Task ObserveStartupControlAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await HonorControlBeforeClaimsAsync(ct);
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }

        async Task AdvertiseCapabilitiesWithControlAsync(
            long generation,
            CancellationToken ct)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var advertisement = CapabilityAdvertisementRecovery.ExecuteAsync(
                "review capability advertisement",
                async operationToken =>
                {
                    await _client.AdvertiseCapabilitiesAsync(
                        RunnerCapabilityProbe.Advertise(
                            _options,
                            gitPushReady: false,
                            connectivity: connectivity.Snapshot),
                        RunnerCapabilityProbe.Telemetry(TakeTelemetry(force: true)),
                        generation,
                        operationToken);
                },
                async operationToken =>
                {
                    _ = await RegisterAsync(operationToken);
                },
                connectivity,
                () => active.Count,
                _options.PollSeconds,
                TimeSpan.FromSeconds(_options.ServerRequestTimeoutSeconds),
                _log,
                operation.Token);
            var control = ObserveStartupControlAsync(operation.Token);
            var completed = await Task.WhenAny(advertisement, control);
            if (completed == control)
            {
                try
                {
                    await control;
                }
                finally
                {
                    operation.Cancel();
                    try { await advertisement; }
                    catch (Exception) when (operation.IsCancellationRequested) { }
                }
                return;
            }

            operation.Cancel();
            try { await control; }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            await advertisement;
        }

        try
        {
            await HonorControlBeforeClaimsAsync(shutdown);
            await WithServerRetryAsync(
                "review registration",
                () => RegisterAsync(shutdown),
                connectivity,
                () => Math.Max(active.Count, persistedAtStartup.Count),
                shutdown,
                HonorControlBeforeClaimsAsync);

            var startupReconciliation = await reconciler.ReconcileAsync(
                new HashSet<string>(StringComparer.Ordinal),
                DateTime.UtcNow,
                shutdown);
            StartContinuations(startupReconciliation, "startup");
            CliProcessReaper.RecordExternalReap(CliOrphanSweep.Sweep(
                [_options.ReviewWorkDir],
                ReviewSlotReconciler.MaximumDormantAge,
                _log));
            if (active.Count > 0)
            {
                _log(
                    $"recovering {active.Count} persisted review slot(s) before replacement claims; " +
                    "load admission applies only to fresh slots");
            }
            nextSlotReconciliation = DateTime.UtcNow.AddMinutes(1);
            idleWatchdog.RecordActiveSlots(active.Count);
            LogSlotHygiene(force: true);

            var capabilityGeneration = DateTime.UtcNow.Ticks;
            await AdvertiseCapabilitiesWithControlAsync(capabilityGeneration, shutdown);

            var nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
            var admissionClosed = false;
            var nextRetentionSweep = DateTime.MinValue;
            var consecutiveFaults = 0;
            string? announcedControlRequestId = null;
            while (!shutdown.IsCancellationRequested)
            {
                idleWatchdog.RecordActiveSlots(active.Count);
                LogSlotHygiene();
                for (var index = active.Count - 1; index >= 0; index--)
                {
                    if (!active[index].Run.IsCompleted) continue;
                    try
                    {
                        var exitCode = await active[index].Run;
                        _log($"remote review slot finished exit={exitCode}");
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                        _log("remote review slot stopped during shutdown");
                    }
                    catch (Exception exception)
                    {
                        _log($"remote review slot failed after cleanup: {exception.Message}");
                    }
                    active.RemoveAt(index);
                }
                idleWatchdog.RecordActiveSlots(active.Count);

                // A drain stops claiming immediately and lets the running reviews
                // finish. Exiting on an empty slot set deliberately stops the role;
                // only a later guarded replacement clears the marker and claims.
                var controlRequest = ReviewDrainGuard.ReadDrainRequest(_options.StateDir);
                if (controlRequest is not null)
                {
                    // This acknowledgement is the admission barrier for the
                    // operator command. The loop can reach it only after an atomic
                    // claim already in flight has returned and its accepted slot
                    // has been placed in the active set. No claim occurs below
                    // while this request remains present.
                    ReviewDrainGuard.AcknowledgeDrain(
                        _options.StateDir,
                        controlRequest,
                        active.Count);
                    if (!string.Equals(
                            announcedControlRequestId,
                            controlRequest.RequestId,
                            StringComparison.Ordinal))
                    {
                        announcedControlRequestId = controlRequest.RequestId;
                        if (ReviewDrainGuard.IsRestartGuard(controlRequest))
                        {
                            _log(
                                $"review restart admission barrier acknowledged: {controlRequest.Reason} " +
                                $"(requested {controlRequest.RequestedAtUtc:O}); no new claims, " +
                                $"{active.Count} running review(s)");
                        }
                        else
                        {
                            _log(
                                $"review daemon draining: {controlRequest.Reason} " +
                                $"(requested {controlRequest.RequestedAtUtc:O}); no new claims, " +
                                $"finishing {active.Count} running review(s)");
                        }
                    }
                    // A drain can outlast the idle deadline by design, so the
                    // watchdog must not mistake it for a stalled poll loop.
                    idleWatchdog.RecordPollStarted();
                    if (!ReviewDrainGuard.IsRestartGuard(controlRequest) && active.Count == 0)
                    {
                        drainedToStop = true;
                        _log("review daemon drained: no running reviews left; stopping without replacement");
                        break;
                    }
                    await DelayThroughShutdown(
                        TimeSpan.FromSeconds(_options.PollSeconds),
                        shutdown);
                    continue;
                }
                if (announcedControlRequestId is not null)
                {
                    announcedControlRequestId = null;
                    _log("review control request withdrawn; resuming review claims");
                }

                try
                {
                    if (DateTime.UtcNow >= nextSlotReconciliation)
                    {
                        var reconciliation = await reconciler.ReconcileAsync(
                            active.Select(slot => slot.AttemptId)
                                .ToHashSet(StringComparer.Ordinal),
                            DateTime.UtcNow,
                            shutdown);
                        StartContinuations(reconciliation, "periodic");
                        nextSlotReconciliation = DateTime.UtcNow.AddMinutes(1);
                        idleWatchdog.RecordActiveSlots(active.Count);
                    }
                    if (DateTime.UtcNow >= nextRetentionSweep)
                    {
                        try
                        {
                            ReviewWorkspaceRetention.Sweep(
                                _options.ReviewWorkDir,
                                active.Select(slot => slot.ResourceNamespace),
                                DateTime.UtcNow,
                                _log);
                            CliProcessReaper.RecordExternalReap(CliOrphanSweep.Sweep(
                                [_options.ReviewWorkDir],
                                ReviewSlotReconciler.MaximumDormantAge,
                                _log));
                        }
                        catch (Exception exception)
                        {
                            _log(
                                "review workspace retention sweep failed; " +
                                $"retrying next interval: {exception.Message}");
                        }
                        nextRetentionSweep = DateTime.UtcNow.AddHours(1);
                    }
                    var observedServer = false;
                    var admissionTelemetry = TakeTelemetry(force: true);
                    if (DateTime.UtcNow >= nextCapabilityAdvertisement)
                    {
                        var generation = ++capabilityGeneration;
                        await AdvertiseCapabilitiesWithControlAsync(generation, shutdown);
                        observedServer = true;
                        nextCapabilityAdvertisement = DateTime.UtcNow.AddMinutes(1);
                        // The concurrent control observer may have acknowledged a
                        // barrier just before advertisement won the race. Yield to
                        // the loop's control branch before any claim in that case.
                        if (ReviewDrainGuard.ReadDrainRequest(_options.StateDir) is not null)
                            continue;
                    }

                    idleWatchdog.RecordPollStarted();

                    var admission = ReviewSlotAdmissionPolicy.Decide(
                        admissionTelemetry,
                        active.Count,
                        _options.HostMaxParallelism,
                        _options.ClaimMaxLoadPerCore);
                    if (!admission.Admitted)
                    {
                        if (!admissionClosed)
                        {
                            _log(
                                $"review slot admission closed: {admission.Reason}; " +
                                $"activeSlots={active.Count}");
                        }
                        admissionClosed = true;
                    }
                    else
                    {
                        if (admissionClosed)
                        {
                            _log(
                                $"review slot admission reopened: {admission.Reason}; " +
                                $"activeSlots={active.Count}");
                        }
                        admissionClosed = false;

                        // Admission owns at most one new lease per fresh telemetry
                        // observation. Persisted continuations above do not pass
                        // through this gate and never lose completed test time.
                        var claim = await _client.ClaimReviewAsync(
                            new ReviewClaimRequest(
                                _options.RunnerId,
                                _client.RunnerInstanceId,
                                _options.TtlSeconds,
                                AvailableSlots: 1),
                            // A claim is an atomic authority mutation. Once sent,
                            // shutdown must not hide a successfully minted fence.
                            CancellationToken.None);
                        observedServer = true;
                        if (string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase))
                        {
                            if (active.Any(slot => string.Equals(
                                    slot.AttemptId,
                                    claim.Attempt!.AttemptId,
                                    StringComparison.Ordinal)))
                            {
                                _log(
                                    $"claim returned attempt {claim.Attempt!.AttemptId} already in flight " +
                                    "on this host; skipping duplicate execution");
                            }
                            else
                            {
                                _log(
                                    $"claimed remote review attempt={claim.Attempt!.AttemptId} " +
                                    $"subject={claim.Subject!.SubjectId} " +
                                    $"slot={active.Count + 1}/{_options.HostMaxParallelism}");
                                var executor = new RemoteReviewExecutor(_options, _client, state, _log);
                                var stale = state.Find(claim.Attempt.AttemptId);
                                if (stale is not null
                                    && !DurableReviewProcess.HasCompleted(stale)
                                    && !DurableReviewProcess.VerifyLive(stale, out var adoptionReason))
                                {
                                    // A previous lease can expire before its loss
                                    // report is accepted. Rebind only that terminal
                                    // report to the new fence. An unproven process is
                                    // never allowed to recover write authority.
                                    stale = state.Save(stale with
                                    {
                                        Claim = claim,
                                        Phase = "adoption-failed-reclaimed",
                                        AdoptionFailure = adoptionReason,
                                    });
                                    active.Add((
                                        executor.ReportNonAdoptableAsync(
                                            stale,
                                            adoptionReason,
                                            shutdown),
                                        claim.Attempt.AttemptId,
                                        claim.Lease!.ResourceNamespace));
                                    idleWatchdog.RecordActiveSlots(active.Count);
                                }
                                else
                                {
                                    active.Add((
                                        executor.RunClaimedAsync(claim, shutdown),
                                        claim.Attempt.AttemptId,
                                        claim.Lease!.ResourceNamespace));
                                    idleWatchdog.RecordActiveSlots(active.Count);
                                }
                            }
                        }
                    }

                    consecutiveFaults = 0;
                    if (observedServer
                        && connectivity.RecordSuccess(DateTime.UtcNow, "review claim poll"))
                    {
                        TakeTelemetry(force: true);
                        nextCapabilityAdvertisement = DateTime.MinValue;
                    }
                    await DelayThroughShutdown(
                        TimeSpan.FromSeconds(_options.PollSeconds),
                        shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    // Active durable workers observe the handoff below.
                }
                catch (TaskServerException fatal) when (fatal.StatusCode is 401 or 403)
                {
                    _log(
                        $"review claim poll rejected with {fatal.StatusCode}; " +
                        $"exiting for re-registration: {fatal.Message}");
                    throw;
                }
                catch (TaskServerException exception) when (
                    ReviewClaimRegistrationRecovery.IsRequired(exception)
                    && !shutdown.IsCancellationRequested)
                {
                    _log(
                        $"review claim authority lost code={exception.ErrorCode}; " +
                        "performing full review executor re-registration");
                    await WithServerRetryAsync(
                        "review claim authority recovery registration",
                        () => RegisterAsync(shutdown),
                        connectivity,
                        () => active.Count,
                        shutdown,
                        HonorControlBeforeClaimsAsync);
                    nextCapabilityAdvertisement = DateTime.MinValue;
                }
                catch (Exception exception) when (RemoteRunnerDaemon.IsTransientServerFault(exception))
                {
                    var delay = TaskServerConnectivityMonitor.RetryDelay(
                        _options.PollSeconds,
                        ++consecutiveFaults);
                    connectivity.RecordFailure(
                        DateTime.UtcNow,
                        "review claim poll",
                        exception,
                        delay,
                        active.Count);
                    TakeTelemetry(force: true);
                    await DelayThroughShutdown(delay, shutdown);
                }
                catch (Exception exception)
                {
                    // A server-side conflict is visible but must not churn the
                    // daemon or its detached workers.
                    _log($"review claim poll failed; retrying next tick: {exception.Message}");
                    await DelayThroughShutdown(
                        TimeSpan.FromSeconds(_options.PollSeconds),
                        shutdown);
                }
            }

        }
        catch (ReviewStartupControlException)
        {
            // The host-local control request already logged why startup ended.
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            _log("review daemon startup stopped during Task Server communication");
        }

        state.Flush();
        if (active.Count > 0)
            await Task.WhenAll(active.Select(slot => slot.Run));
        _log(drainedToStop
            ? "review daemon stopped after the acknowledged drain"
            : "review daemon handoff complete; durable review workers are ready for replacement adoption");
        if (idleWatchdog.Tripped)
            throw new InvalidOperationException(
                "The slot-free review daemon stopped polling and was terminated by its idle watchdog.");
    }

    private async Task<T> WithServerRetryAsync<T>(
        string operation,
        Func<Task<T>> call,
        TaskServerConnectivityMonitor connectivity,
        Func<int> activeSlots,
        CancellationToken shutdown,
        Func<CancellationToken, Task>? controlCheck = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            if (controlCheck is not null)
                await controlCheck(shutdown);
            try
            {
                var result = await call();
                connectivity.RecordSuccess(DateTime.UtcNow, operation);
                return result;
            }
            catch (Exception exception) when (
                RemoteRunnerDaemon.IsTransientServerFault(exception)
                && !shutdown.IsCancellationRequested)
            {
                var delay = TaskServerConnectivityMonitor.RetryDelay(
                    _options.PollSeconds,
                    attempt);
                connectivity.RecordFailure(
                    DateTime.UtcNow,
                    operation,
                    exception,
                    delay,
                    activeSlots());
                var deadline = DateTime.UtcNow.Add(delay);
                while (DateTime.UtcNow < deadline)
                {
                    if (controlCheck is not null)
                        await controlCheck(shutdown);
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(250)
                            ? remaining
                            : TimeSpan.FromMilliseconds(250),
                        shutdown);
                }
            }
        }
    }

    private sealed class ReviewStartupControlException : Exception
    {
    }

    private static async Task DelayThroughShutdown(
        TimeSpan delay,
        CancellationToken shutdown)
    {
        try { await Task.Delay(delay, shutdown); }
        catch (OperationCanceledException) { }
    }
}
