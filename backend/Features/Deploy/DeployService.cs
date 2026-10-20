using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <summary><c>Invalid</c> is a request the input decides (400); <c>Conflict</c> one the stored state decides (409).</summary>
public enum DeployOutcomeKind { Ok, NotFound, Conflict, Forbidden, Invalid }

/// <param name="Reason">A machine-readable refusal reason, such as <see cref="DeployRefusalReasons.Superseded"/>.</param>
public sealed record DeployOutcome<T>(DeployOutcomeKind Kind, T? Value, string? Error, string? Reason = null)
{
    public static DeployOutcome<T> Ok(T value) => new(DeployOutcomeKind.Ok, value, null);
    public static DeployOutcome<T> NotFound(string error) => new(DeployOutcomeKind.NotFound, default, error);
    public static DeployOutcome<T> Conflict(string error, string? reason = null)
        => new(DeployOutcomeKind.Conflict, default, error, reason);
    public static DeployOutcome<T> Forbidden(string error) => new(DeployOutcomeKind.Forbidden, default, error);
    public static DeployOutcome<T> Invalid(string error) => new(DeployOutcomeKind.Invalid, default, error);
}

/// <summary>
/// BD-1 releases, standard lifecycle and deploy runs on top of the AGT-3176
/// environment declarations and host deploy queue. Every mutation reads the
/// facts, asks <see cref="StandardLifecyclePolicy"/>, then writes log and
/// audit before the run list, so an interrupted write repeats an entry rather
/// than hiding a state change.
/// </summary>
public sealed class DeployService
{
    private const int FirstReleaseCommitWindow = 200;
    private const int ReleaseCommitWindow = 5000;
    private const int AuditLimit = 500;
    private readonly object _gate = new();
    private readonly ProjectEnvironmentService _environments;
    private readonly DeployStore _store;
    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeployService> _logger;

    public DeployService(ProjectEnvironmentService environments, DeployStore store, GitService git,
        TaskScannerService scanner, TimeProvider clock, ILogger<DeployService> logger)
    {
        _environments = environments;
        _store = store;
        _git = git;
        _scanner = scanner;
        _clock = clock;
        _logger = logger;
    }

    public static IReadOnlyList<DeployEnvironment> MapEnvironments(string project,
        IEnumerable<EnvironmentDeclaration> declarations)
        => declarations.Where(item => item.Kind is "stage" or "production")
            .Select((item, index) => new DeployEnvironment(item.Id, project, item.Name, index,
                item.Kind == "stage" ? EnvironmentKinds.Staging : EnvironmentKinds.Production))
            .ToArray();

    public IReadOnlyList<DeployEnvironment>? Environments(string project)
        => _environments.Declarations(project) is { } declarations ? MapEnvironments(project, declarations) : null;

