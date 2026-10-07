using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>Documentation-only pilot route from settled review to verified batch publication.</summary>
public sealed class BatchGatePilotService
{
    private readonly BatchGateStore _store;
    private readonly BatchGateLeaseService _leases;
    private readonly RefMutationLeaseService _refLeases;
    private readonly GitService _git;
    private readonly IBuildTestGateRunner _gate;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly HumanReviewEscalation _reviewJournal;
    private readonly RemoteDeliveryIntegrationCoordinator _directIntegration;
    private readonly ILogger<BatchGatePilotService> _logger;
    private readonly ILoadThrottleGate? _load;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly Dictionary<string, Task> _fallbackFlights = new(StringComparer.Ordinal);

    /// <summary>Coordinator lease renewal cadence; well inside the two-minute grant.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-task gate starts per review generation before GateInfra (D8).</summary>
    internal const int FallbackGateStartBudget = 2;

    public BatchGatePilotService(
        BatchGateStore store, BatchGateLeaseService leases,
        RefMutationLeaseService refLeases, GitService git,
        IBuildTestGateRunner gate, AttemptAuthorityService authority,
        TaskScannerService scanner, ProjectSettingsService settings,
        TaskTransitionService transitions, TaskMutationService mutations,
        HumanReviewEscalation reviewJournal,
        RemoteDeliveryIntegrationCoordinator directIntegration,
        ILogger<BatchGatePilotService> logger,
        ILoadThrottleGate? load = null, TimeProvider? timeProvider = null)
    {
        _store = store;
        _leases = leases;
        _refLeases = refLeases;
        _git = git;
        _gate = gate;
        _authority = authority;
        _scanner = scanner;
        _settings = settings;
        _transitions = transitions;
        _mutations = mutations;
        _reviewJournal = reviewJournal;
        _directIntegration = directIntegration;
        _logger = logger;
        _load = load;
        _time = timeProvider ?? TimeProvider.System;
    }

    public BatchGatePendingRecord Enqueue(TaskInfo task, ReviewAttemptDto review,
        RunAttemptDto sourceRun, DateTimeOffset reviewedAtUtc)
    {
        var priorOwnership = BatchGateOwnershipStore.Read(task.FolderPath);
        if (task.State == TaskStates.AutoReview
            && priorOwnership?.ReviewAttemptId == review.AttemptId
            && (priorOwnership.FallbackGateActive || priorOwnership.FallbackTestedSha is not null))
            throw new InvalidOperationException("Per-task fallback is already in progress.");
        if (priorOwnership?.ReviewAttemptId == review.AttemptId
            && _store.ReadPending(review.AttemptId) is { } prior)
            return prior;
        var candidate = BuildPending(task, review, sourceRun, reviewedAtUtc);
        var project = _settings.Get(task.ProjectName);
        var scope = Scope(candidate.Subject);
        var reason = BatchGatePolicy.Exclusion(candidate.Subject with { EnqueueSequence = 1 },
            scope, project.BatchGate);
        if (reason is not null)
            throw new InvalidDataException($"Batch admission failed: {reason}.");
        // The card must fail closed before it becomes visible to the queue.
        BatchGateOwnershipStore.Write(task.FolderPath,
            new BatchGateOwnership(review.AttemptId, candidate.Subject));
        var pending = _store.Enqueue(candidate);
        BatchGateOwnershipStore.Write(task.FolderPath,
            new BatchGateOwnership(review.AttemptId, pending.Subject));
        return pending;
    }

    public async Task RunEmergencyFallbackAsync(TaskInfo task, ReviewAttemptDto review,
        RunAttemptDto sourceRun, DateTimeOffset reviewedAtUtc, CancellationToken ct)
    {
        if (task.State != TaskStates.AutoReview) return;
        var pending = BuildPending(task, review, sourceRun, reviewedAtUtc);
        await RunPerTaskFallbackAsync(pending, ct, recordInStore: false).ConfigureAwait(false);
    }

    private BatchGatePendingRecord BuildPending(TaskInfo task, ReviewAttemptDto review,
        RunAttemptDto sourceRun, DateTimeOffset reviewedAtUtc)
    {
        var project = _settings.Get(task.ProjectName);
        if (review.Subject.Plan?.Commands.Any(command =>
                AgentStudio.TaskServer.Contracts.ReviewCommandKinds.IsAgent(command.ExecutionKind)) != true)
            throw new InvalidDataException("Batch admission requires a passed per-card model review.");
        var repo = _git.ResolveRepoRootForWatchPath(task.WatchPath)
            ?? throw new InvalidDataException("Batch repository is unavailable.");
        var branch = TaskIntegrationBranch.Resolve(task, project.IntegrationBranch);
        var envelope = sourceRun.ResultEnvelope;
        var changed = envelope?.ImmutableRemoteRef is { } resultRef
            ? _git.ChangedPathsAgainstMergeBase(repo, branch, resultRef)
            : null;
        var profile = GateProfile(project.BuildProfile, repo);
        var projection = _authority.GetTaskProjection(task.Id);
        var subject = new BatchGateSubject(
            task.Id, task.ProjectName, review.RepositoryId, branch,
            "complete-suite", profile, PlatformVersion(),
            envelope?.ImmutableRemoteRef ?? string.Empty,
            envelope?.ResultSha ?? string.Empty,
            sourceRun.AttemptId, sourceRun.AuthorityEpoch,
            sourceRun.LastFence,
            review.TestedResultSha ?? string.Empty,
            envelope is not null && sourceRun.State == AttemptLifecycleState.Completed,
            review.Outcome == ReviewTerminalOutcome.Pass,
            review.Subject.Plan?.BuildTestDeferredToBatch == true,
            projection.CurrentRunAttempt?.AttemptId == sourceRun.AttemptId
                && projection.CurrentReviewAttempt?.AttemptId == review.AttemptId,
            false, null,
            DocsOnlyDeliveryPolicy.IsDocsOnly(changed),
            reviewedAtUtc, 0);
        // The planner may have frozen a docs-only plan, but the immutable
        // result or current generation may have changed before settlement.
        return new BatchGatePendingRecord(
            review.AttemptId, subject, repo, task.WatchPath, task.FolderPath,
            project.IntegrationStrategy, PipelineTypes.Resolve(task), _time.GetUtcNow());
    }

