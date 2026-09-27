using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Projects;

namespace AgentStudio.Pipeline;

/// <summary>
/// Opt-in documentation pilot from a settled Remote Review to a verified
/// integration tip. The pending record is durable before the worker can form
/// a manifest; no task leaves Auto Review until its own gate record validates.
/// </summary>
public sealed class BatchGateRuntime
{
    private readonly BatchGateStore _store;
    private readonly BatchGateLeaseService _coordinatorLeases;
    private readonly ProjectRefMutationLeaseService _refLeases;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly GitService _git;
    private readonly IBuildTestGateRunner _gate;
    private readonly RemoteDeliveryIntegrationCoordinator _perTask;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly HumanReviewEscalation _escalation;
    private readonly ILogger<BatchGateRuntime> _logger;
    private readonly AutoReviewPostProcessingQueue? _reviewQueue;
    private readonly PipelineExecutionLog? _pipelineLog;

    public BatchGateRuntime(BatchGateStore store, BatchGateLeaseService coordinatorLeases,
        ProjectRefMutationLeaseService refLeases, AttemptAuthorityService authority,
        TaskScannerService scanner, ProjectSettingsService settings, GitService git,
        IBuildTestGateRunner gate, RemoteDeliveryIntegrationCoordinator perTask,
        TaskTransitionService transitions, TaskMutationService mutations,
        HumanReviewEscalation escalation, ILogger<BatchGateRuntime> logger,
        AutoReviewPostProcessingQueue? reviewQueue = null,
        PipelineExecutionLog? pipelineLog = null)
    {
        _store = store;
        _coordinatorLeases = coordinatorLeases;
        _refLeases = refLeases;
        _authority = authority;
        _scanner = scanner;
        _settings = settings;
        _git = git;
        _gate = gate;
        _perTask = perTask;
        _transitions = transitions;
        _mutations = mutations;
        _escalation = escalation;
        _logger = logger;
        _reviewQueue = reviewQueue;
        _pipelineLog = pipelineLog;
    }

