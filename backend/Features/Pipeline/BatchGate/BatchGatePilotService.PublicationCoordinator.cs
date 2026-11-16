using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Bus;
using HostDiskPressureDecision = AgentStudio.TaskServer.Contracts.HostDiskPressureDecision;
using HostDiskPressureLevel = AgentStudio.TaskServer.Contracts.HostDiskPressureLevel;
using HostDiskPressurePolicy = AgentStudio.TaskServer.Contracts.HostDiskPressurePolicy;
using HostDiskPressureThresholds = AgentStudio.TaskServer.Contracts.HostDiskPressureThresholds;
using HostFilesystemProbe = AgentStudio.TaskServer.Contracts.HostFilesystemProbe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Durable publication transitions shared by the first flight and restart recovery.
/// A step is append-only and names one run and candidate SHA; an interrupted
/// effect is checked against that identity before the next step is attempted.
/// </summary>
public sealed partial class BatchGatePilotService
{
    private void PublicationStep(BatchGateManifest manifest, BatchGateRunRecord run,
        string step, string? taskKey = null)
        => _store.RecordPublicationStep(new BatchPublicationStep(manifest.BatchId,
            run.BatchRunId, run.CandidateSha, step, taskKey, _time.GetUtcNow()));

    private bool PublicationStepComplete(BatchGateManifest manifest, BatchGateRunRecord run,
        string step, string? taskKey = null)
    {
        var recorded = _store.ReadPublicationStep(manifest.BatchId, step, taskKey);
        if (recorded is null) return false;
        if (recorded.BatchId != manifest.BatchId || recorded.BatchRunId != run.BatchRunId
            || recorded.CandidateSha != run.CandidateSha || recorded.Step != step
            || recorded.TaskKey != taskKey)
            throw new InvalidDataException("Publication transition belongs to a different SHA.");
        return true;
    }

    private bool PassingPublicationRun(BatchGateManifest manifest, BatchGateRunRecord run)
        => run.BatchId == manifest.BatchId
            && run.MembershipDigest == manifest.MembershipDigest
            && run.BaseSha == manifest.BaseSha
            && run.GateProfileDigest == manifest.Scope.GateProfileDigest
            && _store.ReadVerdict(manifest.BatchId, run.BatchRunId) is { } verdict
            && verdict.Outcome == "pass"
            && verdict.MembershipDigest == run.MembershipDigest
            && verdict.TestedCandidateSha == run.CandidateSha
            && verdict.GateProfileDigest == run.GateProfileDigest
            && verdict.EvidencePath == run.EvidencePath
            && File.Exists(run.EvidencePath);

    private void HoldAmbiguousPublication(BatchGateManifest manifest, BatchGateState state, long fence)
    {
        // Publishing remains the durable hold: the generic Paused route hands
        // members to per-task gates, which could attribute a different SHA.
        if (state.Reason == "publication-remote-proof-ambiguous") return;
        State(manifest, BatchPhase.Publishing, state.CandidateSha, fence,
            "publication-remote-proof-ambiguous", state.BatchRunId, state.RefMutationFence);
    }

