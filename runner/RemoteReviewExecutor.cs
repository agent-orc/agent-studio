using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Executes one separately fenced ReviewAttempt. Workspace preparation remains
/// daemon-owned; the expensive review plan runs in a detached, durable worker
/// that a replacement daemon can positively identify and adopt.
/// </summary>
public sealed class RemoteReviewExecutor
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly ReviewStateStore _state;
    private readonly Action<string> _log;
    private readonly object _slotGate = new();

    /// <summary>
    /// Live write authority for this slot. The heartbeat may re-fence it while
    /// the executor is saving phases, so nothing captures the lease by value.
    /// </summary>
    private ReviewAuthorityHandle _authority = null!;

    private PersistedReviewSlot _slot = null!;
    private long _renewSequence;

    /// <summary>
    /// Namespace the detached worker physically materialized under. A takeover
    /// mints a new fence, but the running workspace keeps its original name, so
    /// evidence must report the one that was used.
    /// </summary>
    private string _workspaceNamespace = string.Empty;

    internal Func<int, TimeSpan>? ReportRetryDelayOverride { get; set; }

    public RemoteReviewExecutor(
        RunnerOptions options,
        TaskServerClient client,
        ReviewStateStore state,
        Action<string> log)
    {
        _options = options;
        _client = client;
        _state = state;
        _log = log;
    }

    public async Task<int> RunClaimedAsync(ReviewClaimResponse claim, CancellationToken shutdown)
    {
        ValidateClaim(claim);
        var workspace = new RemoteReviewWorkspace(_options, claim.Subject!, claim.Lease!, _log);
        var slot = _state.Create(claim, workspace.RepositoryPath);
        BindAuthority(slot);
        return await RunPersistedAsync(slot, workspace, shutdown, reattach: false);
    }

    public async Task<int> ReattachAsync(PersistedReviewSlot slot, CancellationToken shutdown)
    {
        ValidateClaim(slot.Claim);
        _log(
            $"adopting persisted review attempt={slot.AttemptId} fence={slot.Claim.Lease!.Fence} " +
            $"pid={slot.ProcessId?.ToString() ?? "result-ready"} phase={slot.Phase}");
        var workspace = new RemoteReviewWorkspace(
            _options,
            slot.Claim.Subject!,
            slot.Claim.Lease!,
            _log);
        BindAuthority(slot);
        return await RunPersistedAsync(slot, workspace, shutdown, reattach: true);
    }

    private void BindAuthority(PersistedReviewSlot slot)
    {
        // One executor drives exactly one review slot: its authority, sequence,
        // and workspace namespace are per-run state, not per-instance settings.
        if (_authority is not null)
            throw new InvalidOperationException(
                "This RemoteReviewExecutor already drives a review slot. Create one per slot.");
        _authority = new ReviewAuthorityHandle(slot.Claim);
        _workspaceNamespace = slot.Claim.Lease!.ResourceNamespace;
        lock (_slotGate) _slot = slot;
    }

    /// <summary>
    /// Persists a phase transition together with whatever write authority the
    /// heartbeat holds right now, so a takeover is never overwritten by a stale
    /// captured claim.
    /// </summary>
    private PersistedReviewSlot SaveSlot(PersistedReviewSlot slot)
    {
        lock (_slotGate)
        {
            _slot = _state.Save(slot with { Claim = _authority.Claim });
            return _slot;
        }
    }

    /// <summary>Writes a re-fenced claim through without changing the phase.</summary>
    private void PersistAuthority()
    {
        lock (_slotGate) _slot = _state.Save(_slot with { Claim = _authority.Claim });
    }

    /// <summary>
    /// Settles a persisted slot whose exact process generation cannot be proven.
    /// A caller may first replace <see cref="PersistedReviewSlot.Claim"/> with a
    /// freshly fenced claim for the same attempt when the original lease expired.
    /// </summary>
    public async Task<int> ReportNonAdoptableAsync(
        PersistedReviewSlot slot,
        string reason,
        CancellationToken shutdown)
    {
        ValidateClaim(slot.Claim);
        BindAuthority(slot);
        slot = SaveSlot(slot with
        {
            Phase = "adoption-failed",
            AdoptionFailure = reason,
        });
        var workspace = new RemoteReviewWorkspace(
            _options,
            slot.Claim.Subject!,
            slot.Claim.Lease!,
            _log);
        var summary = LostWorkSummary(slot, reason);
        _log(
            $"review adoption failed attempt={slot.AttemptId} fence={slot.Claim.Lease!.Fence}; " +
            $"settling visible restart loss: {summary}");
        return await FinalizeInfrastructureAsync(
            slot,
            workspace,
            "ExecutorRestarted",
            summary,
            // The HTTP mutation itself is never cancelled once sent. The daemon
            // token only interrupts a retry delay so replacement can adopt it.
            shutdown);
    }

    private async Task<int> RunPersistedAsync(
        PersistedReviewSlot slot,
        RemoteReviewWorkspace workspace,
        CancellationToken shutdown,
        bool reattach)
    {
        var attempt = slot.Claim.Attempt!;
        var subject = slot.Claim.Subject!;
        var lease = slot.Claim.Lease!;
        var workerStarted = slot.ProcessId is not null || DurableReviewProcess.HasCompleted(slot);

        // Adoption verifies before the first heartbeat. A handed-off lease that
        // the Task Server no longer honours must be repaired now, while the
        // worker is still provably alive - not thirty seconds later, when the
        // only remaining move is to drop an expensive report.
        if (reattach) slot = await VerifyAdoptedAuthorityAsync(slot);

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = RenewLoopAsync(attempt.TaskId, heartbeatStop.Token);
        try
        {
            // A completed persisted slot is terminal work from the previous
            // daemon generation. Reporting it must never materialize or launch
            // another worker under the adopted fence.
            if (reattach && DurableReviewProcess.HasCompleted(slot))
            {
                var completedResult = DurableReviewProcess.Attach(slot).ReadResult();
                if (completedResult is not null)
                {
                    slot = SaveSlot(slot with { Phase = "finalizing" });
                    return await FinalizeResultAsync(
                        slot,
                        workspace,
                        completedResult,
                        shutdown);
                }
            }

            if (!reattach)
            {
                try
                {
                    _log(
                        $"materializing review attempt={attempt.AttemptId} " +
                        $"subject={subject.SubjectId} expected={subject.ExpectedResultSha}");
                    await workspace.PrepareAsync(_client, shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return await FinalizeInfrastructureAsync(
                        slot,
                        workspace,
                        "ExecutorRestarted",
                        LostWorkSummary(slot, "daemon stopped during workspace preparation"),
                        shutdown);
                }
                catch (ReviewInfrastructureException exception)
                {
                    return await FinalizeInfrastructureAsync(
                        slot,
                        workspace,
                        exception.Classification,
                        exception.Message,
                        shutdown);
                }

                slot = SaveSlot(slot with { Phase = "launching" });
                DurableReviewProcess process;
                try
                {
                    process = DurableReviewProcess.Start(_options, slot);
                }
                catch (Exception exception)
                {
                    return await FinalizeInfrastructureAsync(
                        slot,
                        workspace,
                        "ReviewWorkerStartFailed",
                        $"Detached review worker could not start: {exception.Message}",
                        shutdown);
                }
                slot = SaveSlot(slot with
                {
                    ProcessId = process.ProcessId,
                    ProcessStartedAtUtc = process.ProcessStartedAtUtc,
                    Phase = "running",
                });
                workerStarted = true;
                _log(
                    $"detached review worker started attempt={attempt.AttemptId} " +
                    $"fence={lease.Fence} pid={process.ProcessId}");
            }

            var attached = DurableReviewProcess.Attach(slot);
            while (true)
            {
                var result = attached.ReadResult();
                if (result is not null)
                {
                    slot = SaveSlot(slot with { Phase = "finalizing" });
                    return await FinalizeResultAsync(slot, workspace, result, shutdown);
                }
                if (!DurableReviewProcess.VerifyLive(slot, out var processProof))
                {
                    // The worker writes identity before executing the plan. Give
                    // the narrow Process.Start-to-identity window one bounded
                    // chance to close before declaring visible lost work.
                    if (string.Equals(slot.Phase, "launching", StringComparison.Ordinal))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown);
                        if (DurableReviewProcess.TryRecoverIdentity(
                                slot,
                                out var recovered,
                                out processProof))
                        {
                            slot = SaveSlot(recovered with { Phase = "running" });
                            attached = DurableReviewProcess.Attach(slot);
                            continue;
                        }
                    }
                    return await FinalizeInfrastructureAsync(
                        slot,
                        workspace,
                        "ExecutorRestarted",
                        LostWorkSummary(slot, processProof),
                        shutdown);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), shutdown);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested && workerStarted)
        {
            // A handoff must not let the lease expire in the restart window. The
            // replacement instance renews with exactly this authority, so the
            // outgoing instance buys it enough runway to get there.
            await ExtendLeaseForHandoffAsync(attempt.AttemptId);
            SaveSlot(slot with { Phase = "handed-off" });
            _log(
                $"review daemon handoff attempt={attempt.AttemptId} fence={_authority.Lease.Fence} " +
                $"pid={slot.ProcessId}; detached worker left running for replacement adoption");
            return 0;
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _log($"review heartbeat stopped with error: {exception.Message}");
            }
        }
    }

    private async Task<int> FinalizeResultAsync(
        PersistedReviewSlot slot,
        RemoteReviewWorkspace workspace,
        DetachedReviewResult result,
        CancellationToken ct)
    {
        if (result.Evidence is null)
        {
            return await FinalizeInfrastructureAsync(
                slot,
                workspace,
                result.FailureClassification ?? "ReviewWorkerFailed",
                result.Summary ?? "Detached review worker returned no execution evidence.",
                ct);
        }

        var evidence = result.Evidence;
        slot = slot with
        {
            ReportPendingSinceUtc = slot.ReportPendingSinceUtc ?? result.CompletedAtUtc,
        };
        return await SubmitReportAndCleanupAsync(
            slot,
            workspace,
            evidence,
            result.FailureClassification,
            result.Summary ?? ExecutionSummary(evidence),
            ct);
    }

    private Task<int> FinalizeInfrastructureAsync(
        PersistedReviewSlot slot,
        RemoteReviewWorkspace workspace,
        string classification,
        string summary,
        CancellationToken ct)
    {
        var subject = slot.Claim.Subject!;
        var evidence = InfrastructureEvidence(
            workspace,
            subject,
            _workspaceNamespace,
            classification);
        slot = slot with
        {
            ReportPendingSinceUtc = slot.ReportPendingSinceUtc ?? DateTime.UtcNow,
        };
        return SubmitReportAndCleanupAsync(
            slot,
            workspace,
            evidence,
            classification,
            summary,
            ct);
    }

    private async Task<int> SubmitReportAndCleanupAsync(
        PersistedReviewSlot slot,
        RemoteReviewWorkspace workspace,
        ReviewExecutionEvidence evidence,
        string? failureClassification,
        string summary,
        CancellationToken ct)
    {
        var attempt = slot.Claim.Attempt!;
        if (failureClassification is not null)
        {
            var capabilityLease = _authority.Lease;
            _log(
                $"review infrastructure outcome attempt={attempt.AttemptId} " +
                $"classification={failureClassification}: {summary}");
            var failedCapability = CapabilityFor(failureClassification);
            if (failedCapability is not null)
            {
                await CapabilityFailureReporter.TryReportAsync(
                    _client,
                    _log,
                    failedCapability,
                    failureClassification,
                    summary.Length <= 500 ? summary : summary[..500],
                    $"review-capability:{attempt.AttemptId}:{capabilityLease.Fence}:{failedCapability}",
                    "review",
                    attempt.AttemptId,
                    capabilityLease.Fence,
                    CancellationToken.None);
            }
        }

        ReviewReportDto report;
        while (true)
        {
            // Built inside the loop: a takeover between two submission attempts
            // re-fences the slot, and the report must carry the authority that
            // is current when it is sent.
            var lease = _authority.Lease;
            var request = new ReviewReportRequest(
                lease.ExecutorId,
                lease.InstanceId,
                lease.LeaseId,
                lease.Fence,
                $"review-report:{attempt.AttemptId}:{lease.Fence}",
                failureClassification is null ? evidence.Outcome : "ReviewInfra",
                failureClassification,
                summary,
                evidence.Workspace,
                workspace.EnvironmentEvidence(lease),
                evidence.Commands,
                evidence.Artifacts,
                evidence.Verdicts,
                AuthorityEpoch: lease.AuthorityEpoch);
            var submittedAt = DateTime.UtcNow;
            slot = SaveSlot(slot with
            {
                Phase = "report-submitting",
                ReportPendingSinceUtc = slot.ReportPendingSinceUtc ?? submittedAt,
                ReportSubmissionAttempts = slot.ReportSubmissionAttempts + 1,
                LastReportSubmissionAtUtc = submittedAt,
                LastReportStatusCode = null,
                LastReportErrorCode = null,
                LastReportError = null,
            });
            try
            {
                // The idempotency key makes this atomic write replay-safe. Do not
                // cancel a request once sent; shutdown only interrupts backoff.
                report = await _client.ReportReviewAsync(
                    attempt.AttemptId,
                    request,
                    CancellationToken.None);
                break;
            }
            catch (Exception exception)
            {
                var taskServer = exception as TaskServerException;
                var transportFailure = exception is HttpRequestException or TaskCanceledException;
                var action = ReviewReportSubmissionPolicy.Decide(
                    taskServer?.StatusCode,
                    taskServer?.ErrorCode,
                    transportFailure);
                var error = exception.Message.Length <= 500
                    ? exception.Message
                    : exception.Message[..500];
                slot = SaveSlot(slot with
                {
                    Phase = action == ReviewReportSubmissionAction.Retry
                        ? "report-pending"
                        : "report-rejected-terminal",
                    LastReportStatusCode = taskServer?.StatusCode,
                    LastReportErrorCode = taskServer?.ErrorCode,
                    LastReportError = error,
                    TerminalClassification = action == ReviewReportSubmissionAction.Retry
                        ? null
                        : ReviewReportSubmissionPolicy.TerminalClassification(action),
                });

                if (action != ReviewReportSubmissionAction.Retry)
                {
                    return await FinalizeTerminalReportRejectionAsync(
                        slot,
                        workspace,
                        action,
                        taskServer);
                }

                var delay = ReportRetryDelayOverride?.Invoke(slot.ReportSubmissionAttempts)
                            ?? TaskServerConnectivityMonitor.RetryDelay(
                                _options.PollSeconds,
                                slot.ReportSubmissionAttempts);
                var age = DateTime.UtcNow - slot.ReportPendingSinceUtc!.Value;
                _log(
                    $"review-report-pending attempt={attempt.AttemptId} " +
                    $"submissionAttempts={slot.ReportSubmissionAttempts} " +
                    $"ageSeconds={Math.Max(0, age.TotalSeconds):0} " +
                    $"status={taskServer?.StatusCode.ToString() ?? "transport"} " +
                    $"code={taskServer?.ErrorCode ?? exception.GetType().Name} " +
                    $"retryInMs={(long)delay.TotalMilliseconds}");
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await ExtendLeaseForHandoffAsync(attempt.AttemptId);
                    PersistAuthority();
                    _log(
                        $"review report retry handed off attempt={attempt.AttemptId} " +
                        $"submissionAttempts={slot.ReportSubmissionAttempts} " +
                        $"phase=report-pending");
                    return 0;
                }
            }
        }
        _log(
            $"review report accepted attempt={attempt.AttemptId} outcome={report.Outcome} " +
            $"classification={report.FailureClassification ?? "none"} taskState={report.TaskState} " +
            $"submissionAttempts={slot.ReportSubmissionAttempts}");
        slot = SaveSlot(slot with { Phase = "report-accepted" });

        var removed = false;
        try
        {
            removed = await CleanupWorkspaceAsync(slot, workspace);
        }
        catch (Exception exception)
        {
            _log(
                $"review workspace cleanup failed attempt={attempt.AttemptId} " +
                $"path={slot.WorkspacePath}: {exception.Message}");
        }

        try
        {
            var cleanupLease = _authority.Lease;
            var cleanup = await _client.CleanupReviewAsync(
                attempt.AttemptId,
                new ReviewCleanupRequest(
                    cleanupLease.ExecutorId,
                    cleanupLease.InstanceId,
                    cleanupLease.LeaseId,
                    cleanupLease.Fence,
                    $"review-cleanup:{attempt.AttemptId}:{cleanupLease.Fence}",
                    removed,
                    removed ? null : "WorkspaceCleanupFailed",
                    AuthorityEpoch: cleanupLease.AuthorityEpoch),
                CancellationToken.None);
            _log(
                $"review cleanup recorded attempt={attempt.AttemptId} " +
                $"status={cleanup.Status} retry={cleanup.RetryScheduled}");
        }
        catch (Exception exception)
        {
            _log($"review cleanup report rejected attempt={attempt.AttemptId}: {exception.Message}");
        }
        if (removed)
        {
            _state.Delete(slot);
            _log(
                $"review slot state deleted attempt={attempt.AttemptId} " +
                $"terminalOutcome={report.Outcome}");
        }

        return report.Outcome == "Pass" ? 0 : report.Outcome == "ProductFailure" ? 2 : 3;
    }

    private async Task<int> FinalizeTerminalReportRejectionAsync(
        PersistedReviewSlot slot,
        RemoteReviewWorkspace workspace,
        ReviewReportSubmissionAction action,
        TaskServerException? exception)
    {
        var classification = ReviewReportSubmissionPolicy.TerminalClassification(action);
        var age = DateTime.UtcNow - (slot.ReportPendingSinceUtc ?? slot.UpdatedAtUtc);
        var removed = false;
        try
        {
            removed = await CleanupWorkspaceAsync(slot, workspace);
        }
        catch (Exception cleanupException)
        {
            slot = SaveSlot(slot with { Phase = "terminal-cleanup-pending" });
            _log(
                $"review-report-terminal attempt={slot.AttemptId} classification={classification} " +
                $"status={exception?.StatusCode.ToString() ?? "none"} " +
                $"code={exception?.ErrorCode ?? "none"} " +
                $"submissionAttempts={slot.ReportSubmissionAttempts} " +
                $"ageSeconds={Math.Max(0, age.TotalSeconds):0} cleanup=pending " +
                $"error={cleanupException.GetType().Name}");
            return 3;
        }

        _log(
            $"review-report-terminal attempt={slot.AttemptId} classification={classification} " +
            $"status={exception?.StatusCode.ToString() ?? "none"} " +
            $"code={exception?.ErrorCode ?? "none"} " +
            $"submissionAttempts={slot.ReportSubmissionAttempts} " +
            $"ageSeconds={Math.Max(0, age.TotalSeconds):0} " +
            $"cleanup={(removed ? "removed" : "pending")}");
        if (removed)
        {
            _state.Delete(slot);
            _log(
                $"review slot state deleted attempt={slot.AttemptId} " +
                $"terminalOutcome={classification}");
        }
        else
            SaveSlot(slot with { Phase = "terminal-cleanup-pending" });
        return 3;
    }

    private async Task<bool> CleanupWorkspaceAsync(
        PersistedReviewSlot slot,
        RemoteReviewWorkspace currentWorkspace)
    {
        if (PathsEqual(slot.WorkspacePath, currentWorkspace.RepositoryPath))
            return await currentWorkspace.CleanupAsync(slot.AttemptId);

        // A dead slot can be re-claimed under a fresh fence solely to report its
        // loss. In that case the current claim's derived workspace differs from
        // the old one that must be removed.
        var attemptRoot = Directory.GetParent(slot.WorkspacePath)?.FullName
                          ?? throw new InvalidOperationException(
                              $"Persisted review workspace has no attempt root: {slot.WorkspacePath}");
        var expectedRoot = Path.GetFullPath(_options.ReviewWorkDir)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(attemptRoot);
        if (!target.StartsWith(expectedRoot, StringComparison.Ordinal)
            || string.Equals(
                target.TrimEnd(Path.DirectorySeparatorChar),
                expectedRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Refusing persisted review cleanup outside the configured review root.");
        await CliProcessReaper.ReapWorkspaceAsync(target, slot.AttemptId, _log, CancellationToken.None);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        return !Directory.Exists(target);
    }

    private async Task RenewLoopAsync(string taskId, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Max(1, Math.Min(_options.HeartbeatSeconds, Math.Max(1, _options.TtlSeconds / 3)))));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                await RenewOnceAsync(_options.TtlSeconds, stop);
            }
            catch (Exception exception) when (!stop.IsCancellationRequested)
            {
                if (!await TryRecoverAuthorityAsync("heartbeat", taskId, exception, stop)) return;
            }
        }
    }

    /// <summary>
    /// Renews the current lease and adopts the server's answer, so the persisted
    /// slot carries a live expiry for the next reconciliation pass.
    /// </summary>
    private async Task RenewOnceAsync(int ttlSeconds, CancellationToken ct)
    {
        var lease = _authority.Lease;
        var sequence = Interlocked.Increment(ref _renewSequence);
        var renewed = await _client.RenewReviewLeaseAsync(
            lease.AttemptId,
            new ReviewLeaseRenewRequest(
                lease.ExecutorId,
                lease.InstanceId,
                lease.LeaseId,
                lease.Fence,
                $"review-renew:{lease.AttemptId}:{lease.Fence}:{_client.RunnerInstanceId}:{sequence}",
                ttlSeconds,
                AuthorityEpoch: lease.AuthorityEpoch),
            ct);
        _authority.Rebind(_authority.Attempt, renewed);
        PersistAuthority();
    }

    /// <summary>
    /// Confirms the adopted authority before the replacement instance starts
    /// heartbeating. A refusal here is repaired (re-registration, then takeover
    /// under a higher fence) while the worker still exists; only a genuinely
    /// gone or superseded attempt falls through to the report path.
    /// </summary>
    private async Task<PersistedReviewSlot> VerifyAdoptedAuthorityAsync(PersistedReviewSlot slot)
    {
        var attempt = slot.Claim.Attempt!;
        try
        {
            // Never cancelled: adoption verification is the whole point of the
            // restart window and must complete even as the daemon is told to go.
            await RenewOnceAsync(_options.TtlSeconds, CancellationToken.None);
            _log(
                $"review adoption lease verified attempt={attempt.AttemptId} " +
                $"fence={_authority.Lease.Fence} expiresAt={_authority.Lease.ExpiresAt:O}");
        }
        catch (Exception exception)
        {
            await TryRecoverAuthorityAsync(
                "adoption",
                attempt.TaskId,
                exception,
                CancellationToken.None);
        }
        return SaveSlot(slot);
    }

    /// <summary>
    /// Repairs a refused lease renewal. Returns true when the slot still holds
    /// write authority (kept, re-adopted, or re-claimed under a higher fence)
    /// and false when nothing can be recovered.
    /// </summary>
    private async Task<bool> TryRecoverAuthorityAsync(
        string context,
        string taskId,
        Exception exception,
        CancellationToken ct)
    {
        var attemptId = _authority.Lease.AttemptId;
        var taskServer = exception as TaskServerException;
        var transportFailure = exception is HttpRequestException or TaskCanceledException;
        var action = ReviewLeaseRecoveryPolicy.Decide(
            taskServer?.StatusCode,
            taskServer?.ErrorCode,
            transportFailure);

        if (action == ReviewLeaseRecoveryAction.RetryLater)
        {
            _log(
                $"review lease renew failed attempt={attemptId} scope={context}; " +
                $"retrying next tick: {exception.Message}");
            return true;
        }

        if (action == ReviewLeaseRecoveryAction.ReRegister)
        {
            var lease = _authority.Lease;
            var adopted = false;
            try
            {
                adopted = await _client.ReRegisterAttemptAsync(
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        attemptId,
                        taskId,
                        lease.LeaseId,
                        lease.Fence,
                        lease.AuthorityEpoch,
                        lease.InstanceId),
                    ct);
            }
            catch (Exception registrationException) when (
                registrationException is not OperationCanceledException)
            {
                _log(
                    $"review lease re-adoption failed attempt={attemptId}: " +
                    registrationException.Message);
            }
            action = ReviewLeaseRecoveryPolicy.AfterReRegistration(adopted, WorkerStillLive());
            if (action == ReviewLeaseRecoveryAction.RetryLater)
            {
                try
                {
                    // Registration saying "adopted" is not enough. Prove that
                    // the Task Server will renew the authority now, and persist
                    // its returned expiry before any worker or report continues.
                    await RenewOnceAsync(_options.TtlSeconds, ct);
                    _log(
                        $"review lease authority re-adopted and renewed attempt={attemptId} " +
                        $"fence={_authority.Lease.Fence} scope={context} " +
                        $"after HTTP {taskServer?.StatusCode}");
                    return true;
                }
                catch (Exception renewalException) when (
                    renewalException is not OperationCanceledException)
                {
                    var renewedTaskServer = renewalException as TaskServerException;
                    if (ReviewLeaseRecoveryPolicy.Decide(
                            renewedTaskServer?.StatusCode,
                            renewedTaskServer?.ErrorCode,
                            renewalException is HttpRequestException or TaskCanceledException)
                        == ReviewLeaseRecoveryAction.Abandon)
                    {
                        _log(
                            $"review lease re-adoption verification refused attempt={attemptId} " +
                            $"scope={context}: {renewalException.Message}");
                        return false;
                    }

                    taskServer = renewedTaskServer ?? taskServer;
                    _log(
                        $"review lease re-adoption did not renew attempt={attemptId} " +
                        $"scope={context}; attempting a fenced re-claim: {renewalException.Message}");
                    action = WorkerStillLive()
                        ? ReviewLeaseRecoveryAction.ReClaim
                        : ReviewLeaseRecoveryAction.Abandon;
                }
            }
        }

        if (action == ReviewLeaseRecoveryAction.ReClaim
            && await TryReClaimAsync(context, taskServer, ct))
        {
            return true;
        }

        _log(
            $"review lease authority lost attempt={attemptId} " +
            $"({taskServer?.StatusCode.ToString() ?? "transport"}) scope={context} " +
            $"code={taskServer?.ErrorCode ?? exception.GetType().Name}; " +
            $"stopping heartbeat: {exception.Message}");
        return false;
    }

    /// <summary>
    /// Takes the attempt over under a higher fence. The running worker and its
    /// workspace are untouched: only the write authority the report will travel
    /// under is replaced.
    /// </summary>
    private async Task<bool> TryReClaimAsync(
        string context,
        TaskServerException? refusal,
        CancellationToken ct)
    {
        var previous = _authority.Lease;
        try
        {
            var reclaimed = await _client.ReClaimReviewAsync(
                previous.AttemptId,
                new ReviewReClaimRequest(
                    previous.ExecutorId,
                    _client.RunnerInstanceId,
                    previous.LeaseId,
                    previous.Fence,
                    $"review-reclaim:{previous.AttemptId}:{previous.Fence}:{_client.RunnerInstanceId}",
                    _options.TtlSeconds),
                ct);
            if (!string.Equals(reclaimed.Status, "claimed", StringComparison.OrdinalIgnoreCase)
                || reclaimed.Attempt is null
                || reclaimed.Lease is null)
            {
                _log(
                    $"review lease re-claim refused attempt={previous.AttemptId} " +
                    $"scope={context} status={reclaimed.Status} " +
                    $"reason={reclaimed.Message ?? "none"}");
                return false;
            }
            if (!string.Equals(
                    reclaimed.Attempt.SubjectId,
                    _authority.Claim.Subject!.SubjectId,
                    StringComparison.Ordinal))
            {
                _log(
                    $"review lease re-claim rejected attempt={previous.AttemptId} scope={context}: " +
                    $"server subject {reclaimed.Attempt.SubjectId} is not the running " +
                    $"subject {_authority.Claim.Subject!.SubjectId}");
                return false;
            }

            _authority.Rebind(reclaimed.Attempt, reclaimed.Lease);
            PersistAuthority();
            _log(
                $"review lease re-claimed attempt={previous.AttemptId} scope={context} " +
                $"previousFence={previous.Fence} fence={reclaimed.Lease.Fence} " +
                $"after HTTP {refusal?.StatusCode.ToString() ?? "transport"} " +
                $"{refusal?.ErrorCode ?? "none"}; running worker and workspace kept");
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log(
                $"review lease re-claim failed attempt={previous.AttemptId} " +
                $"scope={context}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// A takeover is only legitimate while this host still owns the work: either
    /// the detached worker runs or it left a durable result to deliver.
    /// </summary>
    private bool WorkerStillLive()
    {
        PersistedReviewSlot slot;
        lock (_slotGate) slot = _slot;
        return DurableReviewProcess.HasCompleted(slot)
               || DurableReviewProcess.VerifyLive(slot, out _);
    }

    /// <summary>
    /// Buys the handed-off lease enough runway to survive the restart window.
    /// The Task Server clamps the request to its own ceiling.
    /// </summary>
    private async Task ExtendLeaseForHandoffAsync(string attemptId)
    {
        try
        {
            await RenewOnceAsync(_options.HandoffLeaseTtlSeconds, CancellationToken.None);
            _log(
                $"review handoff lease extended attempt={attemptId} " +
                $"fence={_authority.Lease.Fence} " +
                $"requestedTtlSeconds={_options.HandoffLeaseTtlSeconds} " +
                $"expiresAt={_authority.Lease.ExpiresAt:O}");
        }
        catch (Exception exception)
        {
            _log(
                $"review handoff lease extension failed attempt={attemptId}: {exception.Message}; " +
                "the replacement instance verifies and re-claims on adoption");
        }
    }

    private static string LostWorkSummary(PersistedReviewSlot slot, string reason)
    {
        var progress = DurableReviewProcess.Attach(slot).ReadProgress();
        var completed = progress?.CompletedStepIds.Count ?? 0;
        var planned = slot.Claim.Subject?.Plan.Commands.Count ?? 0;
        var seconds = progress?.CompletedCommandSeconds ?? 0;
        var steps = completed == 0
            ? "none"
            : string.Join(", ", progress!.CompletedStepIds);
        return
            $"The replacement daemon could not adopt the persisted review process: {reason}. " +
            $"Lost work extent: {completed} of {planned} review commands completed " +
            $"({seconds:0.###} command-seconds; steps: {steps}). " +
            "The immutable subject must be retried because no unproven process may retain review authority.";
    }

    private static string ExecutionSummary(ReviewExecutionEvidence evidence)
    {
        var baseline = evidence.Verdicts
            .Where(verdict => verdict.Classification is
                "BaselineCompared" or "NewTestFailures" or ReviewFlakyTestIndex.VerdictClassification)
            .Select(verdict => $"{verdict.Aspect}: {verdict.Summary}")
            .ToArray();
        if (baseline.Length > 0) return string.Join(" ", baseline);
        return evidence.Outcome == "Pass"
            ? "All applicable remote review aspects passed."
            : "At least one remote review aspect found a product concern.";
    }

    private static ReviewExecutionEvidence InfrastructureEvidence(
        RemoteReviewWorkspace workspace,
        ReviewSubjectDto subject,
        string workspaceNamespace,
        string classification)
    {
        var repositoryId = classification == "RepositoryMismatch" ? "unknown" : subject.RepositoryId;
        var actualHead = classification == "ShaMismatch" ? "unknown" : subject.ExpectedResultSha;
        var dirtyBefore = classification == "DirtyBefore";
        var proof = new ReviewWorkspaceProofDto(
            repositoryId,
            subject.ExpectedResultSha,
            actualHead,
            "unknown",
            dirtyBefore,
            false,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(workspace.AttemptRoot))).ToLowerInvariant(),
            workspaceNamespace);
        return new ReviewExecutionEvidence("ReviewInfra", proof, [], [], []);
    }

    private static string? CapabilityFor(string classification)
        => classification switch
        {
            "SnapshotUnavailable" or "RepositoryMismatch" or "ShaMismatch"
                => CapabilityProtocol.RepositoryAccess,
            "PreparationFailed" => ReviewCapabilities.DependencyPreparation,
            "ToolUnavailable" => ReviewCapabilities.SemanticReview,
            "VisionUnavailable" => CapabilityProtocol.Vision,
            "DiskFull" => CapabilityProtocol.Disk,
            "LeaseAuthorityInvalid" => CapabilityProtocol.LeaseAuthority,
            _ => null,
        };

    private static void ValidateClaim(ReviewClaimResponse claim)
    {
        if (claim.Attempt is null || claim.Subject is null || claim.Lease is null)
            throw new ArgumentException(
                "Claim must contain an attempt, immutable subject, and review lease.",
                nameof(claim));
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