    /// <summary>
    /// The integration branch built green: record the immutable release for
    /// the gate artifact, with the environment declarations as committed at
    /// that commit, then start the automatic Staging phase from that snapshot.
    /// Releases never move back in history: a commit that is a strict ancestor
    /// of the newest release's commit is refused, and an existing release
    /// starts Staging again only while it is the newest, so creation order is
    /// commit order for every release the lifecycle compares. Git reads and
    /// the archive-inclusive card scan run before the global lock; the
    /// ancestry check repeats under the lock only when another release was
    /// created meanwhile.
    /// </summary>
    public DeployOutcome<Release> CreateRelease(string project, string artifact, string commit, string actor,
        string? channelOverride = null, bool approver = false, string? branch = null, string? versionTag = null)
    {
        const string invalid = "Artifact, commit, or stage and production declarations are invalid.";
        if (_environments.Declarations(project) is null) return DeployOutcome<Release>.NotFound($"Unknown project '{project}'.");
        if (channelOverride is not null && !approver)
            return DeployOutcome<Release>.Forbidden("Channel override requires the Owner or an approver role.");
        if (!_environments.CanQueueRelease(project, artifact, commit)) return DeployOutcome<Release>.Invalid(invalid);
        IReadOnlyList<EnvironmentDeclaration>? declarations;
        try
        {
            declarations = _environments.DeclarationsAtCommit(project, commit);
        }
        catch (JsonException ex)
        {
            return DeployOutcome<Release>.Invalid(
                $"The environment declarations at commit {ShortSha(commit)} are invalid: {ex.Message}");
        }
        if (declarations is null)
            return DeployOutcome<Release>.Conflict(
                $"The environment declarations at commit {ShortSha(commit)} could not be read completely.");
        var environments = MapEnvironments(project, declarations);
        var staging = environments.FirstOrDefault(StandardLifecyclePolicy.IsAutomatic);
        if (staging is null || !environments.Any(StandardLifecyclePolicy.RequiresApproval))
            return DeployOutcome<Release>.Invalid(
                $"Staging and production declarations are required at commit {ShortSha(commit)}.");
        var id = ReleaseSnapshot.Id(project, commit, [artifact]);
        var repo = _git.ResolveRepoRootForProject(project);
        var suppliedBranch = branch;
        branch ??= repo is null ? null : _git.ResolveIntegrationBranch(repo, null);
        var existing = _store.ReadReleaseRecords(project);
        var known = existing.Any(item => item.Release.Id == id);
        var newestSeen = existing.LastOrDefault()?.Release;
        if (OrderRejection(repo, newestSeen, id, commit, known) is { } orderRejection)
            return DeployOutcome<Release>.Conflict(orderRejection.Error, orderRejection.Reason);
        var integrated = known || repo is null ? [] : IntegratedCards(project);
        var definitionRead = known || repo is null ? null : ReadDefinition(repo, commit);
        if (definitionRead is { IsValid: false } && definitionRead.Issues.FirstOrDefault(issue =>
            issue.Path.StartsWith("channels", StringComparison.Ordinal) || issue.Path.EndsWith(".channels", StringComparison.Ordinal)) is { } issue)
            return DeployOutcome<Release>.Invalid($"Channel rule at commit {ShortSha(commit)} did not match: {issue.Message}");
        var definition = definitionRead?.Definition?.Deployment;

        if (suppliedBranch is not null && (repo is null || _git.GetBranchTip(repo, suppliedBranch) != commit))
            return DeployOutcome<Release>.Invalid($"Branch rule '{branch}' did not match commit {ShortSha(commit)} at the branch tip.");

        lock (_gate)
        {
            var current = _store.ReadReleaseRecords(project);
            var newest = current.LastOrDefault()?.Release;
            if (newest?.Id != newestSeen?.Id && OrderRejection(repo, newest, id, commit,
                    current.Any(item => item.Release.Id == id)) is { } raced)
                return DeployOutcome<Release>.Conflict(raced.Error, raced.Reason);
            var now = _clock.GetUtcNow().UtcDateTime;
            var version = ReleaseSnapshot.NextVersion(current.Select(item => item.Release), now);
            var existingRelease = current.FirstOrDefault(item => item.Release.Id == id);
            if (existingRelease is not null && channelOverride is not null && existingRelease.Release.Channel != channelOverride)
                return DeployOutcome<Release>.Conflict(
                    $"Release {existingRelease.Release.Version} belongs to channel '{existingRelease.Release.Channel}'; its channel cannot change.");
            (ProjectChannelDefinition? Channel, string? Refusal) selection = existingRelease is null
                ? ProjectChannelPolicy.Select(definition, versionTag ?? version, branch, channelOverride, approver)
                : (Channel: new ProjectChannelDefinition(existingRelease.Release.Channel, "standard", null, null, true),
                    Refusal: (string?)null);
            if (selection.Refusal is not null) return DeployOutcome<Release>.Invalid(selection.Refusal);
            var (record, created) = FindOrCreateRelease(project, id, commit, [artifact], declarations, definition,
                repo, integrated, now, selection.Channel!);
            var release = record.Release;
            var audit = new List<DeployAuditEntry>();
            if (created)
                audit.Add(new(now, actor, "release-created", release.Id, null, null,
                    $"Release {release.Version} for commit {ShortSha(commit)} with {release.CardKeys.Count} card(s)."));
            var firstPhase = record.Lifecycle.FirstOrDefault();
            if (firstPhase is not null && (!firstPhase.Automatic || firstPhase.Phase != staging.Kind))
            {
                Persist(project, _store.ReadDeployments(project), [], audit);
                return DeployOutcome<Release>.Ok(release);
            }
            var job = _environments.QueueReleaseJob(project, artifact, commit, record.Environments);
            if (job is null)
            {
                Persist(project, _store.ReadDeployments(project), [], audit);
                return DeployOutcome<Release>.Invalid(invalid);
            }
            var runs = _store.ReadDeployments(project).ToList();
            var run = NewRun(release, staging, record.Environments, DeploymentStates.Queued, DeploymentTriggers.Deploy,
                actor, null, job, now);
            runs.Add(run);
            var log = new List<DeploymentLogEntry>
            {
                new(run.Id, run.DeployStep, now,
                    $"Release {release.Version} queued for {staging.Name} by {actor} (automatic after integration)."),
            };
            if (HostChange(record, staging.Id, job) is { } moved) log.Add(new(run.Id, run.DeployStep, now, moved));
            audit.Add(new(now, actor, "deployment-queued", release.Id, run.Id, staging.Id,
                $"Release {release.Version} queued for {staging.Name}."));
            Persist(project, runs, log, audit);
            SyncLocked(project);
            return DeployOutcome<Release>.Ok(release);
        }
    }

    /// <summary>
    /// Why a release of <paramref name="commit"/> may not start Staging after
    /// <paramref name="newest"/>, or null. A rebuild of the newest commit and
    /// a commit unrelated in history stay allowed. The check fails closed: when
    /// git cannot answer the ancestry, the release is refused. The two
    /// "newest release" refusals carry <see cref="DeployRefusalReasons.Superseded"/>,
    /// which scripts/release/promote-develop-to-main.sh logs and continues on.
    /// </summary>
    private (string Error, string? Reason)? OrderRejection(string? repo, Release? newest, string id, string commit,
        bool known)
    {
        if (newest is null || newest.Id == id) return null;
        if (known)
            return ($"The release of commit {ShortSha(commit)} is older than the newest release {newest.Version}. "
                    + "Staging runs only the newest release; use rollback to move back.", DeployRefusalReasons.Superseded);
        if (newest.CommitSha == commit || repo is null) return null;
        return _git.TryIsAncestor(repo, commit, newest.CommitSha) switch
        {
            false => null,
            true => ($"Commit {ShortSha(commit)} is an ancestor of commit {ShortSha(newest.CommitSha)} of the newest "
                     + $"release {newest.Version}. A release never moves back in history; use rollback.",
                DeployRefusalReasons.Superseded),
            null => ($"Git could not compare commit {ShortSha(commit)} with commit {ShortSha(newest.CommitSha)} "
                     + $"of release {newest.Version}. Retry once the repository is readable.", null),
        };
    }

