using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    private void SchedulePreCheck(BatchGatePendingRecord pending, string baseSha, CancellationToken ct)
    {
        if (_gateSlots is null) return;
        var slots = _gateSlots.RemoteSnapshot(TimeSpan.FromMinutes(5)) ?? _gateSlots.Snapshot();
        lock (_runningPreChecks)
        {
            // Slot zero stays available for a formed batch, including a batch
            // dispatched immediately after this check.
            if (slots.Total < 2 || slots.Busy + _runningPreChecks.Count >= slots.Total - 1
                || _runningPreChecks.ContainsKey(pending.ReviewAttemptId)) return;
            _runningPreChecks[pending.ReviewAttemptId] = RunPreCheckFlightAsync(pending, baseSha, ct);
        }
    }

    private void SchedulePreCheckRed(BatchGatePendingRecord pending,
        BatchGatePreCheckVerdict verdict, CancellationToken ct)
    {
        lock (_runningPreChecks)
        {
            if (_runningPreChecks.ContainsKey(pending.ReviewAttemptId)) return;
            _runningPreChecks[pending.ReviewAttemptId] = RunPreCheckRedFlightAsync(pending, verdict, ct);
        }
    }

    private async Task RunPreCheckFlightAsync(BatchGatePendingRecord pending,
        string baseSha, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var paths = DeliveryPaths(pending.RepositoryPath, pending.Subject.IntegrationBranch,
                pending.Subject.ResultSha);
            if (paths is null) return;
            var started = _time.GetUtcNow();
            var settings = _settings.Get(pending.Subject.Project);
            var gate = await _gate.RunAsync(new BuildTestGateRequest(
                    pending.RepositoryPath, pending.Subject.ResultSha, "batch-pre-check")
                {
                    Project = pending.Subject.Project,
                    JobId = pending.Subject.TaskKey,
                    GateId = "batch-pre-check",
                    SubjectRef = pending.Subject.ResultRef,
                    IntegrationRef = baseSha,
                    RequiredTestLevel = TestExecutionLevels.BatchPreCheck,
                    TestExecution = settings.TestExecution,
                    ReserveBatchSlot = true,
                    QueueWaitTimeout = TimeSpan.FromSeconds(1),
                    DisableVerdictCache = true,
                }, paths, settings.BuildProfile, PostStepMode.Fail,
                TimeSpan.FromMinutes(settings.BatchGate.GateTimeoutMinutes), ct).ConfigureAwait(false);
            if (gate.IsInfrastructureFailure || gate.Verdict is not (BuildTestGateVerdict.Ok or BuildTestGateVerdict.Fail)
                || !string.Equals(gate.TestedSha, pending.Subject.ResultSha,
                    StringComparison.OrdinalIgnoreCase)) return;
            var failed = gate.Processes.SelectMany(process => process.TestReport?.FailedTests ?? [])
                .Concat(GateFlakyRerunPolicy.ParseFailedTests(gate.Output))
                .Distinct(StringComparer.Ordinal).Take(32).ToArray();
            var routeLines = gate.Output.Replace("\r", "").Split('\n')
                .Where(line => line.Contains("/api/", StringComparison.OrdinalIgnoreCase))
                .Take(30).Select(line => line.Trim()).ToArray();
            var reason = string.Join('\n', new[] { gate.Reason }
                .Concat(failed.Select(test => "Failed test: " + test))
                .Concat(routeLines)).Trim();
            var verdict = new BatchGatePreCheckVerdict(pending.Subject.Project,
                pending.Subject.TaskKey, pending.ReviewAttemptId, pending.Subject.ResultSha,
                baseSha, pending.Subject.GateProfileDigest,
                gate.Verdict == BuildTestGateVerdict.Ok ? "green" : "red",
                reason.Length > 6000 ? reason[..6000] : reason, failed,
                started, _time.GetUtcNow(),
                gate.Verdict == BuildTestGateVerdict.Fail
                    ? EstimatedPreCheckMinutesSaved(pending.Subject.Project, settings.BatchGate.CloseSize)
                    : 0);
            _store.RecordPreCheck(verdict);
            if (verdict.Outcome == "red")
                await ReturnPreCheckRedAsync(pending, verdict, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("batch-pre-check-cancelled task={TaskKey}", pending.Subject.TaskKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "batch-pre-check-interrupted task={TaskKey}", pending.Subject.TaskKey);
        }
        finally
        {
            lock (_runningPreChecks) _runningPreChecks.Remove(pending.ReviewAttemptId);
        }
    }

    private double EstimatedPreCheckMinutesSaved(string project, int plannedSize)
    {
        var durations = _store.ListExecutions(project)
            .Where(item => item.BatchId is not null && item.CompletedAtUtc >= item.StartedAtUtc)
            .OrderByDescending(item => item.CompletedAtUtc).Take(20)
            .Select(item => (item.CompletedAtUtc - item.StartedAtUtc).TotalMinutes)
            .Order().ToArray();
        if (durations.Length == 0) return 0;
        var median = durations[durations.Length / 2];
        return median * (2 + BatchGatePolicy.DiagnosticRunsPerCycle(Math.Max(2, plannedSize)));
    }

    private async Task RunPreCheckRedFlightAsync(BatchGatePendingRecord pending,
        BatchGatePreCheckVerdict verdict, CancellationToken ct)
    {
        await Task.Yield();
        try { await ReturnPreCheckRedAsync(pending, verdict, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("batch-pre-check-red-handoff-cancelled task={TaskKey}", pending.Subject.TaskKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "batch-pre-check-red-handoff-failed task={TaskKey}", pending.Subject.TaskKey);
        }
        finally
        {
            lock (_runningPreChecks) _runningPreChecks.Remove(pending.ReviewAttemptId);
        }
    }

    private async Task ReturnPreCheckRedAsync(BatchGatePendingRecord pending,
        BatchGatePreCheckVerdict verdict, CancellationToken ct)
    {
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (task?.State != TaskStates.AutoReview || !Refresh(pending).Subject.CurrentGeneration
            || BatchGateOwnershipStore.Read(task.FolderPath)?.BatchId is not null) return;
        var review = _authority.GetTaskProjection(pending.Subject.TaskKey).CurrentReviewAttempt;
        if (review?.AttemptId != pending.ReviewAttemptId) return;
        var followUp = "The batch pre-check failed on your delivery. Fix the checks below, "
            + "rerun the focused build and guards, and deliver a new result SHA.\n\n"
            + verdict.Reason;
        _mutations.AppendContinuationNote(task.Id, followUp, task.WatchPath);
        await ReviewDecisionOrchestrator.WriteFollowUpFilesAsync(task.FolderPath,
            followUp, new ReviewDecisionOrchestrator.SteeringContext(
                "batch-pre-check", "red", 0, Reason: verdict.Reason),
            task.Id, _logger, ct).ConfigureAwait(false);
        var moved = await _transitions.MoveAsync(task.Id, TaskStates.Ready,
            task.WatchPath, ct, cause: $"batch-pre-check:{pending.ReviewAttemptId}",
            reason: verdict.Reason,
            authorityWrite: new AttemptWriteReference(review.AttemptId, review.LastFence,
                review.AuthorityEpoch, $"batch-pre-check-red:{review.AttemptId}"),
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.QualityLoop,
            transitionDetail: "batch-pre-check-red").ConfigureAwait(false);
        if (moved.Status != MoveJobStatus.Success) return;
        _store.ResolvePending(pending.ReviewAttemptId, "pre-check-red");
        _store.ClearQueueWait(pending.ReviewAttemptId);
        BatchGateOwnershipStore.ClearIfReviewAttempt(moved.NewFolderPath ?? task.FolderPath,
            pending.ReviewAttemptId);
    }
}