    private async Task RecoverPublishedAsync(CancellationToken ct)
    {
        var waiting = _store.ListPending();
        var allPending = _store.ListAllPending();
        var fetchedBases = new Dictionary<(string Repo, string Branch), string?>();
        foreach (var manifest in _store.ListManifests())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
            var publication = _store.ReadPublication(manifest.BatchId);
            var phase = _store.LatestState(manifest.BatchId).Phase;
            if (phase is not (BatchPhase.Publishing or BatchPhase.Published)) continue;
            if (phase == BatchPhase.Publishing)
            {
                if (manifest.StackedOnBatchId is { } predecessorId
                    && (_store.ReadPublication(predecessorId) is not { } predecessor
                        || predecessor.TestedCandidateSha != manifest.BaseSha
                        || predecessor.VerifiedRemoteSha != manifest.BaseSha))
                    continue;
                var pendingRepo = waiting.FirstOrDefault(item =>
                    manifest.Members.Any(member => member.TaskKey == item.Subject.TaskKey
                        && member.RunAttempt == item.Subject.RunAttempt));
                if (pendingRepo is null) continue;
                BatchCoordinatorLease? lease;
                try { lease = _leases.TryAcquire(LeaseScope(manifest), "batch-publication-recovery"); }
                catch (IOException) { continue; }
                if (lease is null) continue;
                try
                {
                    using var refLease = await _refLeases.AcquireAsync(
                        manifest.Scope.Project, pendingRepo.RepositoryPath,
                        manifest.Scope.IntegrationBranch, ct).ConfigureAwait(false);
                    if (!_leases.IsCurrent(lease)
                        || !RefMutationLeaseService.IsCurrent(refLease)) continue;
                    if (publication is null)
                    {
                        var state = _store.LatestState(manifest.BatchId);
                        if (state.BatchRunId is null || state.CandidateSha is null)
                        {
                            HoldAmbiguousPublication(manifest, state, lease.Fence);
                            continue;
                        }
                        var matching = _store.ListRuns(manifest.BatchId)
                            .Where(run => run.BatchRunId == state.BatchRunId
                                && run.CandidateSha == state.CandidateSha
                                && run.CoordinatorFence <= state.CoordinatorFence
                                && run.MembershipDigest == manifest.MembershipDigest
                                && run.BaseSha == manifest.BaseSha
                                && run.GateProfileDigest == manifest.Scope.GateProfileDigest
                                && _store.ReadVerdict(manifest.BatchId, run.BatchRunId) is { } verdict
                                && verdict.Outcome == "pass"
                                && verdict.MembershipDigest == manifest.MembershipDigest
                                && verdict.TestedCandidateSha == run.CandidateSha
                                && verdict.GateProfileDigest == run.GateProfileDigest
                                && verdict.EvidencePath == run.EvidencePath
                                && File.Exists(run.EvidencePath))
                            .ToArray();
                        if (matching.Length != 1)
                        {
                            HoldAmbiguousPublication(manifest, state, lease.Fence);
                            continue;
                        }
                        var recoveredRun = matching[0];
                        PublicationStep(manifest, recoveredRun, "publishing");
                        var remote = _git.FetchBatchIntegrationTip(pendingRepo.RepositoryPath,
                            manifest.Scope.IntegrationBranch, ct);
                        if (string.Equals(remote, manifest.BaseSha, StringComparison.OrdinalIgnoreCase))
                        {
                            var admitted = manifest.Members.Where(member =>
                                _store.TryReadReplay(manifest.BatchId, member.TaskKey)?.Outcome == "admitted").ToArray();
                            if (!BatchRouteEnabled(manifest)
                                || admitted.Any(member => _store.HasEject(manifest.Scope.Project, member.TaskKey)
                                    || waiting.FirstOrDefault(item => item.Subject.TaskKey == member.TaskKey
                                        && item.Subject.RunAttempt == member.RunAttempt) is not { } item
                                    || !Refresh(item).Subject.CurrentGeneration)
                                || !_leases.IsCurrent(lease) || !RefMutationLeaseService.IsCurrent(refLease))
                            {
                                HoldAmbiguousPublication(manifest, state, lease.Fence);
                                continue;
                            }
                            PublicationStep(manifest, recoveredRun, "push-intent");
                            var pushed = _git.PublishBatchCandidate(pendingRepo.RepositoryPath,
                                manifest.Scope.IntegrationBranch, manifest.BaseSha,
                                recoveredRun.CandidateSha, ct,
                                () => _leases.IsCurrent(lease)
                                    && RefMutationLeaseService.IsCurrent(refLease));
                            remote = pushed.Success ? pushed.Sha : _git.FetchBatchIntegrationTip(
                                pendingRepo.RepositoryPath, manifest.Scope.IntegrationBranch, ct);
                        }
                        if (!string.Equals(remote, state.CandidateSha,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            HoldAmbiguousPublication(manifest, state, lease.Fence);
                            continue;
                        }
                        if (!_leases.IsCurrent(lease)
                            || !RefMutationLeaseService.IsCurrent(refLease)) continue;
                        PublicationStep(manifest, recoveredRun, "remote-verified");
                        publication = new BatchGatePublication(
                            manifest.BatchId, manifest.MembershipDigest,
                            recoveredRun.BatchRunId, manifest.BaseSha,
                            recoveredRun.CandidateSha, recoveredRun.CandidateSha,
                            recoveredRun.CoordinatorFence,
                            state.RefMutationFence ?? refLease.Fence,
                            _time.GetUtcNow());
                        _store.RecordPublication(publication);
                        PublicationStep(manifest, recoveredRun, "receipt");
                    }
                    var publishedRun = _store.ReadRun(
                        manifest.BatchId, publication.BatchRunId);
                    if (publication.MembershipDigest != manifest.MembershipDigest
                        || publication.PreTipSha != manifest.BaseSha
                        || publication.TestedCandidateSha != publishedRun.CandidateSha
                        || publication.VerifiedRemoteSha != publishedRun.CandidateSha
                        || publication.BatchRunId != _store.LatestState(manifest.BatchId).BatchRunId
                        || !PassingPublicationRun(manifest, publishedRun))
                        throw new InvalidDataException("batch-gate-evidence-missing");
                    PublicationStep(manifest, publishedRun, "receipt");
                    RecordPublishedBaseVerdict(manifest, publishedRun);
                    if (!_leases.IsCurrent(lease)
                        || !RefMutationLeaseService.IsCurrent(refLease)) continue;
                    State(manifest, BatchPhase.Published,
                        publication.TestedCandidateSha, lease.Fence,
                        "resumed verified publication");
                    PublicationStep(manifest, publishedRun, "published");
                    _git.FetchBatchIntegrationTip(pendingRepo.RepositoryPath,
                        manifest.Scope.IntegrationBranch, ct);
                    TrySyncPublishedCheckout(pendingRepo.RepositoryPath,
                        manifest.Scope.IntegrationBranch, publication.TestedCandidateSha);
                }
                finally { _leases.Release(lease); }
            }
            if (publication is null) continue;
            var run = _store.ReadRun(manifest.BatchId, publication.BatchRunId);
            if (publication.MembershipDigest != manifest.MembershipDigest
                || publication.PreTipSha != manifest.BaseSha
                || publication.TestedCandidateSha != run.CandidateSha
                || publication.VerifiedRemoteSha != run.CandidateSha
                || !PassingPublicationRun(manifest, run))
                throw new InvalidDataException("batch-gate-evidence-missing");
            PublicationStep(manifest, run, "published");
            var releaseRepo = allPending.FirstOrDefault(item =>
                manifest.Members.Any(member => member.TaskKey == item.Subject.TaskKey
                    && member.RunAttempt == item.Subject.RunAttempt));
            if (releaseRepo is not null)
                _git.FetchBatchIntegrationTip(releaseRepo.RepositoryPath,
                    manifest.Scope.IntegrationBranch, ct);
            foreach (var member in manifest.Members)
            {
                if (PublicationStepComplete(manifest, run, "released", member.TaskKey)) continue;
                var pending = allPending.FirstOrDefault(item =>
                    item.Subject.TaskKey == member.TaskKey
                    && item.Subject.RunAttempt == member.RunAttempt);
                if (pending is null) continue;
                var replay = _store.TryReadReplay(manifest.BatchId, member.TaskKey);
                if (replay?.Outcome == "conflict")
                {
                    await StartConflictRoundAsync(pending, replay).ConfigureAwait(false);
                    continue;
                }
                // A restart can lose the fallback handoff before its first
                // marker. Containment may release it without another gate.
                if (replay?.Outcome == "unattributable")
                {
                    HandOffPublishedEject(manifest, pending, fetchedBases, ct);
                    continue;
                }
                if (replay?.Outcome != "admitted") continue;
                var task = _scanner.FindJob(member.TaskKey, pending.WatchPath);
                if (task?.State == TaskStates.HumanReview)
                {
                    var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
                    var projection = _authority.GetTaskProjection(AuthorityKey(task));
                    if (ownership is not null && BatchGateOwnershipStore.ReleaseFailure(
                            ownership, projection, _store, task.FolderPath) is null)
                    {
                        RecordBatchIntegration(task.FolderPath, manifest, run, replay);
                        PublicationStep(manifest, run, "attributed", member.TaskKey);
                        _store.ResolvePending(pending.ReviewAttemptId, "published");
                        _store.ClearQueueWait(pending.ReviewAttemptId);
                        PublicationStep(manifest, run, "released", member.TaskKey);
                    }
                    continue;
                }
                if (!Refresh(pending).Subject.CurrentGeneration)
                {
                    ResolveSuperseded(pending, "superseded-after-publication");
                    continue;
                }
                await ReleaseMemberIsolatedAsync(manifest, run, pending, ct).ConfigureAwait(false);
            }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "batch-gate-publication-recovery-failed batch={BatchId} project={Project}",
                    manifest.BatchId, manifest.Scope.Project);
            }
        }
    }


    private async Task PublishGreenAsync(BatchGateManifest manifest,
        BatchGateAssembly assembly, BatchGateRunRecord run,
        BatchGateRunVerdict verdict, BatchCoordinatorLease lease,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, ISet<string> fallbackKeys, CancellationToken ct)
    {
        // AGT-3569: a flight whose claim was expired or replaced writes nothing
        // more; one whose heartbeat only lapsed fails the lease check below.
        if (!FlightClaimOwned(manifest)) return;
        if (FlightOf(manifest) is { } flight)
            AdvanceFlight(flight, BatchFlightStage.Publishing, cancelOnLoss: false);
        using var stackReservation = manifest.StackedOnBatchId is null || _reservations is null
            ? null : await _reservations.AcquireBatchAsync(manifest.Scope.Project,
                ct).ConfigureAwait(false);
        if (manifest.StackedOnBatchId is { } predecessorId
            && !BatchStackingPolicy.CanPublish(_store.ReadManifest(predecessorId),
                _store.LatestState(predecessorId), _store.ReadPublication(predecessorId), manifest))
        {
            State(manifest, BatchPhase.Abandoned, assembly.CandidateSha, lease.Fence,
                "predecessor did not publish");
            ReturnToPending(manifest);
            return;
        }
        var publicationWait = _time.GetTimestamp();
        using (var refLease = await _refLeases.AcquireAsync(
            manifest.Scope.Project, repo, manifest.Scope.IntegrationBranch,
            ct).ConfigureAwait(false))
        {
            var publicationWaitMs = (long)_time.GetElapsedTime(publicationWait).TotalMilliseconds;
            foreach (var key in assembly.AdmittedKeys)
                Lane(pending[key], "publication-lock-acquired",
                    publicationLockWaitMs: publicationWaitMs, reason: $"batch:{manifest.BatchId}");
            if (!BatchRouteEnabled(manifest))
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "abandoned-by-rollback");
                ReturnToPending(manifest);
                return;
            }
            if (manifest.Members.Any(member =>
                    _store.HasEject(manifest.Scope.Project, member.TaskKey)))
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "member ejected before publication");
                ReturnToPending(manifest);
                return;
            }
            var current = assembly.AdmittedKeys.ToDictionary(
                key => key, key => Refresh(pending[key]).Subject, StringComparer.Ordinal);
            var remoteTip = _git.FetchBatchIntegrationTip(
                repo, manifest.Scope.IntegrationBranch, ct);
            var decision = BatchGatePublicationPolicy.Decide(
                manifest, assembly, run, verdict, current,
                remoteTip ?? string.Empty, _leases.IsCurrent(lease) && FlightClaimLive(manifest),
                RefMutationLeaseService.IsCurrent(refLease),
                _git.IsAncestor(repo, manifest.BaseSha, assembly.CandidateSha));
            if (decision != BatchPublishDecision.FastForward)
            {
                if (decision == BatchPublishDecision.StaleBase
                    && QueueOnMovedDocumentationBase(manifest, pending, repo,
                        remoteTip, lease.Fence))
                    return;
                State(manifest, decision is BatchPublishDecision.StaleBase
                        or BatchPublishDecision.Superseded
                        or BatchPublishDecision.LeaseLost
                        ? BatchPhase.Abandoned : BatchPhase.Paused,
                    assembly.CandidateSha, lease.Fence,
                    decision.ToString());
                if (decision is BatchPublishDecision.StaleBase
                    or BatchPublishDecision.Superseded
                    or BatchPublishDecision.LeaseLost)
                {
                    ReturnToPending(manifest);
                    foreach (var key in assembly.AdmittedKeys.Where(key =>
                                 !Refresh(pending[key]).Subject.CurrentGeneration))
                        ResolveSuperseded(pending[key], "superseded");
                }
                return;
            }
            State(manifest, BatchPhase.Publishing, assembly.CandidateSha,
                lease.Fence, runId: run.BatchRunId, refFence: refLease.Fence);
            PublicationStep(manifest, run, "publishing");
            if (!_leases.IsCurrent(lease) || !RefMutationLeaseService.IsCurrent(refLease)
                || !FlightClaimLive(manifest))
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "lease lost before publication");
                ReturnToPending(manifest);
                return;
            }
            if (!BatchRouteEnabled(manifest))
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "abandoned-by-rollback");
                ReturnToPending(manifest);
                return;
            }
            PublicationStep(manifest, run, "push-intent");
            var published = _git.PublishBatchCandidate(
                repo, manifest.Scope.IntegrationBranch, manifest.BaseSha,
                assembly.CandidateSha, ct,
                () => _leases.IsCurrent(lease) && RefMutationLeaseService.IsCurrent(refLease)
                    && FlightClaimLive(manifest));
            if (!published.Success)
            {
                if (published.Status is "integration-unverified" or "lease-lost" or "stale-base")
                {
                    // A transport error can follow a successful remote write.
                    // Leave the intent in Publishing for exact-SHA recovery.
                    var observed = _git.FetchBatchIntegrationTip(repo,
                        manifest.Scope.IntegrationBranch, ct);
                    State(manifest, BatchPhase.Publishing, assembly.CandidateSha,
                        lease.Fence,
                        string.Equals(observed, manifest.BaseSha, StringComparison.OrdinalIgnoreCase)
                            ? "publication-push-retry" : "publication-remote-proof-ambiguous",
                        run.BatchRunId,
                        refLease.Fence);
                    return;
                }
                State(manifest, BatchPhase.Paused, assembly.CandidateSha, lease.Fence, published.Status);
                return;
            }
            AfterRemotePushForTesting?.Invoke();
            if (!_leases.IsCurrent(lease) || !RefMutationLeaseService.IsCurrent(refLease))
                return; // Remote may have advanced; a new fenced coordinator reconstructs its receipt.
            PublicationStep(manifest, run, "remote-verified");
            _store.RecordPublication(new BatchGatePublication(
                manifest.BatchId, manifest.MembershipDigest, run.BatchRunId,
                manifest.BaseSha, assembly.CandidateSha, published.Sha,
                lease.Fence, refLease.Fence, _time.GetUtcNow()));
            PublicationStep(manifest, run, "receipt");
            RecordPublishedBaseVerdict(manifest, run);
            State(manifest, BatchPhase.Published, assembly.CandidateSha, lease.Fence);
            PublicationStep(manifest, run, "published");
            foreach (var key in assembly.AdmittedKeys)
                Lane(pending[key], "published", outcome: published.Status,
                    receipt: published.Sha, reason: $"batch:{manifest.BatchId}");
            // A push by object SHA does not necessarily advance origin/develop
            // in this checkout. The lane status projection reads that mirror.
            _git.FetchBatchIntegrationTip(repo, manifest.Scope.IntegrationBranch, ct);
            TrySyncPublishedCheckout(repo, manifest.Scope.IntegrationBranch,
                assembly.CandidateSha);
        }
        foreach (var key in assembly.EjectedKeys.Where(key =>
                     _store.ReadReplay(manifest.BatchId, key).Outcome != "conflict"))
            fallbackKeys.Add(key);
        foreach (var key in assembly.AdmittedKeys)
            await ReleaseMemberIsolatedAsync(manifest, run, pending[key], ct)
                .ConfigureAwait(false);
    }


    // AGT-3717: the published candidate holds every admitted member. One
    // member that cannot leave Auto Review must not hold the members after it;
    // the next tick's publication recovery retries the one that failed.
    private async Task ReleaseMemberIsolatedAsync(BatchGateManifest manifest,
        BatchGateRunRecord run, BatchGatePendingRecord pending, CancellationToken ct)
    {
        try
        {
            await ReleaseMemberAsync(manifest, run, pending, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "batch-member-release-failed batch={BatchId} task={TaskKey}",
                manifest.BatchId, pending.Subject.TaskKey);
        }
    }

    private async Task ReleaseMemberAsync(BatchGateManifest manifest,
        BatchGateRunRecord run, BatchGatePendingRecord pending, CancellationToken ct)
    {
        var refreshed = Refresh(pending);
        var subject = refreshed.Subject;
        if (!subject.CurrentGeneration)
        {
            ResolveSuperseded(pending, "superseded-after-publication");
            return;
        }
        var replay = _store.ReadReplay(manifest.BatchId, subject.TaskKey);
        BatchMemberTimeline? timeline = null;
        try
        {
            var published = _store.ReadPublication(manifest.BatchId)?.VerifiedAtUtc;
            timeline = BatchMemberTimelineProjection.Build(_store, subject, manifest,
                BatchMemberTimelineProjection.Rows(Service<GateRunLedger>(),
                    manifest.Scope.Project, BatchMemberTimelineProjection.Days(
                        subject.ReviewCompletedAtUtc, published ?? _time.GetUtcNow())),
                _store.ListPreChecks(manifest.Scope.Project),
                _store.ListFallbackCauses(manifest.Scope.Project)) with
            {
                PublishedAtUtc = published,
                TerminalAtUtc = published,
                Outcome = "published",
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "batch-member-timeline-projection-failed task={TaskKey}",
                subject.TaskKey);
        }
        var record = new BatchGateMemberRecord(
            subject.TaskKey, subject.RunAttempt, subject.DeliveryEpoch,
            subject.ResultSha, replay.Replacements.Select(item => item.RebasedSha).ToArray(),
            manifest.BatchId, manifest.MembershipDigest, manifest.BaseSha,
            run.CandidateSha, run.GateProfileDigest, run.BatchRunId,
            "pass", run.EvidencePath, _time.GetUtcNow(), timeline);
        if (_store.TryReadMember(manifest.BatchId, subject.TaskKey, run.BatchRunId) is null)
            _store.RecordMember(record);
        if (!_store.CanRelease(subject, manifest.BatchId, run.BatchRunId))
            throw new InvalidDataException("batch-gate-evidence-missing");
        var task = _scanner.FindJob(subject.TaskKey, pending.WatchPath);
        // A lane move can outrun a cached scanner snapshot. Never write
        // ownership or integration evidence into a vacated source folder:
        // those writers create directories and would resurrect Auto Review.
        if (task is null || task.State != TaskStates.AutoReview
            || !File.Exists(Path.Combine(task.FolderPath, "task.json"))) return;
        // A merged member rewrote no commit: every delivery SHA is an ancestor
        // of the candidate, and the result maps to its merge commit below.
        var rewritten = replay.Strategy == BatchReplayStrategies.Merge ? []
            : replay.Replacements.Where(item =>
                !string.Equals(item.OriginalSha, item.RebasedSha,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rewritten.Length > 0 && !_mutations.RecordMechanicalRebaseOnFolder(
                task.FolderPath, rewritten))
        {
            // AGT-3717: the candidate is already published, so the code of
            // this member is on the integration branch. A card without an
            // attributed chain for its rebased commits cannot pass the lane
            // guard; escalate it with the reason instead of failing the batch.
            _logger.LogWarning(
                "batch-member-replacement-unattributed batch={BatchId} task={TaskKey} rewritten={Rewritten}",
                manifest.BatchId, subject.TaskKey, rewritten.Length);
            await EscalateFallbackAsync(task, pending,
                $"Batch {Short(manifest.BatchId)} published this delivery on "
                + $"{manifest.Scope.IntegrationBranch} at {Short(run.CandidateSha)}, but the "
                + $"replacement mapping of its {rewritten.Length} rebased commit(s) could not be "
                + "recorded: the card has no attributed commit for this delivery. Repair the "
                + "commit attribution, then move the card to Human Review.",
                infrastructure: false, recordInStore: true, ct,
                occurrenceKey: $"batch-release-unattributed:{pending.ReviewAttemptId}:{subject.ResultSha}",
                laneOutcome: "Unattributable",
                resolution: "release-unattributed").ConfigureAwait(false);
            return;
        }
        BatchGateOwnershipStore.Write(task.FolderPath,
            new BatchGateOwnership(pending.ReviewAttemptId, subject,
                manifest.BatchId, run.BatchRunId));
        // The lane guard reads integration attribution before it permits the
        // move. A rebased member is not an ancestor by its original SHA, so
        // record its verified replacement mapping first.
        RecordBatchIntegration(task.FolderPath, manifest, run, replay);
        PublicationStep(manifest, run, "attributed", subject.TaskKey);
        var released = new BatchGateOutcome(
            $"batch-result:{manifest.BatchId}:{run.BatchRunId}:{subject.TaskKey}",
            "Batch gate passed on a verified candidate SHA.");
        if (HoldForEngineBatchDecision(task, pending.ReviewAttemptId, released)) return;
        RecordBatchShadowIntent(task, pending.ReviewAttemptId, released);
        var moved = await _transitions.MoveAsync(
            task.Id, TaskStates.HumanReview, task.WatchPath, ct,
            cause: $"batch-gate:{manifest.BatchId}",
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: "batch-gate-passed").ConfigureAwait(false);
        if (moved.Status != MoveJobStatus.Success)
        {
            WithdrawBatchShadowIntent(released);
            throw new IOException($"Batch lane release failed: {moved.Status} {moved.Message}");
        }
        AfterMemberMoveForTesting?.Invoke(subject.TaskKey);
        RecordBatchShadowOutcome(task, pending.ReviewAttemptId, released);
        _reviewJournal.RecordRemoteReviewParkVerdict(
            task.ProjectName, task.Id, moved.NewFolderPath ?? task.FolderPath,
            "Pass", "Batch gate passed on a verified candidate SHA.");
        _store.ResolvePending(pending.ReviewAttemptId, "published");
        _store.ClearQueueWait(pending.ReviewAttemptId);
        PublicationStep(manifest, run, "released", subject.TaskKey);
    }

    private void RecordBatchIntegration(string folder,
        BatchGateManifest manifest, BatchGateRunRecord run,
        BatchGateReplayRecord replay)
    {
        var member = manifest.Members.Single(item => item.TaskKey == replay.TaskKey);
        var resultReplacement = replay.Replacements.LastOrDefault(item =>
            string.Equals(item.OriginalSha, replay.OriginalResultSha,
                StringComparison.OrdinalIgnoreCase));
        if (resultReplacement is null)
            throw new InvalidDataException("Batch result replacement is missing.");
        var mapping = new TaskIntegrationRecord
        {
            Id = $"batch-map:{manifest.BatchId}:{run.BatchRunId}:{replay.TaskKey}",
            Classification = IntegrationRecordClasses.CuratedMapping,
            RecordedAtUtc = _time.GetUtcNow().UtcDateTime,
            IntegrationBranch = manifest.Scope.IntegrationBranch,
            SourceSha = replay.OriginalResultSha,
            IntegrationSha = resultReplacement.RebasedSha,
            DeliveryEpoch = member.RunAttempt,
            Evidence = $"Batch {manifest.BatchId}, run {run.BatchRunId}, "
                + $"tested candidate and verified remote tip {run.CandidateSha}; "
                + $"membership digest {manifest.MembershipDigest}.",
        };
        if (!_mutations.AppendIntegrationRecordOnFolder(folder, mapping).Succeeded)
            throw new IOException("Batch result mapping could not be recorded.");
        var shas = replay.Replacements.Select(item => item.RebasedSha)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var record = new TaskIntegrationRecord
        {
            Id = $"batch-gate:{manifest.BatchId}:{run.BatchRunId}:{replay.TaskKey}",
            Classification = IntegrationRecordClasses.IntegratedVerified,
            RecordedAtUtc = _time.GetUtcNow().UtcDateTime,
            IntegrationBranch = manifest.Scope.IntegrationBranch,
            CommitShas = shas,
            Evidence = $"Batch {manifest.BatchId}, run {run.BatchRunId}, "
                + $"tested candidate and verified remote tip {run.CandidateSha}; "
                + $"membership digest {manifest.MembershipDigest}.",
        };
        if (!_mutations.AppendIntegrationRecordOnFolder(folder, record).Succeeded)
            throw new IOException("Batch integration bookkeeping could not be recorded.");
    }

    /// <summary>
    /// Repairs only a published, passed member whose release was recorded as
    /// unattributable. The pre-publication tip preserves the delivery range
    /// even after develop contains the candidate.
    /// </summary>
    private async Task RepairPublishedUnattributedAsync(CancellationToken ct)
    {
        foreach (var pending in _store.ListAllPending().Where(item =>
                     _store.ReadPendingOutcome(item.ReviewAttemptId) == "release-unattributed"))
        {
            ct.ThrowIfCancellationRequested();
            var subject = pending.Subject;
            var task = _scanner.FindJob(subject.TaskKey, pending.WatchPath);
            if (task?.State != TaskStates.Escalated || task.PendingIntent is not null
                || !File.Exists(Path.Combine(task.FolderPath, "task.json"))) continue;
            var projection = _authority.GetTaskProjection(AuthorityKey(task));
            if (projection.CurrentReviewAttempt?.AttemptId != pending.ReviewAttemptId
                || projection.CurrentRunAttempt?.AttemptId != subject.RunAttempt
                || projection.CurrentReviewAttempt.Outcome is not
                    (ReviewTerminalOutcome.Pass or ReviewTerminalOutcome.IntegrationBranchDefect)) continue;
            var envelope = ReviewSubjectStore.Read(task.FolderPath);
            if (envelope is null || envelope.RunAttemptId != subject.RunAttempt
                || !string.Equals(envelope.ResultSha, subject.ResultSha,
                    StringComparison.OrdinalIgnoreCase)) continue;
            var deliveryRef = envelope.ImmutableResultRef ?? envelope.ResultRef;
            if (string.IsNullOrWhiteSpace(deliveryRef)) continue;
            foreach (var manifest in _store.ListManifests().Where(item =>
                         item.Members.Any(member => member.TaskKey == subject.TaskKey
                             && member.RunAttempt == subject.RunAttempt)))
            {
                var publication = _store.ReadPublication(manifest.BatchId);
                var replay = _store.TryReadReplay(manifest.BatchId, subject.TaskKey);
                if (publication is null || replay?.Outcome != "admitted"
                    || publication.MembershipDigest != manifest.MembershipDigest
                    || publication.PreTipSha != manifest.BaseSha
                    || publication.TestedCandidateSha != publication.VerifiedRemoteSha)
                    continue;
                var run = _store.ReadRun(manifest.BatchId, publication.BatchRunId);
                if (publication.BatchRunId != run.BatchRunId
                    || publication.TestedCandidateSha != run.CandidateSha
                    || !PassingPublicationRun(manifest, run)
                    || _store.TryReadMember(manifest.BatchId, subject.TaskKey,
                        run.BatchRunId) is null) continue;
                var verified = _git.VerifyDeliveredCommit(pending.RepositoryPath,
                    manifest.Scope.IntegrationBranch, publication.VerifiedRemoteSha);
                if (!verified.IsVerified) continue;
                var range = _git.InspectRemoteDeliveryCommitRange(pending.RepositoryPath,
                    deliveryRef, subject.ResultSha,
                    manifest.Scope.IntegrationBranch, envelope.BaseSha, ct,
                    taskKey: subject.TaskKey, fallbackIntegrationBaseSha: manifest.BaseSha);
                if (!range.Success || range.Commits.Count == 0)
                {
                    _logger.LogWarning("batch-release-attribution-repair-refused task={TaskKey} reason={Reason}",
                        subject.TaskKey, range.Warning);
                    continue;
                }
                var attributed = RemoteCommitAttributionGuard.Attribute(subject.TaskKey,
                    deliveryRef, range.Commits,
                    envelope.Repository, RemoteCommitAttributionGuard.CardReferencedKeys(task));
                var rewritten = replay.Strategy == BatchReplayStrategies.Merge ? []
                    : replay.Replacements.Where(item => !string.Equals(item.OriginalSha,
                        item.RebasedSha, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (!attributed.Accepted || rewritten.Any(item =>
                        !attributed.Commits.Any(commit => string.Equals(commit.Sha,
                            item.OriginalSha, StringComparison.OrdinalIgnoreCase))))
                {
                    _logger.LogWarning("batch-release-attribution-repair-refused task={TaskKey} reason={Reason}",
                        subject.TaskKey, attributed.Warning ?? "Replacement source was not attributed.");
                    continue;
                }
                if (!_mutations.SetRemoteCommitAttributionOnFolder(task.FolderPath,
                        subject.RunAttempt,
                        string.IsNullOrWhiteSpace(envelope.Executor)
                            ? "remote-runner-repair" : envelope.Executor,
                        subject.ResultSha, attributed.Commits)
                    || rewritten.Length > 0 && !_mutations.RecordMechanicalRebaseOnFolder(
                        task.FolderPath, rewritten)) continue;
                BatchGateOwnershipStore.Write(task.FolderPath, new BatchGateOwnership(
                    pending.ReviewAttemptId, subject, manifest.BatchId, run.BatchRunId));
                RecordBatchIntegration(task.FolderPath, manifest, run, replay);
                var moved = await _transitions.MoveAsync(task.Id, TaskStates.HumanReview,
                    task.WatchPath, ct, cause: $"batch-attribution-repair:{manifest.BatchId}",
                    suppressProductExecution: true, expectedSourceState: TaskStates.Escalated,
                    transitionCause: LaneChangeCauses.ReviewVerdict,
                    transitionDetail: "batch-release-attribution-repaired").ConfigureAwait(false);
                if (moved.Status != MoveJobStatus.Success) continue;
                PublicationStep(manifest, run, "attributed", subject.TaskKey);
                PublicationStep(manifest, run, "released", subject.TaskKey);
                _reviewJournal.RecordRemoteReviewParkVerdict(task.ProjectName, task.Id,
                    moved.NewFolderPath ?? task.FolderPath, "Pass",
                    "Published batch attribution and replacement mapping repaired.");
                break;
            }
        }
    }

}
