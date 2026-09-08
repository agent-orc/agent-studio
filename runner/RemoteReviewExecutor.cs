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
        return await RunPersistedAsync(slot, workspace, shutdown, reattach: true);
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
        slot = _state.Save(slot with
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
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var heartbeat = RenewLoopAsync(
            attempt.AttemptId, attempt.TaskId, lease, heartbeatStop.Token);
        var workerStarted = slot.ProcessId is not null || DurableReviewProcess.HasCompleted(slot);
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
                    slot = _state.Save(slot with { Phase = "finalizing" });
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

                slot = _state.Save(slot with { Phase = "launching" });
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
                slot = _state.Save(slot with
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
                    slot = _state.Save(slot with { Phase = "finalizing" });
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
                            slot = _state.Save(recovered with { Phase = "running" });
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
            _state.Save(slot with { Phase = "handed-off" });
            _log(
                $"review daemon handoff attempt={attempt.AttemptId} fence={lease.Fence} " +
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
        var lease = slot.Claim.Lease!;
        var evidence = InfrastructureEvidence(workspace, subject, lease, classification);
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
        var lease = slot.Claim.Lease!;
        if (failureClassification is not null)
        {
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
                    $"review-capability:{attempt.AttemptId}:{lease.Fence}:{failedCapability}",
                    "review",
                    attempt.AttemptId,
                    lease.Fence,
                    CancellationToken.None);
            }
        }

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
            workspace.EnvironmentEvidence(),
            evidence.Commands,
            evidence.Artifacts,
            evidence.Verdicts,
            AuthorityEpoch: lease.AuthorityEpoch);

        ReviewReportDto report;
        while (true)
        {
            var submittedAt = DateTime.UtcNow;
            slot = _state.Save(slot with
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
                slot = _state.Save(slot with
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
        slot = _state.Save(slot with { Phase = "report-accepted" });

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
            var cleanup = await _client.CleanupReviewAsync(
                attempt.AttemptId,
                new ReviewCleanupRequest(
                    lease.ExecutorId,
                    lease.InstanceId,
                    lease.LeaseId,
                    lease.Fence,
                    $"review-cleanup:{attempt.AttemptId}:{lease.Fence}",
                    removed,
                    removed ? null : "WorkspaceCleanupFailed",
                    AuthorityEpoch: lease.AuthorityEpoch),
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
            slot = _state.Save(slot with { Phase = "terminal-cleanup-pending" });
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
            _state.Save(slot with { Phase = "terminal-cleanup-pending" });
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

    private async Task RenewLoopAsync(
        string attemptId,
        string taskId,
        ReviewLeaseDto lease,
        CancellationToken stop)
    {
        var sequence = 0L;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Max(1, Math.Min(_options.HeartbeatSeconds, Math.Max(1, _options.TtlSeconds / 3)))));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                await _client.RenewReviewLeaseAsync(
                    attemptId,
                    new ReviewLeaseRenewRequest(
                        lease.ExecutorId,
                        lease.InstanceId,
                        lease.LeaseId,
                        lease.Fence,
                        $"review-renew:{attemptId}:{lease.Fence}:{_client.RunnerInstanceId}:{++sequence}",
                        _options.TtlSeconds,
                        AuthorityEpoch: lease.AuthorityEpoch),
                    stop);
            }
            catch (TaskServerException dead) when (dead.StatusCode is 404 or 409)
            {
                try
                {
                    var reported = new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        attemptId,
                        taskId,
                        lease.LeaseId,
                        lease.Fence,
                        lease.AuthorityEpoch,
                        lease.InstanceId);
                    if (await _client.ReRegisterAttemptAsync(reported, stop))
                    {
                        _log(
                            $"review lease authority re-adopted attempt={attemptId} " +
                            $"fence={lease.Fence} after HTTP {dead.StatusCode}");
                        continue;
                    }
                }
                catch (Exception registrationException) when (
                    registrationException is not OperationCanceledException)
                {
                    _log(
                        $"review lease re-adoption failed attempt={attemptId}: " +
                        registrationException.Message);
                }
                _log(
                    $"review lease authority lost attempt={attemptId} ({dead.StatusCode}); " +
                    $"stopping heartbeat: {dead.Message}");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log(
                    $"review lease renew failed attempt={attemptId}; " +
                    $"retrying next tick: {exception.Message}");
            }
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
        ReviewLeaseDto lease,
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
            lease.ResourceNamespace);
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