    public BatchGatePilotSnapshot Report(string project)
    {
        var falseCompleted = 0;
        var staleReleased = 0;
        foreach (var pending in _store.ListAllPending().Where(item =>
                     string.Equals(item.Subject.Project, project, StringComparison.Ordinal))
                 .GroupBy(item => item.Subject.TaskKey, StringComparer.Ordinal)
                 .Select(group => group.MaxBy(item => item.Subject.EnqueueSequence)!))
        {
            var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
            if (task?.State is not (TaskStates.HumanReview or TaskStates.Completed)) continue;
            var projection = _authority.GetTaskProjection(task.Id);
            if (projection.CurrentReviewAttempt?.AttemptId != pending.ReviewAttemptId)
                continue;
            try
            {
                var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
                if (ownership is not null
                    && (projection.CurrentRunAttempt?.AttemptId != ownership.Subject.RunAttempt
                        || projection.CurrentReviewAttempt?.AttemptId != ownership.ReviewAttemptId
                        || !string.Equals(projection.CurrentRunAttempt.ResultEnvelope?.ResultSha,
                            ownership.Subject.ResultSha, StringComparison.OrdinalIgnoreCase)
                        || projection.CurrentRunAttempt.ResultEnvelope?.ImmutableRemoteRef
                            != ownership.Subject.ResultRef
                        || projection.CurrentRunAttempt.AuthorityEpoch
                            != ownership.Subject.DeliveryEpoch
                        || projection.CurrentRunAttempt.LastFence
                            != ownership.Subject.FencingToken))
                    staleReleased++;
                if (ownership is null
                    || ownership.ReviewAttemptId != pending.ReviewAttemptId
                    || BatchGateOwnershipStore.ReleaseFailure(
                        ownership, projection, _store, task.FolderPath) is not null)
                {
                    if (task.State == TaskStates.Completed) falseCompleted++;
                }
            }
            catch
            {
                if (task.State == TaskStates.Completed) falseCompleted++;
            }
        }
        return BatchGatePilotSnapshotReader.Read(_store, project,
            falseCompleted, staleReleased);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _tick.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            RecoverInterrupted();
            await ResolvePausedAsync(ct).ConfigureAwait(false);
            await RecoverPublishedAsync(ct).ConfigureAwait(false);
            var waiting = _store.ListPending();
            foreach (var item in waiting)
            {
                var task = _scanner.FindJob(item.Subject.TaskKey, item.WatchPath);
                if (task?.State == TaskStates.Escalated
                    && File.Exists(Path.Combine(TaskPaths.LogsDir(task.FolderPath),
                        $"batch-fallback-{item.ReviewAttemptId}.json")))
                {
                    _store.ResolvePending(item.ReviewAttemptId, "per-task-gate-red");
                    continue;
                }
                if (task?.State != TaskStates.HumanReview) continue;
                var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
                if (ownership?.ReviewAttemptId == item.ReviewAttemptId
                    && ownership.FallbackIntegrated)
                    _store.ResolvePending(item.ReviewAttemptId, "per-task-gate");
            }
            // A persisted active per-task marker with no flight in this process
            // is a gate the process lost: it stopped or the gate threw. Resume
            // it on the same immutable subject; exclusion alone would keep the
            // member in AutoReview on every later tick.
            foreach (var item in _store.ListPending().Select(Refresh)
                         .Where(item => item.Subject.ActivePerTaskGate
                             && !FallbackInFlight(item.ReviewAttemptId)).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                await RunPerTaskFallbackAsync(item, ct).ConfigureAwait(false);
            }
            // A newer passed review of the same task supersedes any older
            // pending record. Left in the queue, the older record would share
            // the task key with its replacement and break member selection.
            foreach (var stale in _store.ListPending()
                         .GroupBy(item => item.Subject.TaskKey, StringComparer.Ordinal)
                         .SelectMany(group => group
                             .OrderByDescending(item => item.Subject.EnqueueSequence)
                             .Skip(1))
                         .ToArray())
                ResolveSuperseded(stale, "superseded-by-newer-review");
            // Refresh before choosing the scope. A queued subject may have
            // outlived a project gate profile change or a platform upgrade.
            // Grouping on the stored values would exclude it from its own
            // batch on every subsequent tick.
            waiting = _store.ListPending().Select(Refresh).ToArray();
            foreach (var group in waiting.GroupBy(item =>
                         (item.Subject.Project, item.Subject.Repository,
                             item.Subject.IntegrationBranch, item.Subject.GateProfileDigest,
                             item.Subject.PlatformVersion, item.RepositoryPath)))
            {
                ct.ThrowIfCancellationRequested();
                var first = group.First();
                var options = _settings.Get(first.Subject.Project).BatchGate;
                if (!options.Enabled)
                {
                    foreach (var item in group)
                        if (PrepareDisabledFallback(item))
                            await RunPerTaskFallbackAsync(item, ct).ConfigureAwait(false);
                    continue;
                }
                var scope = Scope(first.Subject);
                var current = group.ToArray();
                var baseSha = _git.FetchBatchIntegrationTip(
                    first.RepositoryPath, scope.IntegrationBranch, ct);
                if (baseSha is null) continue;
                var formed = BatchGatePolicy.Form(current.Select(item => item.Subject),
                    scope, baseSha, options, _time.GetUtcNow(),
                    _authority.ListPendingReviewAttempts().Count,
                    _load?.Current.Throttle == true);
                if (formed.UsePerTaskGate)
                {
                    foreach (var item in current.Where(item =>
                                 formed.EligibleKeys.Contains(item.Subject.TaskKey)))
                        await RunPerTaskFallbackAsync(item, ct).ConfigureAwait(false);
                    continue;
                }
                if (formed.Manifest is null) continue;
                _store.CloseManifest(formed.Manifest);
                foreach (var member in formed.Manifest.Members)
                {
                    var item = current.Single(candidate =>
                        candidate.Subject.TaskKey == member.TaskKey);
                    var task = _scanner.FindJob(member.TaskKey, item.WatchPath);
                    if (task is null) throw new InvalidDataException("Batch member disappeared after close.");
                    BatchGateOwnershipStore.Write(task.FolderPath,
                        new BatchGateOwnership(item.ReviewAttemptId, member,
                            formed.Manifest.BatchId));
                }
                await ExecuteAsync(formed.Manifest,
                    current.ToDictionary(item => item.Subject.TaskKey,
                        StringComparer.Ordinal), first.RepositoryPath, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _tick.Release();
        }
    }

    private bool PrepareDisabledFallback(BatchGatePendingRecord pending)
    {
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (task is null) return false;
        var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
        if (ownership?.BatchId is not { } batchId) return true;
        var manifest = _store.ReadManifest(batchId);
        if (_store.ReadPublication(batchId) is not null) return false;
        BatchCoordinatorLease? rollback;
        try { rollback = _leases.TryAcquire(manifest.Scope, "batch-rollback"); }
        catch (IOException) { return false; }
        if (rollback is null) return false;
        try
        {
            State(manifest, BatchPhase.Abandoned, null, rollback.Fence,
                "abandoned-by-rollback");
            ReturnToPending(manifest);
            return true;
        }
        finally { _leases.Release(rollback); }
    }

    private void RecoverInterrupted()
    {
        foreach (var manifest in _store.ListRecoverableManifests())
        {
            BatchCoordinatorLease? recovery;
            try { recovery = _leases.TryAcquire(manifest.Scope, "batch-recovery"); }
            catch (IOException) { continue; }
            if (recovery is null) continue;
            try
            {
                State(manifest, BatchPhase.Abandoned, null, recovery.Fence,
                    "interrupted before a publishable verdict; reconstruct from the closed snapshot");
                ReturnToPending(manifest);
            }
            finally { _leases.Release(recovery); }
        }
    }

    // A pause stops the batch, not its members. Every member the paused batch
    // still owns returns to the per-task gate on its unchanged immutable
    // subject, which either integrates the card or escalates it with the gate
    // reason. Members are released before the terminal state is written, so an
    // interruption in between only repeats an idempotent release.
    private async Task ResolvePausedAsync(CancellationToken ct)
    {
        foreach (var manifest in _store.ListPausedManifests())
        {
            ct.ThrowIfCancellationRequested();
            BatchCoordinatorLease? lease;
            try { lease = _leases.TryAcquire(manifest.Scope, "batch-pause-fallback"); }
            catch (IOException) { continue; }
            if (lease is null) continue;
            IReadOnlyList<BatchGatePendingRecord> released;
            try
            {
                var paused = _store.LatestState(manifest.BatchId);
                _logger.LogWarning("batch-gate-paused batch={BatchId} reason={Reason}",
                    manifest.BatchId, paused.Reason);
                released = ReturnToPending(manifest);
                State(manifest, BatchPhase.Abandoned, paused.CandidateSha, lease.Fence,
                    "paused-to-per-task-gate: " + paused.Reason);
            }
            finally { _leases.Release(lease); }
            foreach (var item in released)
                await RunPerTaskFallbackAsync(item, ct).ConfigureAwait(false);
        }
    }

    private async Task RecoverPublishedAsync(CancellationToken ct)
    {
        var waiting = _store.ListPending();
        foreach (var manifest in _store.ListManifests())
        {
            ct.ThrowIfCancellationRequested();
            var publication = _store.ReadPublication(manifest.BatchId);
            var phase = _store.LatestState(manifest.BatchId).Phase;
            if (phase is not (BatchPhase.Publishing or BatchPhase.Published)) continue;
            if (phase == BatchPhase.Publishing)
            {
                var pendingRepo = waiting.FirstOrDefault(item =>
                    manifest.Members.Any(member => member.TaskKey == item.Subject.TaskKey
                        && member.RunAttempt == item.Subject.RunAttempt));
                if (pendingRepo is null) continue;
                BatchCoordinatorLease? lease;
                try { lease = _leases.TryAcquire(manifest.Scope, "batch-publication-recovery"); }
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
                        var remote = _git.FetchBatchIntegrationTip(pendingRepo.RepositoryPath,
                            manifest.Scope.IntegrationBranch, ct);
                        if (remote is null) continue;
                        if (!string.Equals(remote, state.CandidateSha,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            State(manifest, BatchPhase.Abandoned, state.CandidateSha,
                                lease.Fence, "publication interrupted before verification; reconstruct on current base");
                            ReturnToPending(manifest);
                            continue;
                        }
                        // The remote itself is the missing publication proof.
                        // Only a passing recorded run for this closed subject
                        // may be attributed to that exact remote SHA.
                        var matching = _store.ListRuns(manifest.BatchId)
                            .Where(run => run.CandidateSha == state.CandidateSha
                                && (state.BatchRunId is null
                                    || run.BatchRunId == state.BatchRunId)
                                && run.CoordinatorFence == state.CoordinatorFence
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
                            State(manifest, BatchPhase.Paused, state.CandidateSha,
                                lease.Fence, "batch-gate-evidence-missing");
                            continue;
                        }
                        var recoveredRun = matching[0];
                        if (!_leases.IsCurrent(lease)
                            || !RefMutationLeaseService.IsCurrent(refLease)) continue;
                        publication = new BatchGatePublication(
                            manifest.BatchId, manifest.MembershipDigest,
                            recoveredRun.BatchRunId, manifest.BaseSha,
                            recoveredRun.CandidateSha, remote,
                            recoveredRun.CoordinatorFence,
                            state.RefMutationFence ?? refLease.Fence,
                            _time.GetUtcNow());
                        _store.RecordPublication(publication);
                    }
                    var publishedRun = _store.ReadRun(
                        manifest.BatchId, publication.BatchRunId);
                    if (publication.MembershipDigest != manifest.MembershipDigest
                        || publication.PreTipSha != manifest.BaseSha
                        || publication.TestedCandidateSha != publishedRun.CandidateSha
                        || publication.VerifiedRemoteSha != publishedRun.CandidateSha
                        || _store.ReadVerdict(manifest.BatchId,
                            publishedRun.BatchRunId)?.Outcome != "pass")
                        throw new InvalidDataException("batch-gate-evidence-missing");
                    if (!_leases.IsCurrent(lease)
                        || !RefMutationLeaseService.IsCurrent(refLease)) continue;
                    var local = _git.FastForwardIntegrationBranch(
                        pendingRepo.RepositoryPath, manifest.Scope.IntegrationBranch,
                        publication.TestedCandidateSha);
                    if (!local.Success) continue;
                    State(manifest, BatchPhase.Published,
                        publication.TestedCandidateSha, lease.Fence,
                        "resumed verified publication");
                }
                finally { _leases.Release(lease); }
            }
            if (publication is null) continue;
            var run = _store.ReadRun(manifest.BatchId, publication.BatchRunId);
            if (publication.TestedCandidateSha != run.CandidateSha
                || publication.VerifiedRemoteSha != run.CandidateSha
                || _store.ReadVerdict(manifest.BatchId, run.BatchRunId)?.Outcome != "pass")
                throw new InvalidDataException("batch-gate-evidence-missing");
            foreach (var member in manifest.Members)
            {
                var pending = waiting.FirstOrDefault(item =>
                    item.Subject.TaskKey == member.TaskKey
                    && item.Subject.RunAttempt == member.RunAttempt);
                if (pending is null) continue;
                var replay = _store.TryReadReplay(manifest.BatchId, member.TaskKey);
                if (replay?.Outcome == "conflict")
                {
                    await RunPerTaskFallbackAsync(pending, ct).ConfigureAwait(false);
                    continue;
                }
                if (replay?.Outcome != "admitted") continue;
                var task = _scanner.FindJob(member.TaskKey, pending.WatchPath);
                if (task?.State == TaskStates.HumanReview)
                {
                    var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
                    var projection = _authority.GetTaskProjection(task.Id);
                    if (ownership is not null && BatchGateOwnershipStore.ReleaseFailure(
                            ownership, projection, _store, task.FolderPath) is null)
                    {
                        RecordBatchIntegration(task.FolderPath, manifest, run, replay);
                        _store.ResolvePending(pending.ReviewAttemptId, "published");
                    }
                    continue;
                }
                if (!Refresh(pending).Subject.CurrentGeneration)
                {
                    ResolveSuperseded(pending, "superseded-after-publication");
                    continue;
                }
                await ReleaseMemberAsync(manifest, run, pending, ct).ConfigureAwait(false);
            }
        }
    }