    public IReadOnlyList<Release>? Releases(string project)
    {
        lock (_gate)
        {
            return SyncLocked(project)?.Releases.OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public DeployPage<Release>? ReleasePage(string project, int offset, int limit)
    {
        lock (_gate)
        {
            var releases = SyncLocked(project)?.Releases.OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
            return releases is null ? null : Page(releases, offset, limit);
        }
    }

    public ReleaseDetailResponse? ReleaseDetail(string project, string releaseId)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            var release = snapshot?.Releases.FirstOrDefault(item => item.Id == releaseId);
            return release is null ? null : new(release, snapshot!.Deployments(project)
                .Where(item => item.ReleaseId == releaseId).ToArray());
        }
    }

    public IReadOnlyList<Deployment>? Deployments(string project, string? environmentId)
    {
        lock (_gate)
        {
            return SyncLocked(project)?.Deployments(project)
                .Where(item => environmentId is null || item.EnvironmentId == environmentId)
                .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public DeployPage<Deployment>? DeploymentPage(string project, string? environmentId, string? state,
        DateTime? sinceUtc, int offset, int limit)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return null;
            var runs = snapshot.Deployments(project)
                .Where(item => environmentId is null || item.EnvironmentId == environmentId)
                .Where(item => state is null || item.State == state)
                .Where(item => sinceUtc is null || item.CreatedAtUtc >= sinceUtc)
                .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            return Page(runs, offset, limit);
        }
    }