    public bool QueueSettledReview(TaskInfo task, ReviewAttemptDto review,
        RunAttemptDto? run, DateTime receivedAt)
    {
        var settings = _settings.Get(task.ProjectName);
        if (!settings.BatchGate.DocumentationOnly
            || IntegrationStrategies.Normalize(settings.IntegrationStrategy)
                == IntegrationStrategies.PullRequest
            || review.Subject.Plan?.BuildTestDeferredToBatch != true
            || review.Outcome != ReviewTerminalOutcome.Pass)
            return false;
        var profile = Scope(task, run?.RepositoryId ?? review.RepositoryId, settings);
        var envelope = run?.ResultEnvelope;
        var subject = new BatchGateSubject(
            review.TaskKey, task.ProjectName, profile.Repository,
            profile.IntegrationBranch, profile.GateProfile,
            profile.GateProfileDigest, profile.PlatformVersion,
            envelope?.ImmutableRemoteRef ?? string.Empty,
            run?.ResultSha ?? string.Empty,
            run?.AttemptId ?? string.Empty,
            run?.AuthorityEpoch ?? 0,
            run?.LastFence ?? 0,
            review.TestedResultSha ?? string.Empty,
            run is { State: AttemptLifecycleState.Completed, ResultEnvelopeDigest: not null }
                && envelope is not null
                && string.Equals(AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
                    run.ResultEnvelopeDigest, StringComparison.OrdinalIgnoreCase),
            ModelReviewPassed: true,
            BuildTestDeferredToBatch: true,
            CurrentGeneration: true,
            ActivePerTaskGate: IntegrationGateJournal.Read(task.FolderPath) is not null,
            OwningBatchId: null,
            DocumentationOnly: true,
            ReviewCompletedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(receivedAt, DateTimeKind.Utc)),
            EnqueueSequence: DateTime.UtcNow.Ticks);
        if (BatchGatePolicy.Exclusion(subject, profile, settings.BatchGate) is not null)
            return false;
        _store.Queue(new BatchGatePendingDelivery(subject, review.AttemptId,
            task.FolderPath, task.WatchPath, DateTimeOffset.UtcNow));
        return true;
    }

    public bool IsPendingCurrentReview(TaskInfo task, ReviewAttemptDto? review)
        => review is not null && _store.Pending().Any(pending =>
            pending.Subject.TaskKey == review.TaskKey
            && pending.ReviewAttemptId == review.AttemptId);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var pending = _store.Pending();
        foreach (var project in pending.GroupBy(item => item.Subject.Project, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var options = _settings.Get(project.Key).BatchGate;
            var completedKeys = _scanner.ScanAllAutomationJobs()
                .Where(task => task.State == TaskStates.Completed
                    && string.Equals(task.ProjectName, project.Key, StringComparison.OrdinalIgnoreCase))
                .Select(task => task.Key ?? task.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (!_store.Observe(project.Key, completedKeys).CorrectnessFloorMet)
            {
                _logger.LogCritical("Batch gate correctness floor failed for project {Project}; formation is stopped.",
                    project.Key);
                continue;
            }
            if (!options.Enabled)
            {
                foreach (var owned in project.Where(item => _store.PendingOwner(item) is not null)
                             .GroupBy(item => _store.PendingOwner(item)!, StringComparer.Ordinal))
                {
                    var oldScope = _store.ReadManifest(owned.Key).Scope;
                    var recoveryLease = _coordinatorLeases.TryAcquire(oldScope, Environment.MachineName);
                    if (recoveryLease is null) continue;
                    try { await RecoverClaimedAsync(owned.ToArray(), ct, disabling: true).ConfigureAwait(false); }
                    finally { _coordinatorLeases.Release(recoveryLease); }
                }
                foreach (var item in project)
                {
                    var owner = _store.PendingOwner(item);
                    if (owner is not null) continue;
                    await FallBackAsync(item, ct).ConfigureAwait(false);
                }
                continue;
            }
            foreach (var cohort in project.GroupBy(item => BatchGatePolicy.ScopeOf(item.Subject)))
            {
                var items = cohort.ToArray();
                var scope = cohort.Key;
                var lease = _coordinatorLeases.TryAcquire(scope, Environment.MachineName);
                if (lease is null) continue;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var currentLease = lease;
                var heartbeat = Task.Run(async () =>
                {
                    try
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                        while (await timer.WaitForNextTickAsync(linked.Token))
                        {
                            var renewed = _coordinatorLeases.Renew(currentLease);
                            if (renewed is null) { linked.Cancel(); break; }
                            currentLease = renewed;
                        }
                    }
                    catch (OperationCanceledException ex) when (linked.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, "Batch coordinator heartbeat stopped with its cycle.");
                    }
                }, CancellationToken.None);
                try
                {
                    await RecoverClaimedAsync(items, linked.Token).ConfigureAwait(false);
                    if (items.Any(item => _store.PendingOwner(item) is null))
                    {
                        var liveScope = Scope(scope.Project, scope.Repository,
                            scope.IntegrationBranch, _settings.Get(project.Key));
                        if (liveScope != scope)
                        {
                            foreach (var item in items.Where(item => _store.PendingOwner(item) is null))
                                await FallBackAsync(item, linked.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            var repo = _git.ResolveRepoRootForWatchPath(items[0].WatchPath);
                            if (repo is not null)
                                await ProcessProjectAsync(items, scope, repo, options,
                                    () => currentLease, linked.Token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    linked.Cancel();
                    await heartbeat.ConfigureAwait(false);
                    _coordinatorLeases.Release(currentLease);
                }
            }
        }
    }

    private async Task RecoverClaimedAsync(
        IReadOnlyList<BatchGatePendingDelivery> pending, CancellationToken ct,
        bool disabling = false)
    {
        foreach (var owned in pending.Where(item => _store.PendingOwner(item) is not null)
                     .GroupBy(item => _store.PendingOwner(item)!, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var manifest = _store.ReadManifest(owned.Key);
            var state = _store.LatestState(owned.Key);
            if (state.Phase == BatchPhase.Published)
            {
                var publication = _store.ReadPublication(owned.Key);
                var run = _store.ReadRun(owned.Key, publication.BatchRunId);
                var repo = _git.ResolveRepoRootForWatchPath(owned.First().WatchPath);
                if (repo is null) continue;
                foreach (var item in owned)
                {
                    var task = V1ReviewPlaneEndpoints.FindTask(_scanner, item.Subject.TaskKey);
                    if (task?.State == TaskStates.HumanReview
                        && _store.CanRelease(item.Subject, owned.Key, run.BatchRunId))
                    {
                        _store.CompletePending(item, "released");
                        continue;
                    }
                    if (!CurrentSubject(item).CurrentGeneration)
                    {
                        _store.CompletePending(item, "superseded-after-publication");
                        continue;
                    }
                    await ReleaseMemberAsync(item, manifest, run, repo, ct).ConfigureAwait(false);
                }
            }
            else if (state.Phase is BatchPhase.Formed or BatchPhase.Assembling
                     or BatchPhase.Assembled or BatchPhase.Running or BatchPhase.Green
                     || (disabling && state.Phase is BatchPhase.Red or BatchPhase.Paused))
            {
                _store.AppendState(new BatchGateState(owned.Key, manifest.MembershipDigest,
                    BatchPhase.Abandoned, state.CandidateSha, state.CoordinatorFence,
                    DateTimeOffset.UtcNow, "interrupted-before-verified-publication"));
                foreach (var item in owned) _store.ReleaseClaim(item, owned.Key);
            }
        }
    }

    private async Task ProcessProjectAsync(
        IReadOnlyList<BatchGatePendingDelivery> pending, BatchGateScope scope,
        string repo, BatchGateFormationOptions options,
        Func<BatchCoordinatorLease> currentLease, CancellationToken ct)
    {
        var baseSha = _git.GetRemoteIntegrationTip(repo, scope.IntegrationBranch, ct);
        if (baseSha is null) return;
        var refreshed = pending.Select(CurrentSubject).ToArray();
        var formation = BatchGatePolicy.Form(refreshed, scope, baseSha, options,
            DateTimeOffset.UtcNow, reviewQueueLength: _reviewQueue?.PendingCount ?? 0,
            loadThrottled: BatchHostLoadPressure());
        foreach (var excluded in formation.Exclusions.Where(item =>
                     item.Reason != BatchExclusionReason.NextBatch
                     && item.Reason != BatchExclusionReason.OtherBatchOwnsMember))
        {
            var item = pending.First(candidate => candidate.Subject.TaskKey == excluded.TaskKey);
            if (excluded.Reason == BatchExclusionReason.SupersededGeneration)
                _store.CompletePending(item, "superseded");
            else
                await FallBackAsync(item, ct).ConfigureAwait(false);
        }
        if (formation.UsePerTaskGate)
        {
            foreach (var item in pending)
                await FallBackAsync(item, ct).ConfigureAwait(false);
            return;
        }
        var manifest = formation.Manifest;
        if (manifest is null) return;
        // This is the durable boundary before fetch, worktree, or candidate ref.
        _store.CloseManifest(manifest);
        var selected = manifest.Members.Select(member => pending.Single(item =>
            item.Subject.TaskKey == member.TaskKey && item.Subject.RunAttempt == member.RunAttempt)).ToArray();
        foreach (var item in selected) _store.Claim(item, manifest.BatchId);
        foreach (var member in manifest.Members)
        {
            if (!_git.FetchBatchResultRef(repo, member.ResultRef, member.ResultSha, ct))
                throw new IOException($"Batch result ref could not be verified for {member.TaskKey}.");
        }
        var lease = currentLease();
        if (!_coordinatorLeases.IsCurrent(lease)) return;
        var assembly = new BatchGateAssembler(_store, _git, repo)
            .Assemble(manifest.BatchId, lease.Fence);
        foreach (var key in assembly.EjectedKeys)
        {
            var item = selected.Single(x => x.Subject.TaskKey == key);
            _store.ReleaseClaim(item, manifest.BatchId);
            await FallBackAsync(item, ct).ConfigureAwait(false);
        }
        foreach (var key in assembly.DeferredKeys)
            _store.ReleaseClaim(selected.Single(x => x.Subject.TaskKey == key), manifest.BatchId);
        if (assembly.AdmittedKeys.Count == 0) return;
        if (assembly.AdmittedKeys.Any(key => !CurrentSubject(selected.Single(x => x.Subject.TaskKey == key)).CurrentGeneration))
        {
            _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                BatchPhase.Abandoned, assembly.CandidateSha, lease.Fence,
                DateTimeOffset.UtcNow, "superseded-before-suite"));
            foreach (var item in selected) _store.ReleaseClaim(item, manifest.BatchId);
            return;
        }
        var settings = _settings.Get(scope.Project);
        var verify = VerifyCommandPlanner.Plan(repo, settings.BuildProfile).Commands;
        if (verify.Count == 0) throw new InvalidDataException("The full-suite plan has no commands.");
        var runId = Guid.NewGuid().ToString("N");
        var evidence = Path.Combine(_store.BatchDirectory(manifest.BatchId), "runs", runId + ".evidence.json");
        var run = new BatchGateRunRecord(runId, manifest.BatchId, manifest.MembershipDigest,
            baseSha, assembly.CandidateSha, scope.GateProfile, scope.GateProfileDigest,
            Environment.MachineName, lease.Fence,
            verify.Select(command => command.Command).ToArray(),
            DateTimeOffset.UtcNow, evidence);
        _store.RecordRun(run);
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Running, assembly.CandidateSha, lease.Fence, DateTimeOffset.UtcNow));
        var request = new BuildTestGateRequest(repo, assembly.CandidateSha, "batch-gate")
        {
            GateId = "batch-gate",
            Project = scope.Project,
            SubjectRef = assembly.CandidateRef,
            Lane = TaskStates.AutoReview,
            TestExecution = settings.TestExecution,
            ForceFullSuite = true,
            BypassVerdictCache = true,
        };
        var result = await _gate.RunAsync(request, null, settings.BuildProfile,
            PostStepMode.Fail,
            TimeSpan.FromSeconds(GateRunBudgetPolicy.ResolveSeconds(settings.BuildTestGateTimeoutSeconds)),
            ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(result), ct).ConfigureAwait(false);
        var green = result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn
            && string.Equals(result.TestedSha, assembly.CandidateSha, StringComparison.OrdinalIgnoreCase);
        var classification = green && result.FlakyClassification is not null
            ? "flaky-quarantined"
            : green ? "pass"
            : result.IsInfrastructureFailure ? "infrastructure-red"
            : result.FlakyClassification is not null ? "flaky-red"
            : "deterministic-suite-red";
        _store.RecordVerdict(new BatchGateRunVerdict(runId, manifest.BatchId,
            manifest.MembershipDigest, assembly.CandidateSha, scope.GateProfileDigest,
            green ? "pass" : "fail", classification, evidence, DateTimeOffset.UtcNow));
        if (!green)
        {
            _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                BatchPhase.Red, assembly.CandidateSha, lease.Fence,
                DateTimeOffset.UtcNow, classification));
            await HandleRedAsync(manifest, assembly, selected, repo, settings,
                run, classification, currentLease, ct).ConfigureAwait(false);
            return;
        }
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Green, assembly.CandidateSha, lease.Fence, DateTimeOffset.UtcNow));
        await PublishGreenAsync(manifest, assembly, run, selected, repo,
            currentLease, ct).ConfigureAwait(false);
    }

    private async Task PublishGreenAsync(BatchGateManifest manifest,
        BatchGateAssembly assembly, BatchGateRunRecord run,
        IReadOnlyList<BatchGatePendingDelivery> selected, string repo,
        Func<BatchCoordinatorLease> currentLease, CancellationToken ct)
    {
        var scope = manifest.Scope;
        var baseSha = manifest.BaseSha;
        var lease = currentLease();
        var evidence = run.EvidencePath;
        var runId = run.BatchRunId;
        using var refLease = await _refLeases.AcquireAsync(scope.Project, scope.Repository,
            scope.IntegrationBranch, ct).ConfigureAwait(false);
        var currentMembers = selected.ToDictionary(item => item.Subject.TaskKey,
            CurrentSubject, StringComparer.Ordinal);
        var currentTip = _git.GetRemoteIntegrationTip(repo, scope.IntegrationBranch, ct) ?? string.Empty;
        var decision = BatchGatePublicationPolicy.Decide(manifest, assembly, run,
            new BatchGateRunVerdict(runId, manifest.BatchId, manifest.MembershipDigest,
                assembly.CandidateSha, scope.GateProfileDigest, "pass", "pass", evidence, DateTimeOffset.UtcNow),
            currentMembers, currentTip, _coordinatorLeases.IsCurrent(currentLease()),
            refLease.IsCurrent, _git.IsAncestor(repo, baseSha, assembly.CandidateSha), false);
        if (decision != BatchPublishDecision.FastForward)
        {
            _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
                decision == BatchPublishDecision.StaleBase ? BatchPhase.Abandoned : BatchPhase.Paused,
                assembly.CandidateSha, lease.Fence, DateTimeOffset.UtcNow, decision.ToString()));
            if (decision is BatchPublishDecision.StaleBase or BatchPublishDecision.Superseded)
                foreach (var item in selected) _store.ReleaseClaim(item, manifest.BatchId);
            return;
        }
        var pushed = _git.PublishTestedBatchCandidate(repo, scope.IntegrationBranch,
            baseSha, assembly.CandidateSha, ct);
        if (!pushed.Success || !_coordinatorLeases.IsCurrent(currentLease()) || !refLease.IsCurrent)
            throw new IOException($"Batch publication was not verified: {pushed.Status} {pushed.Error}");
        _store.RecordPublication(new BatchGatePublication(manifest.BatchId,
            manifest.MembershipDigest, runId, baseSha, assembly.CandidateSha,
            assembly.CandidateSha, lease.Fence, refLease.Fence, DateTimeOffset.UtcNow));
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Published, assembly.CandidateSha, lease.Fence, DateTimeOffset.UtcNow));
        foreach (var key in assembly.AdmittedKeys)
            await ReleaseMemberAsync(selected.Single(item => item.Subject.TaskKey == key),
                manifest, run, repo, ct).ConfigureAwait(false);
    }

    private sealed record ProbeContext(BatchGateManifest Manifest,
        BatchGateAssembly Assembly, BatchGateRunRecord Run,
        BatchGateProbeResult Result);

    private async Task HandleRedAsync(BatchGateManifest manifest,
        BatchGateAssembly assembly, IReadOnlyList<BatchGatePendingDelivery> selected,
        string repo, ProjectSettings settings, BatchGateRunRecord firstRun,
        string classification, Func<BatchCoordinatorLease> currentLease,
        CancellationToken ct)
    {
        if (classification == "infrastructure-red")
        {
            if (!HostIsHealthy(repo))
            {
                Pause("infrastructure-red-host-unhealthy");
                return;
            }
            // One retry, with the same candidate SHA, after a fresh host health
            // check. The first and second run retain separate verdict files.
            var retried = await RunCandidateAsync(manifest, assembly, repo, settings,
                currentLease(), ct).ConfigureAwait(false);
            if (retried.Result.Classification == BatchGateFailureClass.Pass)
            {
                _store.AppendState(new BatchGateState(manifest.BatchId,
                    manifest.MembershipDigest, BatchPhase.Green, assembly.CandidateSha,
                    currentLease().Fence, DateTimeOffset.UtcNow, "infrastructure-retry-pass"));
                await PublishGreenAsync(manifest, assembly, retried.Run, selected,
                    repo, currentLease, ct).ConfigureAwait(false);
                return;
            }
            if (retried.Result.Classification == BatchGateFailureClass.DeterministicSuiteRed)
            {
                await HandleRedAsync(manifest, assembly, selected, repo, settings,
                    retried.Run, "deterministic-suite-red", currentLease, ct).ConfigureAwait(false);
                return;
            }
            if (retried.Result.Classification == BatchGateFailureClass.FlakyRed)
            {
                Pause("flaky-quarantine-requires-per-task-evidence");
                return;
            }
            Pause("infrastructure-red-retry-exhausted");
            return;
        }
        if (classification == "flaky-red")
        {
            Pause("flaky-quarantine-requires-per-task-evidence");
            return;
        }
        var admitted = assembly.AdmittedKeys
            .Select(key => manifest.Members.Single(member => member.TaskKey == key))
            .ToArray();
        var probes = new Dictionary<string, ProbeContext>(StringComparer.Ordinal);
        var isolated = await BatchGateIsolation.RunAsync(admitted, async (subset, token) =>
        {
            var probe = await ProbeSubsetAsync(manifest, subset, repo, settings,
                currentLease(), token).ConfigureAwait(false);
            probes[string.Join("|", subset.Select(member => member.TaskKey))] = probe;
            return probe.Result;
        }, settings.BatchGate.MaximumDiagnosticRuns, ct).ConfigureAwait(false);
        if (isolated.UnresolvedCohortKeys.Count > 0
            || isolated.SurvivorVerdict?.Classification != BatchGateFailureClass.Pass)
        {
            Pause("unresolved-cohort:" + string.Join(",", isolated.UnresolvedCohortKeys));
            foreach (var item in selected) _store.ReleaseClaim(item, manifest.BatchId);
            foreach (var item in selected)
                await FallBackAsync(item, ct).ConfigureAwait(false);
            return;
        }
        var survivors = isolated.SurvivorKeys.ToHashSet(StringComparer.Ordinal);
        var survivorProbe = probes[string.Join("|", isolated.SurvivorKeys)];
        foreach (var item in selected) _store.ReleaseClaim(item, manifest.BatchId);
        var survivorItems = selected.Where(item => survivors.Contains(item.Subject.TaskKey)).ToArray();
        foreach (var item in survivorItems) _store.Claim(item, survivorProbe.Manifest.BatchId);
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Abandoned, assembly.CandidateSha, currentLease().Fence,
            DateTimeOffset.UtcNow, "deterministic-members-isolated"));
        await PublishGreenAsync(survivorProbe.Manifest, survivorProbe.Assembly,
            survivorProbe.Run, survivorItems, repo, currentLease, ct).ConfigureAwait(false);
        foreach (var item in selected.Where(item => isolated.EjectedKeys.Contains(item.Subject.TaskKey)))
            await FallBackAsync(item, ct).ConfigureAwait(false);

        void Pause(string reason) => _store.AppendState(new BatchGateState(
            manifest.BatchId, manifest.MembershipDigest, BatchPhase.Paused,
            assembly.CandidateSha, currentLease().Fence, DateTimeOffset.UtcNow, reason));
    }

    private async Task<ProbeContext> ProbeSubsetAsync(BatchGateManifest original,
        IReadOnlyList<BatchGateSubject> subset, string repo, ProjectSettings settings,
        BatchCoordinatorLease lease, CancellationToken ct)
    {
        var members = subset.ToArray();
        var manifest = new BatchGateManifest(Guid.NewGuid().ToString("N"),
            original.Scope, original.BaseSha,
            BatchGatePolicy.MembershipDigest(original.BaseSha, original.Scope, members),
            DateTimeOffset.UtcNow, members,
            original.Members.Where(member => members.All(item => item.TaskKey != member.TaskKey))
                .Select(member => new BatchGateExclusion(member.TaskKey, BatchExclusionReason.NextBatch))
                .ToArray(), original.EligibleKeys, IsDiagnostic: true);
        _store.CloseManifest(manifest);
        var assembly = new BatchGateAssembler(_store, _git, repo)
            .Assemble(manifest.BatchId, lease.Fence);
        if (assembly.AdmittedKeys.Count != members.Length || assembly.EjectedKeys.Count > 0)
            return new ProbeContext(manifest, assembly, null!,
                new BatchGateProbeResult(BatchGateFailureClass.InfrastructureRed,
                    assembly.CandidateSha, "mechanical-replay-unresolved"));
        return await RunCandidateAsync(manifest, assembly, repo, settings,
            lease, ct).ConfigureAwait(false);
    }

    private async Task<ProbeContext> RunCandidateAsync(BatchGateManifest manifest,
        BatchGateAssembly assembly, string repo, ProjectSettings settings,
        BatchCoordinatorLease lease, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        var evidence = Path.Combine(_store.BatchDirectory(manifest.BatchId),
            "runs", runId + ".evidence.json");
        var commands = VerifyCommandPlanner.Plan(repo, settings.BuildProfile).Commands
            .Select(command => command.Command).ToArray();
        var run = new BatchGateRunRecord(runId, manifest.BatchId,
            manifest.MembershipDigest, manifest.BaseSha, assembly.CandidateSha,
            manifest.Scope.GateProfile, manifest.Scope.GateProfileDigest,
            Environment.MachineName, lease.Fence, commands,
            DateTimeOffset.UtcNow, evidence);
        _store.RecordRun(run);
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Running, assembly.CandidateSha, lease.Fence, DateTimeOffset.UtcNow));
        var result = await _gate.RunAsync(new BuildTestGateRequest(repo,
                assembly.CandidateSha, "batch-gate-diagnostic")
            {
                GateId = "batch-gate",
                Project = manifest.Scope.Project,
                SubjectRef = assembly.CandidateRef,
                TestExecution = settings.TestExecution,
                ForceFullSuite = true,
                BypassVerdictCache = true,
            }, null, settings.BuildProfile, PostStepMode.Fail,
            TimeSpan.FromSeconds(GateRunBudgetPolicy.ResolveSeconds(settings.BuildTestGateTimeoutSeconds)),
            ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(result), ct).ConfigureAwait(false);
        var pass = result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn
            && string.Equals(result.TestedSha, assembly.CandidateSha, StringComparison.OrdinalIgnoreCase);
        var classification = pass ? BatchGateFailureClass.Pass
            : result.IsInfrastructureFailure ? BatchGateFailureClass.InfrastructureRed
            : result.FlakyClassification is not null ? BatchGateFailureClass.FlakyRed
            : BatchGateFailureClass.DeterministicSuiteRed;
        _store.RecordVerdict(new BatchGateRunVerdict(runId, manifest.BatchId,
            manifest.MembershipDigest, assembly.CandidateSha,
            manifest.Scope.GateProfileDigest, pass ? "pass" : "fail",
            pass && result.FlakyClassification is not null
                ? "flaky-quarantined" : classification.ToString(),
            evidence, DateTimeOffset.UtcNow));
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            pass ? BatchPhase.Green : BatchPhase.Red,
            assembly.CandidateSha, lease.Fence, DateTimeOffset.UtcNow,
            classification.ToString()));
        return new ProbeContext(manifest, assembly, run,
            new BatchGateProbeResult(classification, assembly.CandidateSha, evidence));
    }

    private static bool HostIsHealthy(string repo)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(repo));
            return root is not null && new DriveInfo(root).AvailableFreeSpace > 1_000_000_000;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool BatchHostLoadPressure()
    {
        try
        {
            var load = File.ReadAllText("/proc/loadavg").Split(' ')[0];
            return double.TryParse(load,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var value)
                   && value >= Math.Max(2, Environment.ProcessorCount * .9);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task ReleaseMemberAsync(BatchGatePendingDelivery item,
        BatchGateManifest manifest, BatchGateRunRecord run, string repo, CancellationToken ct)
    {
        var current = CurrentSubject(item);
        if (!current.CurrentGeneration) return;
        var replay = _store.ReadReplay(manifest.BatchId, item.Subject.TaskKey);
        var replacementShas = replay.Replacements.Select(x => x.RebasedSha).ToArray();
        var member = new BatchGateMemberRecord(item.Subject.TaskKey,
            item.Subject.RunAttempt, item.Subject.DeliveryEpoch,
            item.Subject.ResultSha, replacementShas, manifest.BatchId,
            manifest.MembershipDigest, manifest.BaseSha, run.CandidateSha,
            run.GateProfileDigest, run.BatchRunId, "pass", run.EvidencePath,
            DateTimeOffset.UtcNow);
        _store.RecordMember(member);
        if (!_store.CanRelease(current, manifest.BatchId, run.BatchRunId))
            throw new InvalidDataException("batch-gate-evidence-missing");
        if (!_git.IsAncestor(repo, replay.TipAfterSha!, run.CandidateSha)
            || !_git.RemoteIntegrationContainsCandidate(repo,
                manifest.Scope.IntegrationBranch, run.CandidateSha, ct))
            throw new InvalidDataException("integration-unverified");
        var task = V1ReviewPlaneEndpoints.FindTask(_scanner, item.Subject.TaskKey);
        if (task is null || task.State != TaskStates.AutoReview) return;
        var rewritten = replay.Replacements.Where(replacement =>
            !string.Equals(replacement.OriginalSha, replacement.RebasedSha,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rewritten.Length > 0
            && !_mutations.RecordMechanicalRebaseOnFolder(task.FolderPath, rewritten))
            throw new InvalidDataException("batch-gate-evidence-missing: SHA mapping could not be attached to the task.");
        var recorded = _mutations.AppendIntegrationRecordOnFolder(task.FolderPath,
            new TaskIntegrationRecord
            {
                Id = "batch-gate:" + manifest.BatchId + ":" + item.Subject.TaskKey,
                Classification = IntegrationRecordClasses.IntegratedVerified,
                RecordedAtUtc = DateTime.UtcNow,
                IntegrationBranch = manifest.Scope.IntegrationBranch,
                IntegrationTipSha = run.CandidateSha,
                CommitShas = replacementShas.ToList(),
                FenceRefs = [BatchGatePolicy.CandidateRef(manifest, run.CoordinatorFence)],
                Evidence = $"batch-run {run.BatchRunId}; tested-candidate {run.CandidateSha}; {run.EvidencePath}",
            });
        if (!recorded.Succeeded) throw new InvalidDataException("integration-unverified");
        var pipelineAttempt = _pipelineLog?.ReadForMigration(task.FolderPath)?.Attempt;
        if (pipelineAttempt is { } attempt)
        {
            _pipelineLog!.RecordStep(task.FolderPath, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.BuildTestGateStepId,
                Kind = StepKind.Tool,
                Attempt = attempt,
                Status = PipelineStepStatus.Passed,
                StartedAt = run.StartedAtUtc.UtcDateTime,
                CompletedAt = DateTime.UtcNow,
                DurationMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - run.StartedAtUtc).TotalMilliseconds),
                Reason = $"Batch {manifest.BatchId} passed on exact candidate {run.CandidateSha}.",
                ExecutionLocation = "local",
                ExecutionHostId = run.Host,
                ExecutionAttemptId = run.BatchRunId,
                GateOriginEvidencePath = run.EvidencePath,
            });
            _pipelineLog.RecordStep(task.FolderPath, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.MergeIntoDevelopStepId,
                Kind = StepKind.Tool,
                Attempt = attempt,
                Status = PipelineStepStatus.Passed,
                StartedAt = run.StartedAtUtc.UtcDateTime,
                CompletedAt = DateTime.UtcNow,
                Reason = $"Verified remote {manifest.Scope.IntegrationBranch} at {run.CandidateSha}.",
                ExecutionLocation = "local",
                ExecutionHostId = run.Host,
                ExecutionAttemptId = run.BatchRunId,
            });
            _pipelineLog.RecordStep(task.FolderPath, new PipelineStepExecution
            {
                StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
                Kind = StepKind.Tool,
                Attempt = attempt,
                Status = PipelineStepStatus.Passed,
                StartedAt = run.StartedAtUtc.UtcDateTime,
                CompletedAt = DateTime.UtcNow,
                Reason = $"Remote {manifest.Scope.IntegrationBranch} resolves to tested candidate {run.CandidateSha}.",
                ExecutionLocation = "local",
                ExecutionHostId = run.Host,
                ExecutionAttemptId = run.BatchRunId,
            });
        }
        if (!_store.CanRelease(CurrentSubject(item), manifest.BatchId, run.BatchRunId))
            throw new InvalidDataException("batch-gate-evidence-missing");
        var moved = await _transitions.MoveAsync(task.Id, TaskStates.HumanReview,
            task.WatchPath, ct, cause: "batch-gate:" + manifest.BatchId,
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: "batch-gate-passed").ConfigureAwait(false);
        if (moved.Status != MoveJobStatus.Success)
            throw new IOException($"Batch member lane release failed: {moved.Status} {moved.Message}");
        _escalation.RecordRemoteReviewParkVerdict(task.ProjectName, task.Id,
            moved.NewFolderPath ?? task.FolderPath, "Pass",
            "The documentation batch gate passed on the verified integration candidate.",
            null);
        _store.CompletePending(item, "released");
    }

    private BatchGateSubject CurrentSubject(BatchGatePendingDelivery item)
    {
        var projection = _authority.GetTaskProjection(item.Subject.TaskKey);
        var task = V1ReviewPlaneEndpoints.FindTask(_scanner, item.Subject.TaskKey);
        var current = projection.CurrentRunAttempt;
        var review = projection.CurrentReviewAttempt;
        var settings = _settings.Get(item.Subject.Project);
        var liveScope = task is null ? BatchGatePolicy.ScopeOf(item.Subject)
            : Scope(task, current?.RepositoryId ?? item.Subject.Repository, settings);
        return item.Subject with
        {
            CurrentGeneration = IsCurrentGeneration(item, task?.State, current, review),
            Repository = liveScope.Repository,
            IntegrationBranch = liveScope.IntegrationBranch,
            GateProfile = liveScope.GateProfile,
            GateProfileDigest = liveScope.GateProfileDigest,
            PlatformVersion = liveScope.PlatformVersion,
            ActivePerTaskGate = task is not null
                && IntegrationGateJournal.Read(task.FolderPath) is not null,
            OwningBatchId = _store.PendingOwner(item),
        };
    }

    internal static bool IsCurrentGeneration(BatchGatePendingDelivery item,
        string? taskState, RunAttemptDto? run, ReviewAttemptDto? review)
        => taskState == TaskStates.AutoReview
           && run is { State: AttemptLifecycleState.Completed, ResultEnvelope: not null,
               ResultEnvelopeDigest: not null }
           && run.AttemptId == item.Subject.RunAttempt
           && run.RepositoryId == item.Subject.Repository
           && run.AuthorityEpoch == item.Subject.DeliveryEpoch
           && run.LastFence == item.Subject.FencingToken
           && string.Equals(run.ResultSha, item.Subject.ResultSha, StringComparison.OrdinalIgnoreCase)
           && string.Equals(run.ResultEnvelope.ImmutableRemoteRef, item.Subject.ResultRef,
               StringComparison.Ordinal)
           && string.Equals(AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(
                   run.ResultEnvelope), run.ResultEnvelopeDigest, StringComparison.OrdinalIgnoreCase)
           && review is { State: AttemptLifecycleState.Completed,
               Outcome: ReviewTerminalOutcome.Pass }
           && review.AttemptId == item.ReviewAttemptId
           && review.RepositoryId == item.Subject.Repository
           && review.SourceRunAttemptId == run.AttemptId
           && review.Subject.Plan?.BuildTestDeferredToBatch == true
           && string.Equals(review.TestedResultSha, item.Subject.ResultSha,
               StringComparison.OrdinalIgnoreCase);

    private async Task FallBackAsync(BatchGatePendingDelivery item, CancellationToken ct)
    {
        if (!CurrentSubject(item).CurrentGeneration)
        {
            _store.CompletePending(item, "superseded");
            return;
        }
        var task = V1ReviewPlaneEndpoints.FindTask(_scanner, item.Subject.TaskKey);
        if (task is null) return;
        var delivery = DeliveryRefResolver.Resolve(task.Id, task.FolderPath);
        var frozenRef = item.Subject.ResultRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? item.Subject.ResultRef["refs/heads/".Length..]
            : item.Subject.ResultRef;
        if (!string.Equals(delivery.Ref, frozenRef, StringComparison.Ordinal)
            || !string.Equals(delivery.ExpectedResultSha, item.Subject.ResultSha,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The per-task fallback no longer names the immutable batch subject.");
        var settings = _settings.Get(task.ProjectName);
        var request = new RemoteDeliveryIntegrationRequest(task.ProjectName, task.Id,
            task.FolderPath, task.WatchPath, item.Subject.IntegrationBranch,
            settings.IntegrationStrategy, PipelineTypes.Resolve(task), item.QueuedAtUtc);
        var result = await _perTask.EnqueueAsync(request).ConfigureAwait(false);
        if (!result.Outcome.IsSuccessfulIntegration())
        {
            _store.CompletePending(item, "per-task-gate-failed");
            return;
        }
        var moved = await _transitions.MoveAsync(task.Id, TaskStates.HumanReview,
            task.WatchPath, ct, cause: "batch-gate-disabled-fallback",
            suppressProductExecution: true, expectedSourceState: TaskStates.AutoReview,
            transitionCause: LaneChangeCauses.ReviewVerdict,
            transitionDetail: result.Outcome.ToString()).ConfigureAwait(false);
        if (moved.Status == MoveJobStatus.Success)
        {
            _escalation.RecordRemoteReviewParkVerdict(task.ProjectName, task.Id,
                moved.NewFolderPath ?? task.FolderPath, "Pass",
                "The per-task integration gate passed after batch routing was disabled.",
                null);
            _store.CompletePending(item, "per-task-fallback");
        }
    }

    private static BatchGateScope Scope(TaskInfo task, string repository, ProjectSettings settings)
        => Scope(task.ProjectName, repository,
            TaskIntegrationBranch.Resolve(task, settings.IntegrationBranch), settings);

    private static BatchGateScope Scope(string project, string repository,
        string branch, ProjectSettings settings)
    {
        var profile = "full-suite-v1";
        var digest = AttemptAuthorityService.Hash(JsonSerializer.Serialize(new
        {
            profile,
            Build = BuildProfileValidationFingerprint.Create(settings.BuildProfile),
            settings.TestExecution,
            Commands = settings.BuildProfile?.BuildCmds,
            Tests = settings.BuildProfile?.TestCmds,
            PipelineCatalogue.Standard.Version,
        }));
        var version = typeof(BatchGateRuntime).Assembly.GetName().Version?.ToString() ?? "unknown";
        return new BatchGateScope(project, repository, branch, profile, digest, version);
    }
}

public sealed class BatchGateWorker : BackgroundService
{
    private readonly BatchGateRuntime _runtime;
    private readonly ILogger<BatchGateWorker> _logger;

    public BatchGateWorker(BatchGateRuntime runtime, ILogger<BatchGateWorker> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await _runtime.RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Batch gate cycle failed; durable queue remains pending."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
