using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Pipeline;

/// <summary>Project-scoped route from settled review to verified batch publication.</summary>
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
    private readonly BatchGateSnapshotBuilder _snapshots;
    private readonly BatchGateAdvisorService? _advisor;
    private readonly BatchIntegrationReservationService? _reservations;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly GateSlotUsageTracker? _gateSlots;
    private readonly PipelineHealthDetector? _pipelineHealth;
    private readonly ConcurrentDictionary<string, string> _activeBatchGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pushAgeAlerts =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly Dictionary<string, Task> _fallbackFlights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _runningBatches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Coordinator lease renewal cadence; well inside the two-minute grant.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal bool WaitForFlightsInTick { get; set; }

    /// <summary>Per-task gate starts per review generation before GateInfra (D8).</summary>
    internal const int FallbackGateStartBudget = 2;

    public BatchGatePilotService(ITokenPricing pricing,
        BatchGateStore store, BatchGateLeaseService leases,
        RefMutationLeaseService refLeases, GitService git,
        IBuildTestGateRunner gate, AttemptAuthorityService authority,
        TaskScannerService scanner, ProjectSettingsService settings,
        TaskTransitionService transitions, TaskMutationService mutations,
        HumanReviewEscalation reviewJournal,
        RemoteDeliveryIntegrationCoordinator directIntegration,
        ILogger<BatchGatePilotService> logger,
        ILoadThrottleGate? load = null, TimeProvider? timeProvider = null,
        BatchGateAdvisorService? advisor = null,
        BatchIntegrationReservationService? reservations = null,
        PipelineExecutionLog? pipelineLog = null,
        GateSlotUsageTracker? gateSlots = null,
        PipelineHealthDetector? pipelineHealth = null)
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
        _snapshots = new BatchGateSnapshotBuilder(store, authority, scanner, git, logger);
        _advisor = advisor;
        _reservations = reservations;
        _reservations?.LoadPersistedProjects(_settings.GetAll().Keys);
        _pipelineLog = pipelineLog ?? new PipelineExecutionLog(pricing,
            NullLogger<PipelineExecutionLog>.Instance);
        _gateSlots = gateSlots;
        _pipelineHealth = pipelineHealth;
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
        Lane(pending, "enqueued", deliveredAtUtc: pending.Subject.ReviewCompletedAtUtc);
        return pending;
    }

    public async Task RunEmergencyFallbackAsync(TaskInfo task, ReviewAttemptDto review,
        RunAttemptDto sourceRun, DateTimeOffset reviewedAtUtc, CancellationToken ct)
    {
        if (task.State != TaskStates.AutoReview) return;
        var pending = BuildPending(task, review, sourceRun, reviewedAtUtc);
        await RunPerTaskFallbackAsync(pending, ct, recordInStore: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the review-side verification minutes of a settled batch-deferred
    /// review once per review attempt, whatever its outcome. Replays find the
    /// fact and are no-ops; a failed write is logged and never fails settlement.
    /// </summary>
    public bool RecordReviewVerification(string project, ReviewAttemptDto review,
        IReadOnlyList<AgentStudio.TaskServer.Contracts.ReviewCommandEvidenceDto> commands)
    {
        if (review.Subject.Plan?.BuildTestDeferredToBatch != true) return false;
        try
        {
            var minutes = BatchGateReviewVerificationCount.Count(commands);
            return _store.RecordReviewVerification(new BatchGateReviewVerificationFact(
                project, review.TaskKey, review.AttemptId,
                review.TestedResultSha ?? review.Subject.ExpectedResultSha,
                review.Outcome?.ToString() ?? "unknown",
                minutes.CandidateMinutes, minutes.BaselineMinutes,
                minutes.CountedCommands, _time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "batch-gate-review-verification-record-failed attempt={AttemptId}",
                review.AttemptId);
            return false;
        }
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
        // The result SHA against the fetched integration line: the Studio
        // checkout has neither the local result ref nor a current local branch.
        var changed = BatchGateDeliveryDiff.ChangedPaths(_git, repo, branch,
            envelope?.ResultSha);
        var profile = GateProfile(project.BuildProfile, repo);
        var projection = _authority.GetTaskProjection(AuthorityKey(task));
        var subject = new BatchGateSubject(
            AuthorityKey(task), task.ProjectName, review.RepositoryId, branch,
            "complete-suite", profile, PlatformVersion(),
            envelope?.ImmutableRemoteRef ?? string.Empty,
            envelope?.ResultSha ?? string.Empty,
            sourceRun.AttemptId, sourceRun.AuthorityEpoch,
            sourceRun.LastFence,
            review.TestedResultSha ?? string.Empty,
            envelope is not null && sourceRun.State == AttemptLifecycleState.Completed,
            review.Outcome is ReviewTerminalOutcome.Pass
                or ReviewTerminalOutcome.IntegrationBranchDefect,
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

    // The attempt authority keys a card by its reference key in the flat task
    // layout (AGT-3048) and by its folder id in the legacy lane layout. Use the
    // first identity the authority knows, as ReviewDecisionOrchestrator does.
    private string AuthorityKey(TaskInfo task)
        => new[] { task.Key, task.TaskKey, task.Id }
               .Where(value => !string.IsNullOrWhiteSpace(value))
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .FirstOrDefault(value => !_authority.GetTaskProjection(value!).LegacyTask)
           ?? task.Key ?? task.Id;

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
            var projection = _authority.GetTaskProjection(AuthorityKey(task));
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
            falseCompleted, staleReleased) with
        {
            PendingPushWait = CodeBatchEnabled(project)
                ? _reservations?.GetWait(project) : null,
        };
    }

    public bool RequestEject(string project, string taskKey)
    {
        var found = _store.ListPending().Any(item => item.Subject.Project == project
            && item.Subject.TaskKey == taskKey
            && (BatchGateOwnershipStore.Read(item.JobFolderPath)?.BatchId is not { } batchId
                || _store.ReadPublication(batchId) is null));
        if (found) _store.RequestEject(project, taskKey);
        return found;
    }

    public void RequestCloseNow(string project) => _store.RequestCloseNow(project);

    public object State(string project, int history = 10)
    {
        var options = _settings.Get(project).BatchGate;
        var pending = _store.ListPending().Where(item =>
            string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray();
        var risks = pending.Length == 0
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : _snapshots.Build(project, pending[0].Subject.IntegrationBranch,
                pending, options, _time.GetUtcNow()).Waiting
                .GroupBy(card => card.TaskKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Risk,
                    StringComparer.Ordinal);
        return BatchGateStateSnapshotReader.Read(_store, project, options, history,
            risks, CodeBatchEnabled(project) ? _reservations?.GetWait(project) : null,
            _gateSlots?.Snapshot());
    }

    private bool CodeBatchEnabled(string project)
    {
        var options = _settings.Get(project).BatchGate;
        return options.Enabled && options.EffectiveScope == "code";
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _tick.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var fetchedBases = new Dictionary<(string Repo, string Branch), string?>();
            if (_reservations is not null)
                foreach (var project in _reservations.ProjectsWithPending())
                {
                    try
                    {
                        if (!CodeBatchEnabled(project)) continue;
                        using var grant = _reservations.TryAcquire(project);
                        if (grant is null) continue;
                        _reservations.Reconcile(project,
                            marker => IsStalePush(project, marker, fetchedBases, ct));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "batch-gate push reconciliation failed project={Project}", project);
                    }
                    var wait = _reservations.GetWait(project);
                    if (wait is { AgeAlert: true }
                        && _pushAgeAlerts.TryAdd(project + "\n" + wait.JobId
                            + "\n" + wait.ApprovedSha, 0))
                        _logger.LogWarning(
                            "batch-gate push wait exceeded age limit project={Project} job={JobId} approvedSha={ApprovedSha} since={SinceUtc}",
                            project, wait.JobId, wait.ApprovedSha, wait.SinceUtc);
                }
            RecoverInterrupted();
            await ResolvePausedAsync(fetchedBases, ct).ConfigureAwait(false);
            await RecoverPublishedAsync(ct).ConfigureAwait(false);
            ReconcileFormedBatches();
            StartClosedBatches(ct);
            foreach (var item in _store.ListPending().Where(item =>
                         _store.HasEject(item.Subject.Project, item.Subject.TaskKey)
                         && string.IsNullOrWhiteSpace(
                             BatchGateOwnershipStore.Read(item.JobFolderPath)?.BatchId)).ToArray())
            {
                ScheduleFallback(item, ct, clearEject: true);
            }
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
                    _store.ResolvePending(item.ReviewAttemptId,
                        ownership.ContainedInSha is null ? "per-task-gate" : AlreadyIntegratedResolution);
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
                ScheduleFallback(item, ct);
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
            waiting = _store.ListPending().Select(Refresh)
                .Where(item => _snapshots.PendingExclusion(item) is null
                    && !HeldOnRedBase(item)).ToArray();
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
                    _store.ClearCloseNow(first.Subject.Project);
                    foreach (var item in group)
                        if (PrepareDisabledFallback(item))
                            ScheduleFallback(item, ct);
                    continue;
                }
                var scope = Scope(first.Subject);
                var baseSha = FetchBaseOnce(fetchedBases, first.RepositoryPath,
                    scope.IntegrationBranch, ct);
                // AGT-3103: a member the branch already carries (for example
                // one an operator pushed before its review settled) needs no
                // candidate and no per-task gate. It leaves as integrated.
                var contained = baseSha is null
                    ? []
                    : group.Where(item => BatchGatePolicy.LeavesAsIntegrated(item.Subject,
                            resultSha => _git.IsDeliveryPublished(item.RepositoryPath,
                                resultSha, baseSha)))
                        .ToArray();
                foreach (var item in contained)
                    ScheduleFallback(item, ct, containedInSha: baseSha);
                var current = group.Except(contained).ToArray();
                if (options.EffectiveScope == "documentation")
                    foreach (var item in current.Where(item => !item.Subject.DocumentationOnly
                                 && BatchGateOwnershipStore.Read(item.JobFolderPath)?.BatchId is null))
                        ScheduleFallback(item, ct);
                if (baseSha is null || current.Length == 0) continue;
                BatchGateFormation formed;
                if (options.Planner == "look-ahead")
                {
                    var eligible = current.Where(item => BatchGatePolicy.Exclusion(
                        item.Subject, scope, options) is null).ToArray();
                    var snapshot = _snapshots.Build(first.Subject.Project,
                        scope.IntegrationBranch, eligible, options, _time.GetUtcNow(),
                        _store.HasCloseNow(first.Subject.Project),
                        _load?.Current.Throttle == false
                            && !AnyBatchRunning());
                    var priorHold = _store.LatestPlanDecision(first.Subject.Project);
                    var vetoHold = priorHold is
                    {
                        Decision: "wait", Advisor.Verdict: "veto", CloseAtUtc: not null,
                    } && !_store.HasCloseNow(first.Subject.Project);
                    var holdExpired = vetoHold
                        && priorHold!.CloseAtUtc!.Value <= _time.GetUtcNow();
                    var decision = BatchGateLookAheadPlanner.Plan(holdExpired
                        ? snapshot with { CloseNow = true } : snapshot);
                    if (vetoHold && !holdExpired)
                        decision = decision with
                        {
                            Decision = "wait", Reason = priorHold!.Reason,
                            CloseAtUtc = priorHold.CloseAtUtc,
                            Advisor = priorHold.Advisor,
                        };
                    else if (_advisor is not null && !holdExpired
                             && !_store.HasCloseNow(first.Subject.Project))
                        decision = await _advisor.AdviseAsync(first.Subject.Project,
                            _settings.Get(first.Subject.Project), snapshot, decision, ct)
                            .ConfigureAwait(false);
                    _store.AppendPlanDecision(first.Subject.Project, decision);
                    if (decision.Decision is "close" or "per-task")
                        _store.ClearCloseNow(first.Subject.Project);
                    formed = decision.Decision switch
                    {
                        "close" => BatchGatePolicy.Close(current.Select(item => item.Subject),
                            scope, baseSha, options, decision.ProposedMembers, _time.GetUtcNow()),
                        "per-task" => new BatchGateFormation(null, [],
                            decision.ProposedMembers, true),
                        _ => new BatchGateFormation(null, [], [], false),
                    };
                }
                else
                {
                    if (_store.HasCloseNow(first.Subject.Project))
                    {
                        var eligible = current.Where(item => BatchGatePolicy.Exclusion(
                                item.Subject, scope, options) is null)
                            .OrderBy(item => item.Subject.EnqueueSequence)
                            .Select(item => item.Subject.TaskKey).ToArray();
                        formed = BatchGatePolicy.Close(current.Select(item => item.Subject),
                            scope, baseSha, options, eligible, _time.GetUtcNow());
                        _store.ClearCloseNow(first.Subject.Project);
                    }
                    else
                    {
                        formed = BatchGatePolicy.Form(current.Select(item => item.Subject),
                            scope, baseSha, options, _time.GetUtcNow(),
                            _authority.ListPendingReviewAttempts().Count(review =>
                                string.Equals(_scanner.FindJob(review.TaskKey)?.ProjectName,
                                    first.Subject.Project, StringComparison.OrdinalIgnoreCase)),
                            _load?.Current.Throttle == true);
                    }
                    _store.AppendPlanDecision(first.Subject.Project,
                        new BatchPlanDecision(
                            formed.Manifest is not null ? "close"
                                : formed.UsePerTaskGate ? "per-task"
                                : formed.EligibleKeys.Count == 0 ? "idle" : "wait",
                            formed.Manifest is not null ? "threshold reached"
                                : formed.UsePerTaskGate ? "per-card gate selected"
                                : "waiting for threshold or deadline",
                            formed.Manifest is null && current.Length > 0
                                ? current.Min(item => item.Subject.ReviewCompletedAtUtc)
                                    .AddMinutes(options.DeadlineMinutes) : null,
                            formed.Manifest?.Members.Select(member => member.TaskKey).ToArray()
                                ?? formed.EligibleKeys,
                            options.CloseSize, []));
                }
                if (formed.UsePerTaskGate)
                {
                    foreach (var item in current.Where(item =>
                                 formed.EligibleKeys.Contains(item.Subject.TaskKey)))
                        ScheduleFallback(item, ct);
                    continue;
                }
                if (formed.Manifest is null) continue;
                _store.CloseManifest(formed.Manifest);
                if (options.Planner == "look-ahead"
                    && _store.LatestPlanDecision(first.Subject.Project)?.Advisor is { } advice)
                    _store.RecordAdvice(formed.Manifest.BatchId, advice);
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
                StartBatch(formed.Manifest,
                    current.ToDictionary(item => item.Subject.TaskKey,
                        StringComparer.Ordinal), first.RepositoryPath, ct);
            }
            if (WaitForFlightsInTick)
            {
                // A batch can schedule its ejected members only after releasing
                // the project reservation. Observe that second wave as well.
                while (true)
                {
                    Task[] batches;
                    Task[] fallbacks;
                    lock (_runningBatches) batches = _runningBatches.Values.ToArray();
                    lock (_fallbackFlights) fallbacks = _fallbackFlights.Values.ToArray();
                    if (batches.Length == 0 && fallbacks.Length == 0) break;
                    await Task.WhenAll(batches.Concat(fallbacks)).WaitAsync(ct).ConfigureAwait(false);
                    lock (_runningBatches)
                        if (_runningBatches.Values.Any(task => !task.IsCompleted)) continue;
                    lock (_fallbackFlights)
                        if (_fallbackFlights.Values.Any(task => !task.IsCompleted)) continue;
                    break;
                }
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
            if (_store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
                continue;
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

    internal void ReconcileFormedBatches()
    {
        foreach (var manifest in _store.ListRecoverableManifests()
                     .Where(manifest => _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed))
        {
            BatchCoordinatorLease? lease;
            try { lease = _leases.TryAcquire(manifest.Scope, "formed-batch-replan"); }
            catch (IOException) { continue; }
            if (lease is null) continue;
            try { ReconcileFormedBatch(manifest, lease.Fence); }
            finally { _leases.Release(lease); }
        }
    }

    private bool ReconcileFormedBatch(BatchGateManifest manifest, long fence)
    {
        if (_store.LatestState(manifest.BatchId).Phase != BatchPhase.Formed) return false;
        var waiting = _store.ListPending();
        var exclusions = manifest.Members.Select(member =>
        {
            var pending = waiting.FirstOrDefault(item => item.Subject.TaskKey == member.TaskKey
                && item.Subject.RunAttempt == member.RunAttempt);
            var reason = pending is null ? null : _snapshots.PendingExclusion(pending);
            return reason is null ? null : $"{member.TaskKey}: {reason}";
        }).Where(reason => reason is not null).ToArray();
        if (exclusions.Length == 0) return false;

        // Closed manifests are immutable. Retire this snapshot with a per-member
        // audit reason, then let the normal planner select the eligible survivors.
        ReturnToPending(manifest);
        State(manifest, BatchPhase.Abandoned, null, fence,
            "formed-batch-replanned; removed " + string.Join("; ", exclusions));
        return true;
    }

    private void StartClosedBatches(CancellationToken ct)
    {
        var waiting = _store.ListPending();
        foreach (var manifest in _store.ListRecoverableManifests()
                     .Where(manifest => _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed))
        {
            var members = manifest.Members.Select(member => waiting.FirstOrDefault(item =>
                item.Subject.TaskKey == member.TaskKey
                && item.Subject.RunAttempt == member.RunAttempt)).ToArray();
            if (members.Any(item => item is null))
            {
                var recovery = _leases.TryAcquire(manifest.Scope, "formed-batch-recovery");
                if (recovery is not null)
                {
                    try
                    {
                        State(manifest, BatchPhase.Abandoned, null,
                            recovery.Fence, "closed member no longer pending");
                        ReturnToPending(manifest);
                    }
                    finally { _leases.Release(recovery); }
                }
                continue;
            }
            var current = members.Cast<BatchGatePendingRecord>().ToArray();
            StartBatch(manifest, current.ToDictionary(item => item.Subject.TaskKey,
                StringComparer.Ordinal), current[0].RepositoryPath, ct);
        }
    }

    private void StartBatch(BatchGateManifest manifest,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, CancellationToken ct)
    {
        lock (_runningBatches)
        {
            if (_runningBatches.TryGetValue(manifest.Scope.Project, out var existing)
                && !existing.IsCompleted) return;
            var flight = Task.Run(() => ExecuteAsync(manifest, pending, repo, ct), ct);
            _runningBatches[manifest.Scope.Project] = flight;
            _ = flight.ContinueWith(done =>
            {
                if (done.Exception is { } error)
                    _logger.LogError(error, "batch-gate-flight-failed batch={BatchId}", manifest.BatchId);
                lock (_runningBatches)
                {
                    if (_runningBatches.TryGetValue(manifest.Scope.Project, out var current)
                        && ReferenceEquals(current, done))
                        _runningBatches.Remove(manifest.Scope.Project);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private bool AnyBatchRunning()
    {
        lock (_runningBatches)
            return _runningBatches.Values.Any(task => !task.IsCompleted);
    }

    // A pause stops the batch, not its members. Every member the paused batch
    // still owns returns to the per-task gate on its unchanged immutable
    // subject, which either integrates the card or escalates it with the gate
    // reason. Members are released before the terminal state is written, so an
    // interruption in between only repeats an idempotent release.
    private async Task ResolvePausedAsync(
        Dictionary<(string Repo, string Branch), string?> fetchedBases,
        CancellationToken ct)
    {
        foreach (var manifest in _store.ListPausedManifests())
        {
            ct.ThrowIfCancellationRequested();
            BatchCoordinatorLease? lease;
            try { lease = _leases.TryAcquire(manifest.Scope, "batch-pause-fallback"); }
            catch (IOException) { continue; }
            if (lease is null) continue;
            IReadOnlyList<BatchGatePendingRecord> released;
            var ejectedOnly = false;
            try
            {
                var paused = _store.LatestState(manifest.BatchId);
                var options = _settings.Get(manifest.Scope.Project).BatchGate;
                if (paused.Reason == "integration-branch-red" && options.Enabled
                    && (options.EffectiveScope == "code"
                        || manifest.Members.All(member => member.DocumentationOnly)))
                {
                    if (manifest.Members.Any(member =>
                            _store.HasEject(manifest.Scope.Project, member.TaskKey)))
                    {
                        released = ReturnToPending(manifest);
                        State(manifest, BatchPhase.Abandoned, paused.CandidateSha,
                            lease.Fence, "operator eject from held batch");
                        ejectedOnly = true;
                    }
                    else
                    {
                        var pending = _store.ListPending().FirstOrDefault(item =>
                            manifest.Members.Any(member => member.TaskKey == item.Subject.TaskKey
                                && member.RunAttempt == item.Subject.RunAttempt));
                        if (pending is null) continue;
                        var currentBase = FetchBaseOnce(fetchedBases,
                            pending.RepositoryPath, manifest.Scope.IntegrationBranch, ct);
                        if (currentBase is null || string.Equals(currentBase, manifest.BaseSha,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        ReturnToPending(manifest);
                        State(manifest, BatchPhase.Abandoned, paused.CandidateSha, lease.Fence,
                            "integration base changed; reconstruct batch");
                        continue;
                    }
                }
                else
                {
                    _logger.LogWarning("batch-gate-paused batch={BatchId} reason={Reason}",
                        manifest.BatchId, paused.Reason);
                    released = ReturnToPending(manifest);
                    State(manifest, BatchPhase.Abandoned, paused.CandidateSha, lease.Fence,
                        "paused-to-per-task-gate: " + paused.Reason);
                }
            }
            finally { _leases.Release(lease); }
            foreach (var item in released)
                if (!ejectedOnly || _store.HasEject(item.Subject.Project, item.Subject.TaskKey))
                    ScheduleFallback(item, ct, clearEject: ejectedOnly);
        }
    }

    private bool HeldOnRedBase(BatchGatePendingRecord pending)
    {
        var batchId = BatchGateOwnershipStore.Read(pending.JobFolderPath)?.BatchId;
        if (batchId is null) return false;
        var state = _store.LatestState(batchId);
        return state.Phase == BatchPhase.Paused
            && state.Reason == "integration-branch-red";
    }

    private string? FetchBaseOnce(
        Dictionary<(string Repo, string Branch), string?> fetchedBases,
        string repo, string branch, CancellationToken ct)
        => FetchBaseOnce(fetchedBases, repo, branch, _git.FetchBatchIntegrationTip, ct);

    internal static string? FetchBaseOnce(
        Dictionary<(string Repo, string Branch), string?> fetchedBases,
        string repo, string branch,
        Func<string, string, CancellationToken, string?> fetch,
        CancellationToken ct)
    {
        var key = (repo, branch);
        if (!fetchedBases.TryGetValue(key, out var sha))
        {
            sha = fetch(repo, branch, ct);
            fetchedBases[key] = sha;
        }
        return sha;
    }

    internal bool IsStalePush(string project, PendingIntegrationPush marker,
        Dictionary<(string Repo, string Branch), string?> fetchedBases,
        CancellationToken ct)
    {
        var task = _scanner.FindJob(marker.JobId, marker.WatchPath);
        // The merge can enqueue its push before the lane leaves Auto Review.
        // Keep that short transition window fenced.
        if (task is null || task.State is not (TaskStates.AutoReview
                or TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive))
            return true;
        var record = _pipelineLog.Read(task.FolderPath)
            ?? (marker.JobFolderPath is null ? null
                : _pipelineLog.Read(marker.JobFolderPath));
        var merge = record?.Steps.LastOrDefault(step =>
            step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        if (merge?.Status != PipelineStepStatus.Passed
            || !string.Equals(merge.ApprovedIntegrationSha, marker.ApprovedSha,
                StringComparison.OrdinalIgnoreCase))
            return true;
        var push = record?.Steps.LastOrDefault(step =>
            step.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId);
        if (push?.StartedAt is { } pushStarted
            && merge.CompletedAt is { } mergeCompleted
            && pushStarted >= mergeCompleted
            && (push.Status is PipelineStepStatus.Passed or PipelineStepStatus.Skipped
                || push.Status == PipelineStepStatus.Failed
                    && push.Verdict is ("push-blocked" or "lineage-blocked")))
            return true;
        var repo = marker.RepositoryPath
            ?? _git.ResolveRepoRootForWatchPath(task.WatchPath);
        var branch = marker.IntegrationBranch
            ?? TaskIntegrationBranch.Resolve(task, _settings.Get(project).IntegrationBranch);
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(branch)
            || string.IsNullOrWhiteSpace(marker.ApprovedSha)) return false;
        var remote = FetchBaseOnce(fetchedBases, repo, branch, ct);
        return remote is not null && _git.IsAncestor(repo, marker.ApprovedSha, remote);
    }

    private async Task RecoverPublishedAsync(CancellationToken ct)
    {
        var waiting = _store.ListPending();
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
                    State(manifest, BatchPhase.Published,
                        publication.TestedCandidateSha, lease.Fence,
                        "resumed verified publication");
                    _git.FetchBatchIntegrationTip(pendingRepo.RepositoryPath,
                        manifest.Scope.IntegrationBranch, ct);
                    TrySyncPublishedCheckout(pendingRepo.RepositoryPath,
                        manifest.Scope.IntegrationBranch, publication.TestedCandidateSha);
                }
                finally { _leases.Release(lease); }
            }
            if (publication is null) continue;
            var run = _store.ReadRun(manifest.BatchId, publication.BatchRunId);
            if (publication.TestedCandidateSha != run.CandidateSha
                || publication.VerifiedRemoteSha != run.CandidateSha
                || _store.ReadVerdict(manifest.BatchId, run.BatchRunId)?.Outcome != "pass")
                throw new InvalidDataException("batch-gate-evidence-missing");
            var releaseRepo = waiting.FirstOrDefault(item =>
                manifest.Members.Any(member => member.TaskKey == item.Subject.TaskKey
                    && member.RunAttempt == item.Subject.RunAttempt));
            if (releaseRepo is not null)
                _git.FetchBatchIntegrationTip(releaseRepo.RepositoryPath,
                    manifest.Scope.IntegrationBranch, ct);
            foreach (var member in manifest.Members)
            {
                var pending = waiting.FirstOrDefault(item =>
                    item.Subject.TaskKey == member.TaskKey
                    && item.Subject.RunAttempt == member.RunAttempt);
                if (pending is null) continue;
                var replay = _store.TryReadReplay(manifest.BatchId, member.TaskKey);
                // Every ejected member owes a per-task gate. The in-tick
                // fallback dies with a restart between publication and its
                // first marker, and the published batch's ownership keeps the
                // member out of every later batch, so only this branch can
                // still deliver it.
                if (replay?.Outcome is "conflict" or "unattributable")
                {
                    ScheduleFallback(pending, ct);
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
                Lane(pending, "requeued", reason: $"batch:{manifest.BatchId}");
                released.Add(pending);
            }
        }
        return released;
    }

    private async Task ExecuteAsync(BatchGateManifest manifest,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, CancellationToken ct)
    {
        var fallbackKeys = new HashSet<string>(StringComparer.Ordinal);
        var fetchedBases = new Dictionary<(string Repo, string Branch), string?>();
        using var reservation = _reservations is null ? null
            : await _reservations.AcquireBatchAsync(manifest.Scope.Project,
                marker => IsStalePush(manifest.Scope.Project, marker, fetchedBases, ct),
                ct, fetchedBases.Clear)
                .ConfigureAwait(false);
        BatchCoordinatorLease? lease;
        try { lease = _leases.TryAcquire(manifest.Scope, Environment.MachineName); }
        catch (IOException) { return; }
        if (lease is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(lease, cancellation);
        try
        {
            // A formed flight may have waited on the project reservation while a
            // continuation arrived or another tick retired its manifest.
            if (_store.LatestState(manifest.BatchId).Phase != BatchPhase.Formed
                || ReconcileFormedBatch(manifest, lease.Fence))
                return;
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
            foreach (var member in manifest.Members)
                Lane(pending[member.TaskKey], "dequeued", reason: $"batch:{manifest.BatchId}");
            var currentBase = _git.FetchBatchIntegrationTip(
                repo, manifest.Scope.IntegrationBranch, cancellation.Token);
            if (currentBase is not null && !string.Equals(currentBase, manifest.BaseSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!QueueOnMovedDocumentationBase(manifest, pending, repo,
                        currentBase, lease.Fence))
                {
                    State(manifest, BatchPhase.Abandoned, null, lease.Fence,
                        "base moved before replay");
                    ReturnToPending(manifest);
                }
                return;
            }
            var assembly = new BatchGateAssembler(_store, _git, repo, _time)
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
            {
                var ejection = _store.TryReadReplay(manifest.BatchId, key);
                if (ejection?.Outcome == "unattributable")
                    _logger.LogWarning("batch-member-unattributable batch={BatchId} task={TaskKey} reason={Reason}",
                        manifest.BatchId, key, ejection.Reason);
                else
                    _logger.LogWarning("batch-member-conflict batch={BatchId} task={TaskKey}", manifest.BatchId, key);
                Lane(pending[key], "ejected", outcome: ejection?.Outcome == "unattributable" ? "Unattributable" : "Conflict",
                    reason: $"batch:{manifest.BatchId}");
            }
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
                    fallbackKeys.Add(key);
                return;
            }
            var run = NewRun(manifest, assembly.CandidateSha, lease.Fence,
                repo, _settings.Get(manifest.Scope.Project).BuildProfile);
            _store.RecordRun(run);
            State(manifest, BatchPhase.Running, assembly.CandidateSha, lease.Fence);
            var gated = assembly.AdmittedKeys.Select(key => pending[key]).ToArray();
            var gate = await RunGateAsync(run, repo, cancellation.Token,
                () => MembersCurrent() && RouteEnabled(), gated).ConfigureAwait(false);
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
                gate = await RunGateAsync(run, repo, cancellation.Token, gated: gated)
                    .ConfigureAwait(false);
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
                // The candidate suite may stop at its first red step. A red
                // base therefore blocks publication and isolation even when
                // the two observed failure fingerprints differ.
                if (await BaseIsRedAsync(manifest, run, gate, repo,
                        cancellation.Token).ConfigureAwait(false)
                    && string.Equals(gate.TestedSha, assembly.CandidateSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    RecordVerdict(run, gate, "pre-existing-base-red");
                    State(manifest, BatchPhase.Paused, assembly.CandidateSha, lease.Fence,
                        "integration-branch-red");
                    return;
                }
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
                    fallbackKeys, cancellation.Token).ConfigureAwait(false);
                return;
            }
            var verdict = RecordVerdict(run, gate,
                gate.FlakyQuarantinedFailures.Count > 0
                    ? "flaky-quarantined-pass" : "pass");
            State(manifest, BatchPhase.Green, assembly.CandidateSha, lease.Fence);
            _settings.MarkBuildProfileRemotelyValidated(manifest.Scope.Project,
                BuildProfileValidationFingerprint.Create(
                    _settings.Get(manifest.Scope.Project).BuildProfile),
                run.BatchRunId, run.Host, run.CandidateSha, _time.GetUtcNow().UtcDateTime);
            await PublishGreenAsync(manifest, assembly, run, verdict, lease,
                pending, repo, fallbackKeys, cancellation.Token).ConfigureAwait(false);
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
            reservation?.Dispose();
            foreach (var key in fallbackKeys)
                ScheduleFallback(pending[key], ct);
        }

        bool MembersCurrent() => manifest.Members.All(member =>
            !_store.HasEject(manifest.Scope.Project, member.TaskKey)
            && Refresh(pending[member.TaskKey]).Subject.CurrentGeneration);

        bool RouteEnabled() => BatchRouteEnabled(manifest);

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
        string repo, ISet<string> fallbackKeys, CancellationToken ct)
    {
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
                remoteTip ?? string.Empty, _leases.IsCurrent(lease),
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
            if (!_leases.IsCurrent(lease) || !RefMutationLeaseService.IsCurrent(refLease))
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
            var published = _git.PublishBatchCandidate(
                repo, manifest.Scope.IntegrationBranch, manifest.BaseSha,
                assembly.CandidateSha, ct);
            if (!published.Success)
            {
                if (published.Status == "stale-base"
                    && QueueOnMovedDocumentationBase(manifest, pending, repo,
                        _git.FetchBatchIntegrationTip(repo,
                            manifest.Scope.IntegrationBranch, ct), lease.Fence))
                    return;
                State(manifest, BatchPhase.Paused, assembly.CandidateSha, lease.Fence, published.Status);
                if (published.Status == "stale-base") ReturnToPending(manifest);
                return;
            }
            _store.RecordPublication(new BatchGatePublication(
                manifest.BatchId, manifest.MembershipDigest, run.BatchRunId,
                manifest.BaseSha, assembly.CandidateSha, published.Sha,
                lease.Fence, refLease.Fence, _time.GetUtcNow()));
            State(manifest, BatchPhase.Published, assembly.CandidateSha, lease.Fence);
            foreach (var key in assembly.AdmittedKeys)
                Lane(pending[key], "published", outcome: published.Status,
                    receipt: published.Sha, reason: $"batch:{manifest.BatchId}");
            // A push by object SHA does not necessarily advance origin/develop
            // in this checkout. The lane status projection reads that mirror.
            _git.FetchBatchIntegrationTip(repo, manifest.Scope.IntegrationBranch, ct);
            TrySyncPublishedCheckout(repo, manifest.Scope.IntegrationBranch,
                assembly.CandidateSha);
        }
        foreach (var key in assembly.EjectedKeys)
            fallbackKeys.Add(key);
        foreach (var key in assembly.AdmittedKeys)
            await ReleaseMemberAsync(manifest, run, pending[key], ct)
                .ConfigureAwait(false);
    }

    private void TrySyncPublishedCheckout(string repo, string branch, string sha)
    {
        try
        {
            var local = _git.FastForwardPublishedBatchCheckout(repo, branch, sha);
            if (!local.Success)
                _logger.LogWarning("batch-local-sync-deferred branch={Branch} sha={Sha} reason={Reason}",
                    branch, sha, local.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "batch-local-sync-deferred branch={Branch} sha={Sha}", branch, sha);
        }
    }

    private bool QueueOnMovedDocumentationBase(BatchGateManifest manifest,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, string? newBase, long fence)
    {
        if (string.IsNullOrWhiteSpace(newBase)
            || !_git.IsAncestor(repo, manifest.BaseSha, newBase)
            || !DocsOnlyDeliveryPolicy.IsDocsOnly(
                _git.ChangedPathsAgainstMergeBase(repo, manifest.BaseSha, newBase))
            || manifest.Members.Any(member =>
                !Refresh(pending[member.TaskKey]).Subject.CurrentGeneration))
            return false;
        State(manifest, BatchPhase.Abandoned, null, fence,
            "documentation-only base movement; successor replay queued");
        ReturnToPending(manifest);
        var members = manifest.Members.Select(member =>
            Refresh(pending[member.TaskKey]).Subject).ToArray();
        var successor = manifest with
        {
            BatchId = Guid.NewGuid().ToString("N"),
            BaseSha = newBase,
            MembershipDigest = BatchGatePolicy.MembershipDigest(newBase,
                manifest.Scope, members),
            ClosedAtUtc = _time.GetUtcNow(),
            Members = members,
            ParentBatchId = manifest.BatchId,
        };
        _store.CloseManifest(successor);
        foreach (var member in members)
        {
            var item = pending[member.TaskKey];
            var task = _scanner.FindJob(member.TaskKey, item.WatchPath);
            if (task is not null)
                BatchGateOwnershipStore.Write(task.FolderPath,
                    new BatchGateOwnership(item.ReviewAttemptId, member,
                        successor.BatchId));
        }
        return true;
    }

    private async Task IsolateAsync(BatchGateManifest failedManifest,
        BatchGateAssembly failedAssembly, BatchCoordinatorLease lease,
        IReadOnlyDictionary<string, BatchGatePendingRecord> pending,
        string repo, ISet<string> fallbackKeys, CancellationToken ct)
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
                var candidate = new BatchGateAssembler(_store, _git, repo, _time)
                    .Assemble(diagnostic.BatchId, lease.Fence);
                if (candidate.EjectedKeys.Count > 0 || candidate.AdmittedKeys.Count != subset.Count)
                    return new BatchGateProbeResult(BatchGateFailureClass.InfrastructureRed,
                        candidate.CandidateSha, "diagnostic-replay-incomplete");
                var probeRun = NewRun(diagnostic, candidate.CandidateSha,
                    lease.Fence, repo, _settings.Get(diagnostic.Scope.Project).BuildProfile);
                _store.RecordRun(probeRun);
                State(diagnostic, BatchPhase.Running, candidate.CandidateSha, lease.Fence);
                var gate = await RunGateAsync(probeRun, repo, token,
                    () => BatchRouteEnabled(diagnostic)
                        && subset.All(member =>
                        Refresh(pending[member.TaskKey]).Subject.CurrentGeneration),
                    subset.Select(member => pending[member.TaskKey]).ToArray())
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
                fallbackKeys.Add(key);
            return;
        }
        await PublishGreenAsync(prepared.Manifest, prepared.Assembly,
            prepared.Run, prepared.Verdict, lease, pending, repo, fallbackKeys, ct).ConfigureAwait(false);
        if (_store.LatestState(prepared.Manifest.BatchId).Phase == BatchPhase.Published)
        {
            foreach (var key in isolated.EjectedKeys
                         .Concat(failedAssembly.EjectedKeys)
                         .Distinct(StringComparer.Ordinal))
                fallbackKeys.Add(key);
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
        string repo, CancellationToken ct, Func<bool>? stillCurrent = null,
        IReadOnlyList<BatchGatePendingRecord>? gated = null)
    {
        var started = _time.GetUtcNow();
        foreach (var member in gated ?? [])
            Lane(member, "gate-requested", level: TestExecutionLevels.Full,
                reuseToken: run.BatchRunId, reason: $"batch:{run.BatchId}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var project = _store.ReadManifest(run.BatchId).Scope.Project;
        var gateTask = _gate.RunAsync(new BuildTestGateRequest(
                repo, run.CandidateSha, "batch-gate")
            {
                Project = project,
                GateId = "batch-gate",
                SubjectRef = run.CandidateSha,
                IntegrationRef = run.BaseSha,
                RequiredTestLevel = TestExecutionLevels.Full,
                TestExecution = _settings.Get(_store.ReadManifest(run.BatchId).Scope.Project).TestExecution,
                DisableVerdictCache = true,
                OnMachineGateWaiting = () =>
                {
                    var holder = _pipelineHealth?.HoldingProject()
                        ?? _activeBatchGates.Values.FirstOrDefault();
                    foreach (var member in gated ?? [])
                        Lane(member, "gate-token-waiting", level: TestExecutionLevels.Full,
                            holdingProject: holder);
                },
                OnMachineGateAcquired = () =>
                {
                    _activeBatchGates[run.BatchRunId] = project;
                    foreach (var member in gated ?? [])
                        Lane(member, "gate-token-acquired", level: TestExecutionLevels.Full);
                },
            }, null, _settings.Get(_store.ReadManifest(run.BatchId).Scope.Project).BuildProfile,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(_settings.Get(_store.ReadManifest(run.BatchId).Scope.Project)
                .BatchGate.GateTimeoutMinutes), linked.Token);
        var overloadMinutes = 0d;
        BuildTestGateResult result;
        try
        {
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
            result = await gateTask.ConfigureAwait(false);
        }
        finally { _activeBatchGates.TryRemove(run.BatchRunId, out _); }
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
            run.EvidencePath, result.DurationMs, gated?.Select(member => member.Subject.TaskKey).ToArray()));
        foreach (var member in gated ?? [])
            Lane(member, "gate-finished", level: TestExecutionLevels.Full,
                durationMs: result.DurationMs, queueWaitMs: result.GateQueueWaitMs,
                outcome: result.Verdict.ToString(), reuseToken: run.BatchRunId,
                reason: $"batch:{run.BatchId}");
        return result;
    }

    private BatchGateRunVerdict RecordVerdict(BatchGateRunRecord run,
        BuildTestGateResult gate, string classification)
    {
        var verdict = new BatchGateRunVerdict(
            run.BatchRunId, run.BatchId, run.MembershipDigest,
            gate.TestedSha ?? run.CandidateSha, run.GateProfileDigest,
            classification is "pass" or "flaky-quarantined-pass"
                ? "pass" : "fail", classification,
            run.EvidencePath, _time.GetUtcNow());
        _store.RecordVerdict(verdict);
        return verdict;
    }

    private async Task<bool> BaseIsRedAsync(BatchGateManifest manifest,
        BatchGateRunRecord run, BuildTestGateResult candidate, string repo,
        CancellationToken ct)
    {
        if (candidate.Verdict != BuildTestGateVerdict.Fail
            || candidate.FailureKind != BuildTestGateFailureKind.Code)
            return false;
        var baseline = _store.ReadBaseVerdict(manifest.Scope.Project,
            manifest.BaseSha, run.GateProfileDigest);
        if (baseline is null)
        {
            var settings = _settings.Get(manifest.Scope.Project);
            if (GateProfile(settings.BuildProfile, repo) != run.GateProfileDigest)
                throw new InvalidDataException("Batch gate profile changed before base verification.");
            var started = _time.GetUtcNow();
            var result = await _gate.RunAsync(new BuildTestGateRequest(
                    repo, manifest.BaseSha, "batch-base")
                {
                    Project = manifest.Scope.Project,
                    GateId = "batch-base",
                    SubjectRef = manifest.BaseSha,
                    IntegrationRef = manifest.BaseSha,
                    RequiredTestLevel = TestExecutionLevels.Full,
                    TestExecution = settings.TestExecution,
                    DisableVerdictCache = true,
                }, null, settings.BuildProfile, PostStepMode.Fail,
                TimeSpan.FromMinutes(settings.BatchGate.GateTimeoutMinutes), ct)
                .ConfigureAwait(false);
            if (GateProfile(_settings.Get(manifest.Scope.Project).BuildProfile, repo)
                != run.GateProfileDigest)
                throw new InvalidDataException("Batch gate profile changed during base verification.");
            if (!string.Equals(result.TestedSha, manifest.BaseSha,
                    StringComparison.OrdinalIgnoreCase)
                || result.IsInfrastructureFailure)
                throw new IOException("The base verification did not produce an exact-SHA verdict.");
            var evidencePath = Path.Combine(_store.BatchDirectory(manifest.BatchId),
                "base-" + manifest.BaseSha + ".json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(result));
            baseline = new BatchGateBaseVerdict(manifest.Scope.Project,
                manifest.BaseSha, run.GateProfileDigest,
                result.Verdict == BuildTestGateVerdict.Ok ? "pass" : "fail",
                result.Verdict == BuildTestGateVerdict.Fail
                    ? BuildTestGateRunner.DiagnosticFingerprint(result) : null,
                result.Reason, evidencePath, _time.GetUtcNow());
            _store.RecordBaseVerdict(baseline);
            _store.RecordExecution(new BatchGateExecutionFact(
                manifest.Scope.Project, manifest.BatchId, null, manifest.BaseSha,
                result.Verdict.ToString(), Environment.MachineName,
                started, _time.GetUtcNow(), result.GateQueueWaitMs, 0, evidencePath));
        }
        return baseline.Outcome == "fail";
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
        // A merged member rewrote no commit: every delivery SHA is an ancestor
        // of the candidate, and the result maps to its merge commit below.
        var rewritten = replay.Strategy == BatchReplayStrategies.Merge ? []
            : replay.Replacements.Where(item =>
                !string.Equals(item.OriginalSha, item.RebasedSha,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rewritten.Length > 0 && !_mutations.RecordMechanicalRebaseOnFolder(
                task.FolderPath, rewritten))
            throw new InvalidDataException("Batch replacement SHA mapping could not be attributed.");
        BatchGateOwnershipStore.Write(task.FolderPath,
            new BatchGateOwnership(pending.ReviewAttemptId, subject,
                manifest.BatchId, run.BatchRunId));
        // The lane guard reads integration attribution before it permits the
        // move. A rebased member is not an ancestor by its original SHA, so
        // record its verified replacement mapping first.
        RecordBatchIntegration(task.FolderPath, manifest, run, replay);
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
        _store.ResolvePending(pending.ReviewAttemptId, "published");
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

    // One per-task gate per review generation in this process. A retried
    // settlement request, or a tick that meets a running fallback, joins it
    // instead of starting a second gate on the same immutable subject.
    private void ScheduleFallback(BatchGatePendingRecord pending,
        CancellationToken ct, bool clearEject = false, string? containedInSha = null)
    {
        var flight = RunPerTaskFallbackAsync(pending, ct, containedInSha: containedInSha);
        _ = flight.ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully && clearEject)
                _store.ClearEject(pending.Subject.Project, pending.Subject.TaskKey);
            if (done.Exception is { } error)
                _logger.LogError(error, "batch-fallback-flight-failed task={TaskKey}",
                    pending.Subject.TaskKey);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task RunPerTaskFallbackAsync(BatchGatePendingRecord pending,
        CancellationToken ct, bool recordInStore = true, string? containedInSha = null)
    {
        Task flight;
        lock (_fallbackFlights)
        {
            if (!_fallbackFlights.TryGetValue(pending.ReviewAttemptId, out flight!))
            {
                flight = RunFallbackFlightAsync(pending, recordInStore, containedInSha, ct);
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
        bool recordInStore, string? containedInSha, CancellationToken ct)
    {
        // Yield first so the flight is registered before it can complete.
        await Task.Yield();
        try
        {
            await RunPerTaskFallbackCoreAsync(pending, ct, recordInStore, containedInSha).ConfigureAwait(false);
        }
        finally
        {
            lock (_fallbackFlights) _fallbackFlights.Remove(pending.ReviewAttemptId);
        }
    }

    private async Task RunPerTaskFallbackCoreAsync(BatchGatePendingRecord pending,
        CancellationToken ct, bool recordInStore, string? containedInSha = null)
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
        // AGT-3139: a patch equivalent publication carries the result's tree
        // under another SHA. The per-task coordinator checks ancestry, so it
        // would merge and gate it again; the containment record releases it.
        var patchEquivalent = containedInSha is not null
            && !_git.IsAncestor(pending.RepositoryPath, pending.Subject.ResultSha, containedInSha);
        if (containedInSha is not null)
        {
            // AGT-3103: the branch already carries this result. Record the SHA
            // that proved it and integrate as a no-op; no gate runs.
            ownership = RecordContainment(task, pending, containedInSha,
                fallbackIntegrated: patchEquivalent);
        }
        else if (!alreadyPassed)
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
            Lane(pending, "gate-requested", level: TestExecutionLevels.Full,
                reason: "per-task-fallback");
            BuildTestGateResult gate;
            try
            {
                gate = await _gate.RunAsync(new BuildTestGateRequest(
                        pending.RepositoryPath, pending.Subject.ResultSha, "batch-fallback")
                    {
                        Project = pending.Subject.Project,
                        SubjectRef = pending.Subject.ResultRef,
                        RequiredTestLevel = TestExecutionLevels.Full,
                        TestExecution = _settings.Get(pending.Subject.Project).TestExecution,
                    }, null, _settings.Get(pending.Subject.Project).BuildProfile,
                    PostStepMode.Fail,
                    TimeSpan.FromMinutes(_settings.Get(pending.Subject.Project)
                        .BatchGate.GateTimeoutMinutes), ct).ConfigureAwait(false);
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
            // Every per-task gate that ran costs host minutes, emergency
            // fallbacks included; only the queue mutations depend on recordInStore.
            _store.RecordExecution(new BatchGateExecutionFact(
                pending.Subject.Project, null, null,
                gate.TestedSha ?? pending.Subject.ResultSha,
                gate.Verdict.ToString(), Environment.MachineName,
                started, _time.GetUtcNow(), gate.GateQueueWaitMs, 0, evidence));
            Lane(pending, "gate-finished", level: TestExecutionLevels.Full,
                durationMs: gate.DurationMs, queueWaitMs: gate.GateQueueWaitMs,
                outcome: gate.Verdict.ToString(), reason: "per-task-fallback");
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
        if (!patchEquivalent)
        {
            // The coordinator opens its own lane sequence for the same delivery.
            Lane(pending, "handed-off", reason: "per-task-fallback");
            var result = await _directIntegration.EnqueueAsync(new RemoteDeliveryIntegrationRequest(
                task.ProjectName, task.Id, task.FolderPath, task.WatchPath,
                pending.Subject.IntegrationBranch, pending.IntegrationStrategy,
                pending.PipelineType, pending.Subject.ReviewCompletedAtUtc)).ConfigureAwait(false);
            if (!result.Outcome.IsSuccessfulIntegration())
            {
                // A contained member whose tree already failed its gate has a
                // verdict; replaying the no-op on every tick would not change it.
                if (containedInSha is not null && result.Outcome == MergeIntoIntegrationOutcome.GateFailed)
                    await EscalateFallbackAsync(task, pending,
                        result.Error ?? "integration-unverified", recordInStore, ct).ConfigureAwait(false);
                return;
            }
            BatchGateOwnershipStore.Write(task.FolderPath,
                ownership! with { FallbackIntegrated = true });
        }
        var moved = await _transitions.MoveAsync(task.Id, TaskStates.HumanReview,
            task.WatchPath, ct, cause: $"batch-fallback:{pending.ReviewAttemptId}",
            suppressProductExecution: true,
            expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: containedInSha is null ? "per-task-gate-passed" : AlreadyIntegratedResolution)
            .ConfigureAwait(false);
        if (moved.Status == MoveJobStatus.Success)
        {
            if (recordInStore)
                _store.ResolvePending(pending.ReviewAttemptId,
                    containedInSha is null ? "per-task-gate" : AlreadyIntegratedResolution);
            _reviewJournal.RecordRemoteReviewParkVerdict(task.ProjectName, task.Id,
                moved.NewFolderPath ?? task.FolderPath, "Pass",
                containedInSha is null
                    ? "Per-task fallback gate passed on the immutable result SHA."
                    : $"The integration branch already contained the result SHA at {containedInSha}; "
                      + "no batch candidate and no gate ran.");
        }
    }

    /// <summary>Pending-queue resolution of a member that left as integrated (AGT-3103).</summary>
    internal const string AlreadyIntegratedResolution = "already-integrated";

    /// <summary>
    /// AGT-3103: the durable evidence of a member that left the queue because
    /// the integration branch already contained its result SHA. The lane
    /// release guard reads it instead of a gate verdict.
    /// </summary>
    private BatchGateOwnership RecordContainment(TaskInfo task,
        BatchGatePendingRecord pending, string containedInSha, bool fallbackIntegrated)
    {
        var ownership = BatchGateOwnershipStore.RecordContainment(task.FolderPath,
            new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject),
            containedInSha, _time, fallbackIntegrated);
        // The lane guard reads integration attribution before it permits the
        // move, and a patch equivalent result is no ancestor of the branch.
        if (!_mutations.AppendIntegrationRecordOnFolder(task.FolderPath,
                BatchGateOwnershipStore.ContainmentMapping(ownership, _time.GetUtcNow())).Succeeded)
            throw new IOException("Batch containment mapping could not be recorded.");
        _logger.LogInformation(
            "batch-member-already-integrated task={TaskKey} attempt={AttemptId} result={ResultSha} containedIn={ContainedIn}",
            pending.Subject.TaskKey, pending.ReviewAttemptId, pending.Subject.ResultSha, containedInSha);
        return ownership;
    }

    private async Task EscalateFallbackAsync(TaskInfo task, BatchGatePendingRecord pending,
        string reason, bool recordInStore, CancellationToken ct)
    {
        var movedRed = await _reviewJournal.EscalateAsync(task.Id,
            task.WatchPath, task.ProjectName,
            HumanReviewEscalationCategories.AutoReviewEscalation,
            reason, ct).ConfigureAwait(false);
        if (movedRed.Status != MoveJobStatus.Success) return;
        Lane(pending, "ejected", outcome: reason.StartsWith("GateInfra", StringComparison.Ordinal)
            ? "GateInfra" : "GateFailed", reason: reason);
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
            && (projection.CurrentReviewAttempt.Outcome is ReviewTerminalOutcome.Pass
                or ReviewTerminalOutcome.IntegrationBranchDefect)
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
                ActivePerTaskGate = ownership?.FallbackGateActive == true
                    || FallbackInFlight(pending.ReviewAttemptId),
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
        Lane(pending, "ejected", outcome: "Superseded", reason: reason);
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        if (task is not null)
            BatchGateOwnershipStore.ClearIfReviewAttempt(task.FolderPath,
                pending.ReviewAttemptId);
        _store.ResolvePending(pending.ReviewAttemptId, reason);
    }

    // AGT-3070: typed lane events for batch-deferred deliveries. The durable
    // batch enqueue sequence identifies the delivery; an emergency fallback
    // was never enqueued, has no sequence and records nothing.
    private void Lane(BatchGatePendingRecord pending, string kind,
        DateTimeOffset? deliveredAtUtc = null, long? queueWaitMs = null,
        long? durationMs = null, long? publicationLockWaitMs = null,
        string? level = null, string? outcome = null, string? reuseToken = null,
        string? reason = null, string? receipt = null,
        string? holdingProject = null)
    {
        if (pending.Subject.EnqueueSequence <= 0) return;
        var folder = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath)?.FolderPath
            ?? pending.JobFolderPath;
        IntegrationLaneEvents.Append(folder, pending.Subject.Project, pending.Subject.TaskKey,
            kind, sequence: pending.Subject.EnqueueSequence, deliveredAtUtc: deliveredAtUtc,
            queueWaitMs: queueWaitMs, durationMs: durationMs,
            publicationLockWaitMs: publicationLockWaitMs, level: level, outcome: outcome,
            reuseToken: reuseToken, reason: reason, receipt: receipt,
            holdingProject: holdingProject, atUtc: _time.GetUtcNow());
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

    private bool BatchRouteEnabled(BatchGateManifest manifest)
    {
        var options = _settings.Get(manifest.Scope.Project).BatchGate;
        return options.Enabled && (options.EffectiveScope == "code"
            || manifest.Members.All(member => member.DocumentationOnly));
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
