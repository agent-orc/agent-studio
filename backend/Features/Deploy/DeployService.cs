using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

public enum DeployOutcomeKind { Ok, NotFound, Conflict, Forbidden }

public sealed record DeployOutcome<T>(DeployOutcomeKind Kind, T? Value, string? Error)
{
    public static DeployOutcome<T> Ok(T value) => new(DeployOutcomeKind.Ok, value, null);
    public static DeployOutcome<T> NotFound(string error) => new(DeployOutcomeKind.NotFound, default, error);
    public static DeployOutcome<T> Conflict(string error) => new(DeployOutcomeKind.Conflict, default, error);
    public static DeployOutcome<T> Forbidden(string error) => new(DeployOutcomeKind.Forbidden, default, error);
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
    /// the gate artifact and start the automatic Staging phase.
    /// </summary>
    public DeployOutcome<Release> CreateRelease(string project, string artifact, string commit, string actor)
    {
        lock (_gate)
        {
            var declarations = _environments.Declarations(project);
            if (declarations is null) return DeployOutcome<Release>.NotFound($"Unknown project '{project}'.");
            var environments = MapEnvironments(project, declarations);
            var staging = environments.FirstOrDefault(StandardLifecyclePolicy.IsAutomatic);
            if (staging is null || !environments.Any(StandardLifecyclePolicy.RequiresApproval))
                return DeployOutcome<Release>.Conflict("Staging and production declarations are required.");
            var job = _environments.QueueReleaseJob(project, artifact, commit);
            if (job is null)
                return DeployOutcome<Release>.Conflict("Artifact, commit, or stage and production declarations are invalid.");

            var now = _clock.GetUtcNow().UtcDateTime;
            var (release, created) = FindOrCreateRelease(project, commit, [artifact], declarations, now);
            var runs = _store.ReadDeployments(project).ToList();
            var run = NewRun(release, staging, declarations, DeploymentStates.Queued, DeploymentTriggers.Deploy,
                actor, null, job, now);
            runs.Add(run);
            var log = new List<DeploymentLogEntry>
            {
                new(run.Id, run.DeployStep, now,
                    $"Release {release.Version} queued for {staging.Name} by {actor} (automatic after integration)."),
            };
            var audit = new List<DeployAuditEntry>();
            if (created)
                audit.Add(new(now, actor, "release-created", release.Id, null, null,
                    $"Release {release.Version} for commit {ShortSha(commit)} with {release.CardKeys.Count} card(s)."));
            audit.Add(new(now, actor, "deployment-queued", release.Id, run.Id, staging.Id,
                $"Release {release.Version} queued for {staging.Name}."));
            Persist(project, runs, log, audit);
            SyncLocked(project);
            return DeployOutcome<Release>.Ok(release);
        }
    }

    public IReadOnlyList<Release>? Releases(string project)
    {
        lock (_gate)
        {
            return SyncLocked(project)?.Releases.OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
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
            var release = snapshot.Releases.FirstOrDefault(item => item.Id == releaseId);
            if (release is null) return DeployOutcome<Deployment>.NotFound($"Unknown release '{releaseId}'.");
            var environment = snapshot.Environments.FirstOrDefault(item => item.Id == request.EnvironmentId);
            if (environment is null)
                return DeployOutcome<Deployment>.NotFound($"Unknown environment '{request.EnvironmentId}'.");
            return Start(project, snapshot, release, environment, DeployIntent.Deploy, request.Approve, actor);
        }
    }

    public DeployOutcome<Deployment> Redeploy(string project, string environmentId, bool approve, DeployActor actor)
        => StartForEnvironment(project, environmentId, DeployIntent.Redeploy, approve, actor);

    public DeployOutcome<Deployment> Rollback(string project, string environmentId, bool approve, DeployActor actor)
        => StartForEnvironment(project, environmentId, DeployIntent.Rollback, approve, actor);