    private IReadOnlyList<BatchGatePendingRecord> ReturnToPending(BatchGateManifest manifest)
    {
        var released = new List<BatchGatePendingRecord>();
        if (manifest.ParentBatchId is { } parentId)
            released.AddRange(ReturnToPending(_store.ReadManifest(parentId)));
        foreach (var member in manifest.Members)
        {
            var pending = _store.ListPending().FirstOrDefault(item =>
                item.Subject.TaskKey == member.TaskKey
                && item.Subject.RunAttempt == member.RunAttempt);
            if (pending is null) continue;
            var task = _scanner.FindJob(member.TaskKey, pending.WatchPath);
            if (task is null) continue;
            var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
            if (ownership is not null
                && (ownership.BatchId == manifest.BatchId
                    || manifest.ParentBatchId is not null
                    && ownership.BatchId == manifest.ParentBatchId)
                && ownership.BatchRunId is null)
            {
                BatchGateOwnershipStore.Write(task.FolderPath,
                    new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject));
                released.Add(pending);
            }
        }
        return released;
    }

    private async Task ExecuteAsync(BatchGateManifest manifest,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, CancellationToken ct)
    {
        BatchCoordinatorLease? lease;
        try { lease = _leases.TryAcquire(manifest.Scope, Environment.MachineName); }
        catch (IOException) { return; }
        if (lease is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(lease, cancellation);
        try
        {
            if (!RouteEnabled())
            {
                AbandonRollback();
                return;
            }
            if (!MembersCurrent())
            {
                AbandonSuperseded("superseded before replay");
                return;
            }
            var assembly = new BatchGateAssembler(_store, _git, repo)
                .Assemble(manifest.BatchId, lease.Fence);
            if (!RouteEnabled())
            {
                AbandonRollback();
                return;
            }
            if (!MembersCurrent())
            {
                AbandonSuperseded("superseded after replay");
                return;
            }
            foreach (var key in assembly.EjectedKeys)
                _logger.LogWarning("batch-member-conflict batch={BatchId} task={TaskKey}", manifest.BatchId, key);
            if (assembly.CascadeStopped)
                _logger.LogWarning("batch-conflict-cascade batch={BatchId} deferred={Members}",
                    manifest.BatchId, string.Join(',', assembly.DeferredKeys));
            foreach (var key in assembly.DeferredKeys)
            {
                _store.RecordReplay(new BatchGateReplayRecord(
                    manifest.BatchId, manifest.MembershipDigest, key,
                    pending[key].Subject.ResultSha, assembly.CandidateSha, null,
                    [], [], "cascade-deferred", _time.GetUtcNow()));
                var task = _scanner.FindJob(key, pending[key].WatchPath);
                if (task is not null)
                    BatchGateOwnershipStore.Write(task.FolderPath,
                        new BatchGateOwnership(pending[key].ReviewAttemptId,
                            pending[key].Subject));
            }
            if (assembly.AdmittedKeys.Count == 0)
            {
                State(manifest, BatchPhase.Abandoned, null, lease.Fence, "no-admitted-members");
                ReturnToPending(manifest);
                foreach (var key in assembly.EjectedKeys.Concat(assembly.DeferredKeys))
                    await RunPerTaskFallbackAsync(pending[key], cancellation.Token)
                        .ConfigureAwait(false);
                return;
            }
            var run = NewRun(manifest, assembly.CandidateSha, lease.Fence,
                repo, _settings.Get(manifest.Scope.Project).BuildProfile);
            _store.RecordRun(run);
            State(manifest, BatchPhase.Running, assembly.CandidateSha, lease.Fence);
            var gate = await RunGateAsync(run, repo, cancellation.Token,
                () => MembersCurrent() && RouteEnabled()).ConfigureAwait(false);
            if (!RouteEnabled())
            {
                AbandonRollback();
                return;
            }
            if (!MembersCurrent())
            {
                AbandonSuperseded("superseded during gate");
                return;
            }
            if (gate.IsInfrastructureFailure)
            {
                // Only the same candidate SHA can consume the one healthy-host
                // retry. The second infrastructure red remains a visible pause.
                if (_load?.Current.Throttle != false)
                {
                    RecordVerdict(run, gate, "infrastructure-red");
                    State(manifest, BatchPhase.Paused, assembly.CandidateSha,
                        lease.Fence, "GateInfra: no healthy host for same-SHA retry");
                    return;
                }
                gate = await RunGateAsync(run, repo, cancellation.Token).ConfigureAwait(false);
                if (gate.IsInfrastructureFailure)
                {
                    RecordVerdict(run, gate, "infrastructure-red");
                    State(manifest, BatchPhase.Paused, assembly.CandidateSha,
                        lease.Fence, "GateInfra: second infrastructure red");
                    return;
                }
            }
            if (gate.Verdict != BuildTestGateVerdict.Ok
                || !string.Equals(gate.TestedSha, assembly.CandidateSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (gate.FlakyQuarantinedFailures.Count > 0)
                {
                    RecordVerdict(run, gate, "flaky-red");
                    State(manifest, BatchPhase.Paused, assembly.CandidateSha,
                        lease.Fence, "flaky quarantine requires a fresh gate verdict");
                    return;
                }
                RecordVerdict(run, gate, "deterministic-suite-red");
                State(manifest, BatchPhase.Red, assembly.CandidateSha, lease.Fence,
                    "deterministic suite red");
                await IsolateAsync(manifest, assembly, lease, pending, repo,
                    cancellation.Token).ConfigureAwait(false);
                return;
            }
            var verdict = RecordVerdict(run, gate,
                gate.FlakyQuarantinedFailures.Count > 0
                    ? "flaky-quarantined-pass" : "pass");
            State(manifest, BatchPhase.Green, assembly.CandidateSha, lease.Fence);
            await PublishGreenAsync(manifest, assembly, run, verdict, lease,
                pending, repo, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !RouteEnabled())
        {
            AbandonRollback();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !MembersCurrent())
        {
            AbandonSuperseded("superseded during gate");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
            && cancellation.IsCancellationRequested)
        {
            // Only the heartbeat cancels this batch on its own: the coordinator
            // lease is gone, so the batch may not publish. A verified remote
            // publication keeps its phase for the next tick's recovery.
            if (_store.ReadPublication(manifest.BatchId) is null
                && _store.LatestState(manifest.BatchId).Phase != BatchPhase.Publishing)
            {
                State(manifest, BatchPhase.Abandoned, null, lease.Fence,
                    "coordinator lease lost during the gate");
                ReturnToPending(manifest);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "batch-gate-failed batch={BatchId}", manifest.BatchId);
            // A verified remote publication is durable. Keep its phase so the
            // next tick can finish local attribution and lane release.
            if (_store.ReadPublication(manifest.BatchId) is null
                && _store.LatestState(manifest.BatchId).Phase != BatchPhase.Publishing)
                State(manifest, BatchPhase.Paused, null, lease.Fence, ex.GetType().Name);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException ex)
            {
                SilentCatch.Note(ex, "Batch gate coordinator heartbeat stopped");
            }
            _leases.Release(lease);
        }

        bool MembersCurrent() => manifest.Members.All(member =>
            Refresh(pending[member.TaskKey]).Subject.CurrentGeneration);

        bool RouteEnabled() => _settings.Get(manifest.Scope.Project).BatchGate.Enabled;

        void AbandonRollback()
        {
            State(manifest, BatchPhase.Abandoned, null, lease.Fence,
                "abandoned-by-rollback");
            ReturnToPending(manifest);
        }

        void AbandonSuperseded(string reason)
        {
            State(manifest, BatchPhase.Abandoned, null, lease.Fence, reason);
            ReturnToPending(manifest);
            foreach (var member in manifest.Members.Where(member =>
                         !Refresh(pending[member.TaskKey]).Subject.CurrentGeneration))
                ResolveSuperseded(pending[member.TaskKey], "superseded");
        }
    }

    private async Task PublishGreenAsync(BatchGateManifest manifest,
        BatchGateAssembly assembly, BatchGateRunRecord run,
        BatchGateRunVerdict verdict, BatchCoordinatorLease lease,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, CancellationToken ct)
    {
        using (var refLease = await _refLeases.AcquireAsync(
            manifest.Scope.Project, repo, manifest.Scope.IntegrationBranch,
            ct).ConfigureAwait(false))
        {
            if (!_settings.Get(manifest.Scope.Project).BatchGate.Enabled)
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "abandoned-by-rollback");
                ReturnToPending(manifest);
                return;
            }
            var current = assembly.AdmittedKeys.ToDictionary(
                key => key, key => Refresh(pending[key]).Subject, StringComparer.Ordinal);
            var remoteTip = _git.FetchBatchIntegrationTip(
                repo, manifest.Scope.IntegrationBranch, ct);
            var decision = BatchGatePublicationPolicy.Decide(
                manifest, assembly, run, verdict, current,
                remoteTip ?? string.Empty, _leases.IsCurrent(lease),
                RefMutationLeaseService.IsCurrent(refLease),
                _git.IsAncestor(repo, manifest.BaseSha, assembly.CandidateSha));
            if (decision != BatchPublishDecision.FastForward)
            {
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
            if (!_leases.IsCurrent(lease) || !RefMutationLeaseService.IsCurrent(refLease))
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "lease lost before publication");
                ReturnToPending(manifest);
                return;
            }
            if (!_settings.Get(manifest.Scope.Project).BatchGate.Enabled)
            {
                State(manifest, BatchPhase.Abandoned, assembly.CandidateSha,
                    lease.Fence, "abandoned-by-rollback");
                ReturnToPending(manifest);
                return;
            }
            var published = _git.PublishBatchCandidate(
                repo, manifest.Scope.IntegrationBranch, manifest.BaseSha,
                assembly.CandidateSha, ct);
            if (!published.Success)
            {
                State(manifest, BatchPhase.Paused, assembly.CandidateSha, lease.Fence, published.Status);
                if (published.Status == "stale-base") ReturnToPending(manifest);
                return;
            }
            _store.RecordPublication(new BatchGatePublication(
                manifest.BatchId, manifest.MembershipDigest, run.BatchRunId,
                manifest.BaseSha, assembly.CandidateSha, published.Sha,
                lease.Fence, refLease.Fence, _time.GetUtcNow()));
            // Local ref follows the verified remote object. Acceptance still
            // checks ancestry and the batch record before Completed.
            var local = _git.FastForwardIntegrationBranch(repo,
                manifest.Scope.IntegrationBranch, assembly.CandidateSha);
            if (!local.Success)
                throw new IOException(local.Error ?? "Verified batch could not advance the local integration ref.");
            State(manifest, BatchPhase.Published, assembly.CandidateSha, lease.Fence);
        }
        foreach (var key in assembly.AdmittedKeys)
            await ReleaseMemberAsync(manifest, run, pending[key], ct)
                .ConfigureAwait(false);
        foreach (var key in assembly.EjectedKeys)
            await RunPerTaskFallbackAsync(pending[key], ct).ConfigureAwait(false);
    }

    private async Task IsolateAsync(BatchGateManifest failedManifest,
        BatchGateAssembly failedAssembly, BatchCoordinatorLease lease,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, CancellationToken ct)
    {
        var ordered = failedManifest.Members
            .Where(member => failedAssembly.AdmittedKeys.Contains(member.TaskKey))
            .ToArray();
        var green = new Dictionary<string,
            (BatchGateManifest Manifest, BatchGateAssembly Assembly,
                BatchGateRunRecord Run, BatchGateRunVerdict Verdict)>(StringComparer.OrdinalIgnoreCase);
        var budget = Math.Min(6, BatchGatePolicy.DiagnosticRunsPerCycle(ordered.Length) * 2);
        var isolated = await BatchGateIsolation.RunAsync(ordered,
            async (subset, token) =>
            {
                var digest = BatchGatePolicy.MembershipDigest(
                    failedManifest.BaseSha, failedManifest.Scope, subset);
                var diagnostic = new BatchGateManifest(
                    Guid.NewGuid().ToString("N"), failedManifest.Scope,
                    failedManifest.BaseSha, digest, _time.GetUtcNow(),
                    subset.ToArray(), [], subset.Select(item => item.TaskKey).ToArray(),
                    failedManifest.BatchId);
                _store.CloseManifest(diagnostic);
                var candidate = new BatchGateAssembler(_store, _git, repo)
                    .Assemble(diagnostic.BatchId, lease.Fence);
                if (candidate.EjectedKeys.Count > 0 || candidate.AdmittedKeys.Count != subset.Count)
                    return new BatchGateProbeResult(BatchGateFailureClass.InfrastructureRed,
                        candidate.CandidateSha, "diagnostic-replay-incomplete");
                var probeRun = NewRun(diagnostic, candidate.CandidateSha,
                    lease.Fence, repo, _settings.Get(diagnostic.Scope.Project).BuildProfile);
                _store.RecordRun(probeRun);
                State(diagnostic, BatchPhase.Running, candidate.CandidateSha, lease.Fence);
                var gate = await RunGateAsync(probeRun, repo, token,
                    () => _settings.Get(diagnostic.Scope.Project).BatchGate.Enabled
                        && subset.All(member =>
                        Refresh(pending[member.TaskKey]).Subject.CurrentGeneration))
                    .ConfigureAwait(false);
                var classification = gate.IsInfrastructureFailure
                    ? BatchGateFailureClass.InfrastructureRed
                    : gate.Verdict == BuildTestGateVerdict.Ok
                      && string.Equals(gate.TestedSha, candidate.CandidateSha,
                          StringComparison.OrdinalIgnoreCase)
                        ? BatchGateFailureClass.Pass
                        : BatchGateFailureClass.DeterministicSuiteRed;
                var verdict = RecordVerdict(probeRun, gate,
                    classification == BatchGateFailureClass.Pass
                        ? "pass" : classification.ToString());
                State(diagnostic, classification == BatchGateFailureClass.Pass
                        ? BatchPhase.Green : BatchPhase.Red,
                    candidate.CandidateSha, lease.Fence, classification.ToString());
                if (classification == BatchGateFailureClass.Pass)
                    green[candidate.CandidateSha] = (diagnostic, candidate, probeRun, verdict);
                return new BatchGateProbeResult(classification,
                    candidate.CandidateSha, probeRun.EvidencePath);
            }, budget, ct).ConfigureAwait(false);
        if (isolated.SurvivorVerdict is not { Classification: BatchGateFailureClass.Pass } survivor
            || !green.TryGetValue(survivor.TestedSha, out var prepared))
        {
            State(failedManifest, BatchPhase.Paused, failedAssembly.CandidateSha,
                lease.Fence, "unresolved-cohort:" + string.Join(',', isolated.UnresolvedCohortKeys));
            foreach (var key in isolated.UnresolvedCohortKeys
                         .Concat(isolated.SurvivorKeys)
                         .Concat(isolated.EjectedKeys)
                         .Concat(failedAssembly.EjectedKeys)
                         .Distinct(StringComparer.Ordinal))
                await RunPerTaskFallbackAsync(pending[key], ct).ConfigureAwait(false);
            return;
        }
        await PublishGreenAsync(prepared.Manifest, prepared.Assembly,
            prepared.Run, prepared.Verdict, lease, pending, repo, ct).ConfigureAwait(false);
        if (_store.LatestState(prepared.Manifest.BatchId).Phase == BatchPhase.Published)
        {
            foreach (var key in isolated.EjectedKeys
                         .Concat(failedAssembly.EjectedKeys)
                         .Distinct(StringComparer.Ordinal))
                await RunPerTaskFallbackAsync(pending[key], ct).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(BatchCoordinatorLease lease, CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, cancellation.Token).ConfigureAwait(false);
            if (_leases.Renew(lease) is null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private BatchGateRunRecord NewRun(BatchGateManifest manifest, string sha,
        long fence, string repo, BuildProfile? profile)
    {
        var inspection = Path.Combine(Path.GetTempPath(),
            "agentstudio-batch-plan-" + Guid.NewGuid().ToString("N"));
        var added = _git.WorktreeAddDetached(repo, inspection, sha);
        if (!added.Success)
            throw new IOException(added.Error ?? "Candidate command plan could not be materialized.");
        string[] commands;
        try
        {
            commands = VerifyCommandPlanner.Plan(inspection, profile).Commands
                .Select(command => command.Command).ToArray();
        }
        finally
        {
            var removed = _git.WorktreeRemove(repo, inspection);
            if (!removed.Success)
                throw new IOException(removed.Error ?? "Candidate command-plan worktree cleanup failed.");
        }
        return new BatchGateRunRecord(
            Guid.NewGuid().ToString("N"), manifest.BatchId,
            manifest.MembershipDigest, manifest.BaseSha, sha,
            manifest.Scope.GateProfile, manifest.Scope.GateProfileDigest,
            Environment.MachineName, fence, commands, _time.GetUtcNow(),
            Path.Combine(_store.BatchDirectory(manifest.BatchId), "gate-evidence.jsonl"));
    }

    private async Task<BuildTestGateResult> RunGateAsync(BatchGateRunRecord run,
        string repo, CancellationToken ct, Func<bool>? stillCurrent = null)
    {
        var started = _time.GetUtcNow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gateTask = _gate.RunAsync(new BuildTestGateRequest(
                repo, run.CandidateSha, "batch-gate")
            {
                Project = _store.ReadManifest(run.BatchId).Scope.Project,
                GateId = "batch-gate",
                SubjectRef = run.CandidateSha,
                DisableVerdictCache = true,
            }, null, _settings.Get(_store.ReadManifest(run.BatchId).Scope.Project).BuildProfile,
            PostStepMode.Fail, TimeSpan.FromMinutes(45), linked.Token);
        var overloadMinutes = 0d;
        while (!gateTask.IsCompleted)
        {
            var tick = Task.Delay(TimeSpan.FromSeconds(15), ct);
            if (await Task.WhenAny(gateTask, tick).ConfigureAwait(false) == gateTask) break;
            if (stillCurrent is not null && !stillCurrent())
            {
                await linked.CancelAsync().ConfigureAwait(false);
                try { await gateTask.ConfigureAwait(false); }
                catch (OperationCanceledException ex)
                {
                    SilentCatch.Note(ex, "Superseded batch gate stopped after cleanup");
                }
                throw new OperationCanceledException("Batch member was superseded during the gate.");
            }
            if (_load?.Current.Throttle == true) overloadMinutes += .25;
        }
        var result = await gateTask.ConfigureAwait(false);
        var project = _store.ReadManifest(run.BatchId).Scope.Project;
        if (GateProfile(_settings.Get(project).BuildProfile, repo)
            != run.GateProfileDigest)
            throw new InvalidDataException("Batch gate profile changed during execution.");
        using var stream = new FileStream(run.EvidencePath,
            FileMode.Append, FileAccess.Write, FileShare.None);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
        _store.RecordExecution(new BatchGateExecutionFact(
            project,
            run.BatchId, run.BatchRunId, result.TestedSha ?? run.CandidateSha,
            result.Verdict.ToString(), Environment.MachineName, started,
            _time.GetUtcNow(), result.GateQueueWaitMs, overloadMinutes,
            run.EvidencePath));
        return result;
    }

    private BatchGateRunVerdict RecordVerdict(BatchGateRunRecord run,
        BuildTestGateResult gate, string classification)
    {
        var verdict = new BatchGateRunVerdict(
            run.BatchRunId, run.BatchId, run.MembershipDigest,
            gate.TestedSha ?? run.CandidateSha, run.GateProfileDigest,
            classification is "pass" or "flaky-quarantined-pass" ? "pass" : "fail", classification,
            run.EvidencePath, _time.GetUtcNow());
        _store.RecordVerdict(verdict);
        return verdict;
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
        var record = new BatchGateMemberRecord(
            subject.TaskKey, subject.RunAttempt, subject.DeliveryEpoch,
            subject.ResultSha, replay.Replacements.Select(item => item.RebasedSha).ToArray(),
            manifest.BatchId, manifest.MembershipDigest, manifest.BaseSha,
            run.CandidateSha, run.GateProfileDigest, run.BatchRunId,
            "pass", run.EvidencePath, _time.GetUtcNow());
        if (_store.TryReadMember(manifest.BatchId, subject.TaskKey, run.BatchRunId) is null)
            _store.RecordMember(record);
        if (!_store.CanRelease(subject, manifest.BatchId, run.BatchRunId))
            throw new InvalidDataException("batch-gate-evidence-missing");
        var task = _scanner.FindJob(subject.TaskKey, pending.WatchPath);
        if (task is null || task.State != TaskStates.AutoReview) return;
        var rewritten = replay.Replacements.Where(item =>
            !string.Equals(item.OriginalSha, item.RebasedSha,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rewritten.Length > 0 && !_mutations.RecordMechanicalRebaseOnFolder(
                task.FolderPath, rewritten))
            throw new InvalidDataException("Batch replacement SHA mapping could not be attributed.");
        BatchGateOwnershipStore.Write(task.FolderPath,
            new BatchGateOwnership(pending.ReviewAttemptId, subject,
                manifest.BatchId, run.BatchRunId));
        var moved = await _transitions.MoveAsync(
            task.Id, TaskStates.HumanReview, task.WatchPath, ct,
            cause: $"batch-gate:{manifest.BatchId}",
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: "batch-gate-passed").ConfigureAwait(false);
        if (moved.Status != MoveJobStatus.Success)
            throw new IOException($"Batch lane release failed: {moved.Status} {moved.Message}");
        _reviewJournal.RecordRemoteReviewParkVerdict(
            task.ProjectName, task.Id, moved.NewFolderPath ?? task.FolderPath,
            "Pass", "Batch gate passed on a verified candidate SHA.");
        RecordBatchIntegration(moved.NewFolderPath ?? task.FolderPath,
            manifest, run, replay);
        _store.ResolvePending(pending.ReviewAttemptId, "published");
    }

    private void RecordBatchIntegration(string folder,
        BatchGateManifest manifest, BatchGateRunRecord run,
        BatchGateReplayRecord replay)
    {
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

    // One per-task gate per review generation in this process. A retried
    // settlement request, or a tick that meets a running fallback, joins it
    // instead of starting a second gate on the same immutable subject.
    private Task RunPerTaskFallbackAsync(BatchGatePendingRecord pending,
        CancellationToken ct, bool recordInStore = true)
    {
        Task flight;
        lock (_fallbackFlights)
        {
            if (!_fallbackFlights.TryGetValue(pending.ReviewAttemptId, out flight!))
            {
                flight = RunFallbackFlightAsync(pending, recordInStore, ct);
                _fallbackFlights[pending.ReviewAttemptId] = flight;
            }
        }
        return flight.WaitAsync(ct);
    }

    private bool FallbackInFlight(string reviewAttemptId)
    {
        lock (_fallbackFlights) return _fallbackFlights.ContainsKey(reviewAttemptId);
    }

    private async Task RunFallbackFlightAsync(BatchGatePendingRecord pending,
        bool recordInStore, CancellationToken ct)
    {
        // Yield first so the flight is registered before it can complete.
        await Task.Yield();
        try
        {
            await RunPerTaskFallbackCoreAsync(pending, ct, recordInStore).ConfigureAwait(false);
        }
        finally
        {
            lock (_fallbackFlights) _fallbackFlights.Remove(pending.ReviewAttemptId);
        }
    }

    private async Task RunPerTaskFallbackCoreAsync(BatchGatePendingRecord pending,
        CancellationToken ct, bool recordInStore)
    {
        var refreshed = Refresh(pending);
        if (!refreshed.Subject.CurrentGeneration)
        {
            if (recordInStore) ResolveSuperseded(pending, "superseded");
            else BatchGateOwnershipStore.ClearIfReviewAttempt(pending.JobFolderPath,
                pending.ReviewAttemptId);
            return;
        }
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (task is null || task.State != TaskStates.AutoReview) return;
        var evidence = Path.Combine(TaskPaths.LogsDir(task.FolderPath),
            $"batch-fallback-{pending.ReviewAttemptId}.json");
        var ownership = BatchGateOwnershipStore.Read(task.FolderPath);
        var alreadyPassed = ownership?.ReviewAttemptId == pending.ReviewAttemptId
            && string.Equals(ownership.FallbackTestedSha, pending.Subject.ResultSha,
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(evidence);
        if (!alreadyPassed)
        {
            // Starts are counted only across interrupted gates of this review
            // generation; the budget turns a gate that never returns into
            // GateInfra instead of an endless restart.
            var starts = ownership?.ReviewAttemptId == pending.ReviewAttemptId
                && ownership.FallbackGateActive ? ownership.FallbackGateStarts : 0;
            if (starts >= FallbackGateStartBudget)
            {
                await EscalateFallbackAsync(task, pending,
                    $"GateInfra: per-task gate interrupted {starts} times", recordInStore, ct)
                    .ConfigureAwait(false);
                return;
            }
            BatchGateOwnershipStore.Write(task.FolderPath,
                new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject,
                    FallbackGateActive: true, FallbackGateStarts: starts + 1));
            var started = _time.GetUtcNow();
            BuildTestGateResult gate;
            try
            {
                gate = await _gate.RunAsync(new BuildTestGateRequest(
                        pending.RepositoryPath, pending.Subject.ResultSha, "batch-fallback")
                    {
                        Project = pending.Subject.Project,
                        SubjectRef = pending.Subject.ResultRef,
                    }, null, _settings.Get(pending.Subject.Project).BuildProfile,
                    PostStepMode.Fail, TimeSpan.FromMinutes(45), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The active marker stays; the next tick resumes this gate.
                _logger.LogWarning(ex, "batch-fallback-gate-interrupted task={TaskKey} attempt={AttemptId} start={Start}",
                    pending.Subject.TaskKey, pending.ReviewAttemptId, starts + 1);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            using (var stream = new FileStream(evidence, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, gate);
                stream.Flush(flushToDisk: true);
            }
            if (recordInStore)
                _store.RecordExecution(new BatchGateExecutionFact(
                    pending.Subject.Project, null, null,
                    gate.TestedSha ?? pending.Subject.ResultSha,
                    gate.Verdict.ToString(), Environment.MachineName,
                    started, _time.GetUtcNow(), gate.GateQueueWaitMs, 0, evidence));
            if (gate.Verdict != BuildTestGateVerdict.Ok
                || !string.Equals(gate.TestedSha, pending.Subject.ResultSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                await EscalateFallbackAsync(task, pending,
                    gate.Reason ?? (gate.IsInfrastructureFailure
                        ? "GateInfra" : "deterministic-suite-red"), recordInStore, ct)
                    .ConfigureAwait(false);
                return;
            }
            ownership = new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject,
                FallbackTestedSha: gate.TestedSha,
                FallbackEvidencePath: Path.GetFileName(evidence));
            BatchGateOwnershipStore.Write(task.FolderPath, ownership);
        }
        var result = await _directIntegration.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
            task.ProjectName, task.Id, task.FolderPath, task.WatchPath,
            pending.Subject.IntegrationBranch, pending.IntegrationStrategy,
            pending.PipelineType, pending.Subject.ReviewCompletedAtUtc)).ConfigureAwait(false);
        if (!result.Outcome.IsSuccessfulIntegration()) return;
        BatchGateOwnershipStore.Write(task.FolderPath,
            ownership! with { FallbackIntegrated = true });
        var moved = await _transitions.MoveAsync(task.Id, TaskStates.HumanReview,
            task.WatchPath, ct, cause: $"batch-fallback:{pending.ReviewAttemptId}",
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: "per-task-gate-passed").ConfigureAwait(false);
        if (moved.Status == MoveJobStatus.Success)
        {
            if (recordInStore)
                _store.ResolvePending(pending.ReviewAttemptId, "per-task-gate");
            _reviewJournal.RecordRemoteReviewParkVerdict(task.ProjectName, task.Id,
                moved.NewFolderPath ?? task.FolderPath, "Pass",
                "Per-task fallback gate passed on the immutable result SHA.");
        }
    }

    private async Task EscalateFallbackAsync(TaskInfo task, BatchGatePendingRecord pending,
        string reason, bool recordInStore, CancellationToken ct)
    {
        var movedRed = await _reviewJournal.EscalateAsync(task.Id,
            task.WatchPath, task.ProjectName,
            HumanReviewEscalationCategories.AutoReviewEscalation,
            reason, ct).ConfigureAwait(false);
        if (movedRed.Status != MoveJobStatus.Success) return;
        BatchGateOwnershipStore.ClearIfReviewAttempt(
            movedRed.NewFolderPath ?? task.FolderPath, pending.ReviewAttemptId);
        if (recordInStore)
            _store.ResolvePending(pending.ReviewAttemptId, "per-task-gate-red");
    }

    private BatchGatePendingRecord Refresh(BatchGatePendingRecord pending)
    {
        var subject = pending.Subject;
        var projection = _authority.GetTaskProjection(subject.TaskKey);
        var task = _scanner.FindJob(subject.TaskKey, pending.WatchPath);
        var settings = _settings.Get(subject.Project);
        var ownership = task is null ? null : BatchGateOwnershipStore.Read(task.FolderPath);
        var current = task is { State: TaskStates.AutoReview }
            && projection.CurrentRunAttempt?.AttemptId == subject.RunAttempt
            && projection.CurrentReviewAttempt?.AttemptId == pending.ReviewAttemptId
            && projection.CurrentReviewAttempt.Outcome == ReviewTerminalOutcome.Pass
            && string.Equals(projection.CurrentReviewAttempt.TestedResultSha,
                subject.ResultSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(projection.CurrentRunAttempt.ResultEnvelope?.ResultSha,
                subject.ResultSha, StringComparison.OrdinalIgnoreCase)
            && projection.CurrentRunAttempt.ResultEnvelope?.ImmutableRemoteRef == subject.ResultRef
            && projection.CurrentRunAttempt.LastFence == subject.FencingToken
            && projection.CurrentRunAttempt.AuthorityEpoch == subject.DeliveryEpoch;
        return pending with
        {
            Subject = subject with
            {
                CurrentGeneration = current,
                ActivePerTaskGate = ownership?.FallbackGateActive == true,
                OwningBatchId = ownership?.BatchId,
                GateProfileDigest = GateProfile(settings.BuildProfile, pending.RepositoryPath),
                PlatformVersion = PlatformVersion(),
                FencingToken = projection.CurrentRunAttempt?.LastFence ?? subject.FencingToken,
                DeliveryEpoch = projection.CurrentRunAttempt?.AuthorityEpoch
                    ?? subject.DeliveryEpoch,
                ResultRef = projection.CurrentRunAttempt?.ResultEnvelope?.ImmutableRemoteRef
                    ?? subject.ResultRef,
                ResultSha = projection.CurrentRunAttempt?.ResultEnvelope?.ResultSha
                    ?? subject.ResultSha,
            },
        };
    }

    private void ResolveSuperseded(BatchGatePendingRecord pending, string reason)
    {
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (task is not null)
            BatchGateOwnershipStore.ClearIfReviewAttempt(task.FolderPath,
                pending.ReviewAttemptId);
        _store.ResolvePending(pending.ReviewAttemptId, reason);
    }

    private void State(BatchGateManifest manifest, BatchPhase phase,
        string? sha, long fence, string? reason = null,
        string? runId = null, long? refFence = null)
        => _store.AppendState(new BatchGateState(manifest.BatchId,
            manifest.MembershipDigest, phase, sha, fence, _time.GetUtcNow(),
            reason, runId, refFence));

    private static BatchGateScope Scope(BatchGateSubject subject)
        => new(subject.Project, subject.Repository,
            subject.IntegrationBranch, subject.GateProfile,
            subject.GateProfileDigest, subject.PlatformVersion);

    private static string GateProfile(BuildProfile? profile, string repo)
    {
        var commands = VerifyCommandPlanner.Plan(repo, profile).Commands
            .Select(command => new { command.Command, command.WorkingSubdir,
                kind = command.Kind.ToString() }).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { fingerprint = BuildProfileValidationFingerprint.Create(profile), commands })));
    }

    private static string PlatformVersion()
        => typeof(BatchGatePilotService).Assembly.GetName().Version?.ToString() ?? "unknown";
}

public sealed class BatchGatePilotHostedService : BackgroundService
{
    private readonly BatchGatePilotService _pilot;
    private readonly ILogger<BatchGatePilotHostedService> _logger;

    public BatchGatePilotHostedService(BatchGatePilotService pilot,
        ILogger<BatchGatePilotHostedService> logger)
    {
        _pilot = pilot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _pilot.TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Batch gate pilot tick failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
        }
    }
}