    private static DeployPage<T> Page<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 50);
        return new(items.Skip(safeOffset).Take(safeLimit).ToArray(), safeOffset, safeLimit, items.Count);
    }

    public Deployment? DeploymentById(string project, string deploymentId)
    {
        lock (_gate)
        {
            return SyncLocked(project)?.Deployments(project).FirstOrDefault(item => item.Id == deploymentId);
        }
    }

    public DeploymentLogResponse? Log(string project, string deploymentId, string? step)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null || snapshot.Runs.All(item => item.Id != deploymentId)) return null;
            return new(deploymentId, _store.ReadLog(project, deploymentId)
                .Where(item => step is null || item.Step == step).ToArray());
        }
    }

    public IReadOnlyList<DeployAuditEntry>? Audit(string project)
    {
        lock (_gate)
        {
            return SyncLocked(project) is null ? null : _store.ReadAudit(project, AuditLimit);
        }
    }

    public IReadOnlyList<EnvironmentStatus>? Status(string project)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return null;
            var hostHealth = snapshot.View.Environments.ToDictionary(item => item.Declaration.Id, item => item.Health,
                StringComparer.Ordinal);
            return EnvironmentStatusProjection.Project(snapshot.Environments, snapshot.Releases,
                snapshot.Deployments(project), hostHealth);
        }
    }

    public DeployMatrixResponse Matrix()
    {
        var rows = new List<DeployMatrixRow>();
        foreach (var project in _scanner.GetWatchPaths().Select(item => item.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Reading the queue persists its state, so skip projects
                // without environment declarations.
                if (_environments.Declarations(project) is not { Count: > 0 }) continue;
                var statuses = Status(project);
                if (statuses is { Count: > 0 }) rows.Add(new(project, statuses, null));
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Deploy matrix skipped project {Project}: invalid deployment state", project);
                rows.Add(new(project, [], ex.Message));
            }
        }
        return new([EnvironmentKinds.Staging, EnvironmentKinds.Production], rows);
    }

    /// <summary>
    /// Folds a host claim or report into the runs. The host call has already
    /// succeeded, so a failure here must not fail it; the next read repeats
    /// the idempotent reconciliation.
    /// </summary>
    public void SyncAfterHostCall(string project)
    {
        try
        {
            lock (_gate)
            {
                SyncLocked(project);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Deploy runs for {Project} were not reconciled after a host call", project);
        }
    }

    public DeployOutcome<Deployment> RequestDeployment(string project, string releaseId, DeploymentRequest request,
        DeployActor actor)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return DeployOutcome<Deployment>.NotFound($"Unknown project '{project}'.");
            if (!snapshot.Records.TryGetValue(releaseId, out var record))
                return DeployOutcome<Deployment>.NotFound($"Unknown release '{releaseId}'.");
            var environment = snapshot.Environments.FirstOrDefault(item => item.Id == request.EnvironmentId);
            if (environment is null)
                return DeployOutcome<Deployment>.NotFound($"Unknown environment '{request.EnvironmentId}'.");
            return Start(project, snapshot, record, environment, DeployIntent.Deploy, request.Approve, actor);
        }
    }

    public DeployOutcome<Deployment> Redeploy(string project, string environmentId, bool approve, DeployActor actor)
        => StartForEnvironment(project, environmentId, DeployIntent.Redeploy, approve, actor);

    public DeployOutcome<Deployment> Rollback(string project, string environmentId, bool approve, DeployActor actor)
        => StartForEnvironment(project, environmentId, DeployIntent.Rollback, approve, actor);

    /// <summary>
    /// Approves a waiting run. The lifecycle decides again, because another
    /// run may have reached the environment since the request: the approval is
    /// refused while another run is queued or running there, and a deploy that
    /// is no longer newer than the current release needs a rollback instead.
    /// Queuing the run closes the waiting runs it makes invalid.
    /// </summary>
    public DeployOutcome<Deployment> Approve(string project, string deploymentId, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out var snapshot, out var run, out var record);
            if (found is not null) return found;
            var rejection = StandardLifecyclePolicy.ApprovalRejection(run!.State, actor);
            if (rejection is not null) return Refuse(run.State, rejection);
            var release = record!.Release;
            var environment = snapshot!.Environments.FirstOrDefault(item => item.Id == run.EnvironmentId);
            if (environment is null)
                return DeployOutcome<Deployment>.Conflict($"Environment '{run.EnvironmentId}' is no longer declared.");
            var target = record.Environments.FirstOrDefault(item => item.Id == environment.Id);
            if (target is null) return MissingSnapshot(release, environment);
            var intent = StandardLifecyclePolicy.IntentFor(run.Trigger);
            var decision = StandardLifecyclePolicy.Decide(Facts(project, snapshot, record, environment, intent,
                approve: true, actor, excludeRunId: run.Id));
            if (decision.Outcome == LifecycleOutcome.Reject) return Rejected(decision);

            var now = _clock.GetUtcNow().UtcDateTime;
            var job = QueueJob(project, snapshot, run.Trigger, target, release, actor.Id);
            if (job is null) return DeployOutcome<Deployment>.Conflict("The deploy job could not be queued.");
            var approved = run with
            {
                State = DeploymentStates.Queued, ApprovedBy = actor.Id, ApprovedAtUtc = now, JobId = job.Id,
            };
            var runs = snapshot.Runs.Select(item => item.Id == run.Id ? approved : item).ToList();
            var log = new List<DeploymentLogEntry>
            {
                new(run.Id, DeployStepNames.Approval, now, $"Approved by {actor.Id}."),
                new(run.Id, run.DeployStep, now, $"Queued for deploy host {job.HostId}."),
            };
            if (HostChange(record, environment.Id, job) is { } moved) log.Add(new(run.Id, run.DeployStep, now, moved));
            var audit = new List<DeployAuditEntry>
            {
                new(now, actor.Id, "deployment-approved", release.Id, run.Id, run.EnvironmentId,
                    $"Release {release.Version} approved for {environment.Name}."),
            };
            Supersede(project, snapshot, runs, approved, intent, release, actor, log, audit, now);
            Persist(project, runs, log, audit);
            return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
        }
    }

    public DeployOutcome<Deployment> Reject(string project, string deploymentId, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out var snapshot, out var run, out var record);
            if (found is not null) return found;
            var rejection = StandardLifecyclePolicy.ApprovalRejection(run!.State, actor);
            return rejection is not null
                ? Refuse(run.State, rejection)
                : Close(project, run, actor, rejected: true, ClosesPromotion(snapshot!, run, record!.Release));
        }
    }

    /// <summary>
    /// Cancels a queued or waiting run, or aborts a running one. The host's
    /// lease is revoked on abort, so its late report is refused.
    /// </summary>
    public DeployOutcome<Deployment> Cancel(string project, string deploymentId, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out var snapshot, out var run, out var record);
            if (found is not null) return found;
            var environment = snapshot!.Environments.FirstOrDefault(item => item.Id == run!.EnvironmentId)
                ?? MapEnvironments(project, record!.Environments).FirstOrDefault(item => item.Id == run!.EnvironmentId);
            if (environment is null)
                return DeployOutcome<Deployment>.Conflict($"Environment '{run!.EnvironmentId}' is no longer declared.");
            var closesPromotion = ClosesPromotion(snapshot, run!, record!.Release);
            var rejection = StandardLifecyclePolicy.CancelRejection(run!.State, environment, actor, closesPromotion);
            if (rejection is not null)
                return DeploymentStates.IsFinished(run.State)
                    ? DeployOutcome<Deployment>.Conflict(rejection) : DeployOutcome<Deployment>.Forbidden(rejection);
            return Close(project, run, actor, rejected: false, closesPromotion);
        }
    }

    /// <summary>
    /// The AGT-3176 promotion routes answer through the lifecycle: they
    /// approve or reject the waiting run of the release that carries the
    /// artifact. Only an artifact from before releases existed keeps the
    /// direct promotion decision, and only while production is idle.
    /// </summary>
    public DeployOutcome<Deployment?> DecidePromotion(string project, string artifact, bool approve, DeployActor actor)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return DeployOutcome<Deployment?>.NotFound($"Unknown project '{project}'.");
            var promotion = snapshot.View.Promotions.LastOrDefault(item => item.Artifact == artifact && item.Status == "pending");
            if (promotion is null) return DeployOutcome<Deployment?>.Conflict("Promotion is not pending.");
            var run = snapshot.Runs.LastOrDefault(item => item.EnvironmentId == promotion.ProductionId
                && item.State == DeploymentStates.AwaitingApproval && item.Trigger == DeploymentTriggers.Deploy
                && snapshot.Records.TryGetValue(item.ReleaseId, out var record)
                && record.Release.ArtifactDigests.Contains(artifact, StringComparer.Ordinal));
            if (run is not null)
            {
                var decided = approve ? Approve(project, run.Id, actor) : Reject(project, run.Id, actor);
                return new(decided.Kind, decided.Value, decided.Error);
            }
            if (snapshot.Releases.Any(item => item.ArtifactDigests.Contains(artifact, StringComparer.Ordinal)))
                return DeployOutcome<Deployment?>.Conflict("The release of this artifact is not awaiting approval.");
            if (approve && HostBusy(snapshot, promotion.ProductionId))
                return DeployOutcome<Deployment?>.Conflict("Wait until the active deployment in production has finished.");
            if (!_environments.Decide(project, artifact, approve, actor.Id))
                return DeployOutcome<Deployment?>.Conflict("Promotion is not pending.");
            SyncLocked(project);
            return DeployOutcome<Deployment?>.Ok(null);
        }
    }

    /// <summary>
    /// The AGT-3176 rollback route: a rollback run through the lifecycle when
    /// the environment has a release to roll back to, the artifact history
    /// otherwise. The history covers only the environment's first release,
    /// whose predecessor is an artifact from before releases existed: a
    /// history target that belongs to any release is refused, so the route
    /// never redeploys a release (for example the one a lifecycle rollback
    /// just left) without the lifecycle decision. That rollback has no
    /// release, so it runs only while the environment is idle.
    /// </summary>
    public DeployOutcome<Deployment?> RollbackEnvironment(string project, string environmentId, DeployActor actor)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return DeployOutcome<Deployment?>.NotFound($"Unknown project '{project}'.");
            var environment = snapshot.Environments.FirstOrDefault(item => item.Id == environmentId);
            if (environment is not null && EnvironmentStatusProjection.PreviousRelease(
                    snapshot.Deployments(project).Where(item => item.EnvironmentId == environmentId),
                    snapshot.ReleaseById) is not null)
            {
                var queued = Rollback(project, environmentId, approve: true, actor);
                return new(queued.Kind, queued.Value, queued.Error);
            }
            if (HostBusy(snapshot, environmentId))
                return DeployOutcome<Deployment?>.Conflict("Wait until the active deployment has finished.");
            var releaseTarget = false;
            bool AcceptTarget(string artifact)
            {
                releaseTarget = snapshot.Releases.Any(item => item.ArtifactDigests.Contains(artifact, StringComparer.Ordinal));
                return !releaseTarget;
            }
            if (!_environments.Rollback(project, environmentId, actor.Id, AcceptTarget))
                return DeployOutcome<Deployment?>.Conflict(releaseTarget
                    ? $"{environment?.Name ?? environmentId} has no previous release to roll back to."
                    : "No previous artifact is available.");
            SyncLocked(project);
            return DeployOutcome<Deployment?>.Ok(null);
        }
    }

    private DeployOutcome<Deployment> StartForEnvironment(string project, string environmentId, DeployIntent intent,
        bool approve, DeployActor actor)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return DeployOutcome<Deployment>.NotFound($"Unknown project '{project}'.");
            var environment = snapshot.Environments.FirstOrDefault(item => item.Id == environmentId);
            if (environment is null) return DeployOutcome<Deployment>.NotFound($"Unknown environment '{environmentId}'.");
            var runs = snapshot.Deployments(project).Where(item => item.EnvironmentId == environmentId).ToArray();
            var release = intent == DeployIntent.Rollback
                ? EnvironmentStatusProjection.PreviousRelease(runs, snapshot.ReleaseById)
                : EnvironmentStatusProjection.CurrentRelease(runs, snapshot.ReleaseById);
            if (release is null)
                return DeployOutcome<Deployment>.Conflict(intent == DeployIntent.Rollback
                    ? $"{environment.Name} has no previous release to roll back to."
                    : $"{environment.Name} has no successfully deployed release.");
            return Start(project, snapshot, snapshot.Records[release.Id], environment, intent, approve, actor);
        }
    }

    private DeployOutcome<Deployment> Start(string project, Snapshot snapshot, ReleaseRecord record,
        DeployEnvironment environment, DeployIntent intent, bool approve, DeployActor actor)
    {
        var release = record.Release;
        var target = record.Environments.FirstOrDefault(item => item.Id == environment.Id);
        if (target is null) return MissingSnapshot(release, environment);
        var decision = StandardLifecyclePolicy.Decide(Facts(project, snapshot, record, environment, intent, approve,
            actor, excludeRunId: null));
        if (decision.Outcome == LifecycleOutcome.Reject) return Rejected(decision);

        var trigger = intent switch
        {
            DeployIntent.Redeploy => DeploymentTriggers.Redeploy,
            DeployIntent.Rollback => DeploymentTriggers.Rollback,
            _ => DeploymentTriggers.Deploy,
        };
        var now = _clock.GetUtcNow().UtcDateTime;
        DeployJob? job = null;
        if (decision.Outcome == LifecycleOutcome.Queue)
        {
            job = QueueJob(project, snapshot, trigger, target, release, decision.ApprovedBy ?? actor.Id);
            if (job is null) return DeployOutcome<Deployment>.Conflict("The deploy job could not be queued.");
        }
        var run = NewRun(release, environment, record.Environments,
            job is null ? DeploymentStates.AwaitingApproval : DeploymentStates.Queued, trigger, actor.Id,
            decision.ApprovedBy, job, now);
        var log = new List<DeploymentLogEntry>();
        if (job is null)
            log.Add(new(run.Id, DeployStepNames.Approval, now,
                $"Requested by {actor.Id}. Waiting for approval by {StandardLifecyclePolicy.ApprovalRoles}."));
        else if (decision.ApprovedBy is not null)
            log.Add(new(run.Id, DeployStepNames.Approval, now, $"Approved by {decision.ApprovedBy}."));
        if (job is not null)
            log.Add(new(run.Id, run.DeployStep, now,
                $"Release {release.Version} queued for {environment.Name} by {actor.Id} ({trigger})."));
        if (job is not null && HostChange(record, environment.Id, job) is { } moved)
            log.Add(new(run.Id, run.DeployStep, now, moved));
        var action = job is null ? "approval-requested" : trigger switch
        {
            DeploymentTriggers.Redeploy => "redeploy-queued",
            DeploymentTriggers.Rollback => "rollback-queued",
            _ => "deployment-queued",
        };
        var audit = new List<DeployAuditEntry>
        {
            new(now, actor.Id, action, release.Id, run.Id, environment.Id,
                $"Release {release.Version} {(job is null ? "awaits approval" : "queued")} for {environment.Name} ({trigger})."),
        };
        var runs = new List<DeploymentRecord>([.. snapshot.Runs, run]);
        if (job is not null) Supersede(project, snapshot, runs, run, intent, release, actor, log, audit, now);
        Persist(project, runs, log, audit);
        return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
    }

    /// <summary>The lifecycle facts for one release in one environment, without the run being approved.</summary>
    private static LifecycleRequest Facts(string project, Snapshot snapshot, ReleaseRecord record, DeployEnvironment environment,
        DeployIntent intent, bool approve, DeployActor actor, string? excludeRunId)
    {
        var release = record.Release;
        var deployments = snapshot.Deployments(project).Where(item => item.Id != excludeRunId).ToArray();
        var here = deployments.Where(item => item.EnvironmentId == environment.Id).ToArray();
        var required = record.Lifecycle.FirstOrDefault(item => item.Phase == environment.Kind)?.Requires;
        var previous = required is null && record.Lifecycle.Count > 0 ? null : required is not null
            ? snapshot.Environments.FirstOrDefault(item => item.Kind == required)
            : snapshot.Environments.Where(item => item.Order < environment.Order).OrderBy(item => item.Order).LastOrDefault();
        return new(intent, environment, actor, approve,
            previous is null || EnvironmentStatusProjection.SucceededIn(deployments, release.Id, previous.Id),
            EnvironmentStatusProjection.Position(release, EnvironmentStatusProjection.CurrentRelease(here, snapshot.ReleaseById)),
            EnvironmentStatusProjection.PreviousRelease(here, snapshot.ReleaseById)?.Id == release.Id,
            here.Any(item => item.ReleaseId == release.Id && DeploymentStates.IsActive(item.State)),
            here.Any(item => item.State is DeploymentStates.Queued or DeploymentStates.Running))
        { Channel = release.Channel, Phases = record.Lifecycle };
    }

    /// <summary>
    /// Closes the runs waiting for approval in the environment that the queued
    /// run makes invalid (<see cref="StandardLifecyclePolicy.Supersedes"/>),
    /// with their pending promotions.
    /// </summary>
    private void Supersede(string project, Snapshot snapshot, List<DeploymentRecord> runs, DeploymentRecord queued,
        DeployIntent intent, Release release, DeployActor actor, List<DeploymentLogEntry> log,
        List<DeployAuditEntry> audit, DateTime now)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var waiting = runs[i];
            if (waiting.Id == queued.Id || waiting.EnvironmentId != queued.EnvironmentId
                || waiting.State != DeploymentStates.AwaitingApproval
                || !snapshot.ReleaseById.TryGetValue(waiting.ReleaseId, out var waitingRelease)
                || !StandardLifecyclePolicy.Supersedes(intent, StandardLifecyclePolicy.IntentFor(waiting.Trigger),
                    EnvironmentStatusProjection.IsNewer(waitingRelease, release))) continue;
            if (ClosesPromotion(snapshot, waiting, waitingRelease)) ClosePromotion(project, waitingRelease, actor.Id);
            runs[i] = waiting with { State = DeploymentStates.Cancelled, CancelledBy = actor.Id, FinishedAtUtc = now };
            var message = $"Superseded by release {release.Version} ({queued.Trigger}) queued by {actor.Id}.";
            log.Add(new(waiting.Id, DeployStepNames.Approval, now, message));
            audit.Add(new(now, actor.Id, "deployment-superseded", waiting.ReleaseId, waiting.Id, waiting.EnvironmentId,
                message));
        }
    }

    private DeployOutcome<Deployment> Close(string project, DeploymentRecord run, DeployActor actor, bool rejected,
        bool closesPromotion)
    {
        var running = run.State == DeploymentStates.Running;
        if (run.State is DeploymentStates.Queued or DeploymentStates.Running
            && (run.JobId is null || !_environments.CancelJob(project, run.JobId, actor.Id, abortClaimed: running)))
            return DeployOutcome<Deployment>.Conflict(running
                ? "The deploy host has already reported this deployment."
                : "A deploy host has already claimed this deployment. Abort the running deployment instead.");
        // Close the AGT-3176 promotion too, so it neither expires later nor
        // asks for approval again.
        if (closesPromotion && _store.ReadReleases(project).FirstOrDefault(item => item.Id == run.ReleaseId) is { } release)
            ClosePromotion(project, release, actor.Id);
        var now = _clock.GetUtcNow().UtcDateTime;
        var closed = run with
        {
            State = DeploymentStates.Cancelled, CancelledBy = actor.Id, FinishedAtUtc = now,
            Evidence = running ? DeployEvidence.Aborted : run.Evidence,
        };
        var runs = _store.ReadDeployments(project).Select(item => item.Id == run.Id ? closed : item).ToList();
        var message = rejected ? $"Rejected by {actor.Id}."
            : running ? $"Aborted by {actor.Id} while deploy host {run.HostId} was running it."
            : $"Cancelled by {actor.Id}.";
        var action = rejected ? "deployment-rejected" : running ? "deployment-aborted" : "deployment-cancelled";
        Persist(project, runs,
            [new(run.Id, run.State == DeploymentStates.AwaitingApproval ? DeployStepNames.Approval : run.DeployStep, now, message)],
            [new(now, actor.Id, action, run.ReleaseId, run.Id, run.EnvironmentId, message)]);
        return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
    }

    /// <summary>A waiting plain deploy whose release has a pending AGT-3176 promotion into the environment.</summary>
    private static bool ClosesPromotion(Snapshot snapshot, DeploymentRecord run, Release release)
        => run.State == DeploymentStates.AwaitingApproval && run.Trigger == DeploymentTriggers.Deploy
           && snapshot.View.Promotions.Any(item => item.Status == "pending" && item.ProductionId == run.EnvironmentId
               && release.ArtifactDigests.Contains(item.Artifact, StringComparer.Ordinal));

    private void ClosePromotion(string project, Release release, string actor)
    {
        foreach (var artifact in release.ArtifactDigests)
            _environments.DecidePromotion(project, artifact, false, actor);
    }

    private static bool HostBusy(Snapshot snapshot, string environmentId)
        => snapshot.View.Jobs.Any(item => item.EnvironmentId == environmentId && item.Status is "queued" or "claimed");

    /// <summary>
    /// Queues the host job from the release's snapshot of the declaration. A
    /// plain deploy of a release whose AGT-3176 promotion is pending answers
    /// that promotion, so the older promotion state stays consistent.
    /// </summary>
    private DeployJob? QueueJob(string project, Snapshot snapshot, string trigger, EnvironmentDeclaration target,
        Release release, string actor)
    {
        var artifact = release.ArtifactDigests[0];
        if (trigger == DeploymentTriggers.Deploy && snapshot.View.Promotions.Any(item => item.Artifact == artifact
                && item.ProductionId == target.Id && item.Status == "pending"))
        {
            var (decided, job) = _environments.DecidePromotion(project, artifact, true, actor, target);
            if (decided) return job;
        }
        return _environments.QueueDeployment(project, target, artifact, actor,
            rollback: trigger == DeploymentTriggers.Rollback);
    }

    private DeployOutcome<Deployment>? FindRun(string project, string deploymentId, out Snapshot? snapshot,
        out DeploymentRecord? run, out ReleaseRecord? release)
    {
        snapshot = SyncLocked(project);
        var found = snapshot?.Runs.FirstOrDefault(item => item.Id == deploymentId);
        run = found;
        release = found is not null && snapshot!.Records.TryGetValue(found.ReleaseId, out var record) ? record : null;
        if (snapshot is null) return DeployOutcome<Deployment>.NotFound($"Unknown project '{project}'.");
        return found is null || release is null
            ? DeployOutcome<Deployment>.NotFound($"Unknown deployment '{deploymentId}'.") : null;
    }

    private static DeployOutcome<Deployment> Refuse(string state, string reason)
        => state == DeploymentStates.AwaitingApproval
            ? DeployOutcome<Deployment>.Forbidden(reason) : DeployOutcome<Deployment>.Conflict(reason);

    private static DeployOutcome<Deployment> Rejected(LifecycleDecision decision)
        => decision.Forbidden
            ? DeployOutcome<Deployment>.Forbidden(decision.Reason!) : DeployOutcome<Deployment>.Conflict(decision.Reason!);

    /// <summary>The run log line when the environment moved to another host after the release was created.</summary>
    private static string? HostChange(ReleaseRecord record, string environmentId, DeployJob job)
        => record.Environments.FirstOrDefault(item => item.Id == environmentId) is { } declared
           && declared.TargetHostId != job.HostId
            ? $"Release {record.Release.Version} declared deploy host {declared.TargetHostId}; the environment now "
              + $"declares deploy host {job.HostId}, so the run goes there with the release's declaration."
            : null;

    private static DeployOutcome<Deployment> MissingSnapshot(Release release, DeployEnvironment environment)
        => DeployOutcome<Deployment>.Conflict(
            $"Release {release.Version} has no declaration for {environment.Name} at its commit. Create a new release.");

    private (ReleaseRecord Release, bool Created) FindOrCreateRelease(string project, string id, string commit,
        IReadOnlyList<string> artifacts, IReadOnlyList<EnvironmentDeclaration> declarations,
        ProjectDeploymentDefinition? definition, string? repo, IReadOnlyList<IntegratedCard> integrated, DateTime now,
        ProjectChannelDefinition channel)
    {
        var existing = _store.ReadReleaseRecords(project);
        if (existing.FirstOrDefault(item => item.Release.Id == id) is { } known) return (known, false);
        var previous = existing.LastOrDefault()?.Release;
        var release = new Release(id, project, ReleaseSnapshot.NextVersion(existing.Select(item => item.Release), now),
            commit, artifacts, ReleaseSnapshot.ProcessVersion(definition, declarations),
            ReleaseSnapshot.VariablesVersion(definition, declarations),
            repo is null ? [] : CardKeys(repo, commit, previous?.CommitSha, integrated), now, channel.Name);
        var phases = channel.Lifecycle == "standard" ? definition?.StandardLifecycle
            : definition?.Lifecycles.GetValueOrDefault(channel.Lifecycle);
        return _store.AppendRelease(project, new ReleaseRecord(release, declarations) { Lifecycle = phases ?? [] });
    }

    private ProjectDefinitionReadResult? ReadDefinition(string repo, string commit)
        => _git.GetFileAtCommit(repo, commit, ProjectPreparationPaths.Definition) is { } yaml
            ? ProjectDefinitionReader.Parse(yaml) : null;

    private sealed record IntegratedCard(string Key, IReadOnlyList<string> Shas);

    /// <summary>Cards of the project with an integration verification and the commits it names.</summary>
    private IReadOnlyList<IntegratedCard> IntegratedCards(string project)
        => _scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Select(task => (Task: task, Record: IntegrationVerificationStore.Read(task.FolderPath)))
            .Where(item => item.Record is not null)
            .Select(item => new IntegratedCard(
                string.IsNullOrWhiteSpace(item.Task.Key) ? item.Task.Id : item.Task.Key,
                [.. item.Record!.Sha is { } sha ? [sha] : Array.Empty<string>(), .. item.Record.DeliveryShas]))
            .ToArray();

    /// <summary>
    /// Cards whose integration verification names a commit this release adds:
    /// reachable from the release commit and not from the previous release.
    /// </summary>
    private IReadOnlyList<string> CardKeys(string repo, string commit, string? previousCommit,
        IReadOnlyList<IntegratedCard> integrated)
    {
        var revisions = _git.ListRevisions(repo, commit, previousCommit,
                previousCommit is null ? FirstReleaseCommitWindow : ReleaseCommitWindow)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (revisions.Count == 0) return [];
        return integrated.Where(card => card.Shas.Any(revisions.Contains))
            .Select(card => card.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static DeploymentRecord NewRun(Release release, DeployEnvironment environment,
        IEnumerable<EnvironmentDeclaration> declarations, string state, string trigger, string requestedBy,
        string? approvedBy, DeployJob? job, DateTime now)
    {
        var template = declarations.FirstOrDefault(item => item.Id == environment.Id)?.Template ?? "container";
        var run = new DeploymentRecord
        {
            Id = job?.Id ?? Guid.NewGuid().ToString("N"), ReleaseId = release.Id, EnvironmentId = environment.Id,
            State = state, Trigger = trigger, DeployStep = DeployStepNames.ForTemplate(template),
            NeedsApproval = StandardLifecyclePolicy.RequiresApproval(environment),
            RequestedBy = requestedBy, ApprovedBy = approvedBy, ApprovedAtUtc = approvedBy is null ? null : now,
            JobId = job?.Id, CreatedAtUtc = now,
        };
        return run with { Steps = DeploymentStepPlan.Build(run) };
    }

    private Snapshot? SyncLocked(string project)
    {
        var view = _environments.Read(project);
        if (view is null) return null;
        var environments = MapEnvironments(project, view.Environments.Select(item => item.Declaration));
        var records = _store.ReadReleaseRecords(project);
        var result = DeploymentReconciler.Reconcile(new(environments, records, _store.ReadDeployments(project),
            view.Jobs, view.Promotions));
        if (result.Changed) Persist(project, result.Deployments, result.Log, result.Audit);
        return new(environments, records, result.Deployments, view);
    }

    private void Persist(string project, IReadOnlyList<DeploymentRecord> runs,
        IReadOnlyList<DeploymentLogEntry> log, IReadOnlyList<DeployAuditEntry> audit)
    {
        _store.AppendLog(project, log);
        _store.AppendAudit(project, audit);
        _store.SaveDeployments(project, runs);
        foreach (var entry in audit)
            _logger.LogInformation("Deploy audit {Project} {Action} by {Actor}: {Detail}",
                project, entry.Action, entry.Actor, entry.Detail);
    }

    private static string ShortSha(string commit) => commit.Length > 7 ? commit[..7] : commit;

    private sealed class Snapshot(IReadOnlyList<DeployEnvironment> environments, IReadOnlyList<ReleaseRecord> records,
        IReadOnlyList<DeploymentRecord> runs, ProjectEnvironmentsResponse view)
    {
        public IReadOnlyList<DeployEnvironment> Environments { get; } = environments;
        public IReadOnlyDictionary<string, ReleaseRecord> Records { get; } =
            records.ToDictionary(item => item.Release.Id, StringComparer.Ordinal);
        public IReadOnlyList<Release> Releases { get; } = records.Select(item => item.Release).ToArray();
        public IReadOnlyDictionary<string, Release> ReleaseById { get; } =
            records.ToDictionary(item => item.Release.Id, item => item.Release, StringComparer.Ordinal);
        public IReadOnlyList<DeploymentRecord> Runs { get; } = runs;
        public ProjectEnvironmentsResponse View { get; } = view;
        public IReadOnlyList<Deployment> Deployments(string project) => Runs.Select(item => ToContract(project, item)).ToArray();
    }

    public static Deployment ToContract(string project, DeploymentRecord run)
        => new(run.Id, run.ReleaseId, run.EnvironmentId, run.State, run.StartedAtUtc, run.FinishedAtUtc, run.ApprovedBy,
            run.Steps.Select(step => new DeploymentStep(step.Name, step.State, step.StartedAtUtc, step.FinishedAtUtc,
                $"/api/projects/{Uri.EscapeDataString(project)}/deploy/deployments/{run.Id}/log?step={step.Name}")).ToArray(),
            run.Trigger, run.RequestedBy, run.CreatedAtUtc);
}