    public DeployOutcome<Deployment> Approve(string project, string deploymentId, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out var snapshot, out var run, out var release);
            if (found is not null) return found;
            var rejection = StandardLifecyclePolicy.ApprovalRejection(run!.State, actor);
            if (rejection is not null) return Refuse(run.State, rejection);
            var now = _clock.GetUtcNow().UtcDateTime;
            var job = QueueJob(project, run.Trigger, run.EnvironmentId, release!, actor.Id);
            if (job is null) return DeployOutcome<Deployment>.Conflict("The deploy job could not be queued.");
            var environment = snapshot!.Environments.First(item => item.Id == run.EnvironmentId);
            var approved = run with
            {
                State = DeploymentStates.Queued, ApprovedBy = actor.Id, ApprovedAtUtc = now, JobId = job.Id,
            };
            var runs = snapshot.Runs.Select(item => item.Id == run.Id ? approved : item).ToList();
            Persist(project, runs,
            [
                new(run.Id, DeployStepNames.Approval, now, $"Approved by {actor.Id}."),
                new(run.Id, run.DeployStep, now, $"Queued for deploy host {job.HostId}."),
            ],
            [
                new(now, actor.Id, "deployment-approved", release!.Id, run.Id, run.EnvironmentId,
                    $"Release {release.Version} approved for {environment.Name}."),
            ]);
            return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
        }
    }

    public DeployOutcome<Deployment> Reject(string project, string deploymentId, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out _, out var run, out _);
            if (found is not null) return found;
            var rejection = StandardLifecyclePolicy.ApprovalRejection(run!.State, actor);
            return rejection is not null ? Refuse(run.State, rejection) : Close(project, run, actor, rejected: true);
        }
    }

    public DeployOutcome<Deployment> Cancel(string project, string deploymentId, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out _, out var run, out _);
            if (found is not null) return found;
            var rejection = StandardLifecyclePolicy.CancelRejection(run!.State);
            return rejection is not null ? DeployOutcome<Deployment>.Conflict(rejection) : Close(project, run, actor, rejected: false);
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
            var releases = snapshot.Releases.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var runs = snapshot.Deployments(project).Where(item => item.EnvironmentId == environmentId).ToArray();
            var release = intent == DeployIntent.Rollback
                ? EnvironmentStatusProjection.PreviousRelease(runs, releases)
                : EnvironmentStatusProjection.CurrentRelease(runs, releases);
            if (release is null)
                return DeployOutcome<Deployment>.Conflict(intent == DeployIntent.Rollback
                    ? $"{environment.Name} has no previous release to roll back to."
                    : $"{environment.Name} has no successfully deployed release.");
            return Start(project, snapshot, release, environment, intent, approve, actor);
        }
    }

    private DeployOutcome<Deployment> Start(string project, Snapshot snapshot, Release release,
        DeployEnvironment environment, DeployIntent intent, bool approve, DeployActor actor)
    {
        var deployments = snapshot.Deployments(project);
        var previous = snapshot.Environments.Where(item => item.Order < environment.Order)
            .OrderBy(item => item.Order).LastOrDefault();
        var decision = StandardLifecyclePolicy.Decide(new(intent, environment, actor, approve,
            previous is null || EnvironmentStatusProjection.SucceededIn(deployments, release.Id, previous.Id),
            EnvironmentStatusProjection.SucceededIn(deployments, release.Id, environment.Id),
            deployments.Any(item => item.EnvironmentId == environment.Id && item.ReleaseId == release.Id
                && DeploymentStates.IsActive(item.State)),
            deployments.Any(item => item.EnvironmentId == environment.Id && DeploymentStates.IsActive(item.State))));
        if (decision.Outcome == LifecycleOutcome.Reject)
            return approve && !actor.IsApprover && StandardLifecyclePolicy.RequiresApproval(environment)
                ? DeployOutcome<Deployment>.Forbidden(decision.Reason!)
                : DeployOutcome<Deployment>.Conflict(decision.Reason!);

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
            job = QueueJob(project, trigger, environment.Id, release, decision.ApprovedBy ?? actor.Id);
            if (job is null) return DeployOutcome<Deployment>.Conflict("The deploy job could not be queued.");
        }
        var run = NewRun(release, environment, snapshot.View.Environments.Select(item => item.Declaration),
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
        var action = job is null ? "approval-requested" : trigger switch
        {
            DeploymentTriggers.Redeploy => "redeploy-queued",
            DeploymentTriggers.Rollback => "rollback-queued",
            _ => "deployment-queued",
        };
        Persist(project, [.. snapshot.Runs, run], log,
            [new(now, actor.Id, action, release.Id, run.Id, environment.Id,
                $"Release {release.Version} {(job is null ? "awaits approval" : "queued")} for {environment.Name} ({trigger}).")]);
        return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
    }

    private DeployOutcome<Deployment> Close(string project, DeploymentRecord run, DeployActor actor, bool rejected)
    {
        if (run.State == DeploymentStates.Queued
            && (run.JobId is null || !_environments.CancelQueued(project, run.JobId, actor.Id)))
            return DeployOutcome<Deployment>.Conflict("A deploy host has already claimed this deployment.");
        if (run.State == DeploymentStates.AwaitingApproval && run.Trigger == DeploymentTriggers.Deploy)
        {
            // Close the AGT-3176 promotion too, so it neither expires later nor
            // asks for approval again.
            var release = _store.ReadReleases(project).FirstOrDefault(item => item.Id == run.ReleaseId);
            foreach (var artifact in release?.ArtifactDigests ?? [])
                _environments.DecidePromotion(project, artifact, false, actor.Id);
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        var closed = run with { State = DeploymentStates.Cancelled, CancelledBy = actor.Id, FinishedAtUtc = now };
        var runs = _store.ReadDeployments(project).Select(item => item.Id == run.Id ? closed : item).ToList();
        var message = rejected ? $"Rejected by {actor.Id}." : $"Cancelled by {actor.Id}.";
        Persist(project, runs,
            [new(run.Id, run.State == DeploymentStates.AwaitingApproval ? DeployStepNames.Approval : run.DeployStep, now, message)],
            [new(now, actor.Id, rejected ? "deployment-rejected" : "deployment-cancelled", run.ReleaseId, run.Id,
                run.EnvironmentId, message)]);
        return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
    }

    /// <summary>
    /// A plain deploy of a release whose AGT-3176 promotion is pending answers
    /// that promotion, so the older promotion state stays consistent.
    /// </summary>
    private DeployJob? QueueJob(string project, string trigger, string environmentId, Release release, string actor)
    {
        var artifact = release.ArtifactDigests[0];
        if (trigger == DeploymentTriggers.Deploy
            && _environments.Read(project)?.Promotions.Any(item => item.Artifact == artifact
                && item.ProductionId == environmentId && item.Status == "pending") == true)
        {
            var (decided, job) = _environments.DecidePromotion(project, artifact, true, actor);
            if (decided) return job;
        }
        return _environments.QueueDeployment(project, environmentId, artifact, actor,
            rollback: trigger == DeploymentTriggers.Rollback);
    }

    private DeployOutcome<Deployment>? FindRun(string project, string deploymentId, out Snapshot? snapshot,
        out DeploymentRecord? run, out Release? release)
    {
        snapshot = SyncLocked(project);
        var found = snapshot?.Runs.FirstOrDefault(item => item.Id == deploymentId);
        run = found;
        release = found is null ? null : snapshot!.Releases.FirstOrDefault(item => item.Id == found.ReleaseId);
        if (snapshot is null) return DeployOutcome<Deployment>.NotFound($"Unknown project '{project}'.");
        return found is null || release is null
            ? DeployOutcome<Deployment>.NotFound($"Unknown deployment '{deploymentId}'.") : null;
    }

    private static DeployOutcome<Deployment> Refuse(string state, string reason)
        => state == DeploymentStates.AwaitingApproval
            ? DeployOutcome<Deployment>.Forbidden(reason) : DeployOutcome<Deployment>.Conflict(reason);

    private (Release Release, bool Created) FindOrCreateRelease(string project, string commit,
        IReadOnlyList<string> artifacts, IReadOnlyList<EnvironmentDeclaration> declarations, DateTime now)
    {
        var existing = _store.ReadReleases(project);
        var id = ReleaseSnapshot.Id(project, commit, artifacts);
        if (existing.FirstOrDefault(item => item.Id == id) is { } known) return (known, false);
        var repo = _git.ResolveRepoRootForProject(project);
        var previous = existing.LastOrDefault();
        var yaml = repo is null ? null : _git.GetFileAtCommit(repo, commit, ProjectPreparationPaths.Definition);
        var definition = yaml is null ? null : ProjectDefinitionReader.Parse(yaml).Definition?.Deployment;
        var release = new Release(id, project, ReleaseSnapshot.NextVersion(existing, now), commit, artifacts,
            ReleaseSnapshot.ProcessVersion(definition, declarations), ReleaseSnapshot.VariablesVersion(declarations),
            repo is null ? [] : CardKeys(project, repo, commit, previous?.CommitSha), now, ReleaseSnapshot.DefaultChannel);
        return _store.AppendRelease(project, release);
    }

    /// <summary>
    /// Cards whose integration verification names a commit this release adds:
    /// reachable from the release commit and not from the previous release.
    /// </summary>
    private IReadOnlyList<string> CardKeys(string project, string repo, string commit, string? previousCommit)
    {
        var revisions = _git.ListRevisions(repo, commit, previousCommit,
                previousCommit is null ? FirstReleaseCommitWindow : ReleaseCommitWindow)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (revisions.Count == 0) return [];
        return _scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Where(task => IntegrationVerificationStore.Read(task.FolderPath) is { } record
                && (record.Sha is not null && revisions.Contains(record.Sha) || record.DeliveryShas.Any(revisions.Contains)))
            .Select(task => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key)
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
        var declarations = view.Environments.Select(item => item.Declaration).ToArray();
        var environments = MapEnvironments(project, declarations);
        var releases = _store.ReadReleases(project);
        var result = DeploymentReconciler.Reconcile(new(environments,
            declarations.ToDictionary(item => item.Id, item => DeployStepNames.ForTemplate(item.Template), StringComparer.Ordinal),
            releases, _store.ReadDeployments(project), view.Jobs, view.Promotions));
        if (result.Changed) Persist(project, result.Deployments, result.Log, result.Audit);
        return new(environments, releases, result.Deployments, view);
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

    private sealed record Snapshot(IReadOnlyList<DeployEnvironment> Environments, IReadOnlyList<Release> Releases,
        IReadOnlyList<DeploymentRecord> Runs, ProjectEnvironmentsResponse View)
    {
        public IReadOnlyList<Deployment> Deployments(string project) => Runs.Select(item => ToContract(project, item)).ToArray();
    }

    public static Deployment ToContract(string project, DeploymentRecord run)
        => new(run.Id, run.ReleaseId, run.EnvironmentId, run.State, run.StartedAtUtc, run.FinishedAtUtc, run.ApprovedBy,
            run.Steps.Select(step => new DeploymentStep(step.Name, step.State, step.StartedAtUtc, step.FinishedAtUtc,
                $"/api/projects/{Uri.EscapeDataString(project)}/deploy/deployments/{run.Id}/log?step={step.Name}")).ToArray(),
            run.Trigger, run.RequestedBy, run.CreatedAtUtc);
}
