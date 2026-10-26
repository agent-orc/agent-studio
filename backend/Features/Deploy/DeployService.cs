using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

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
    private readonly Dictionary<string, (DateTime Window, int Count)> _hostLogRates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hostLogRunIds = new(StringComparer.Ordinal);
    private readonly ProjectEnvironmentService _environments;
    private readonly DeployStore _store;
    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeployService> _logger;
    private readonly TaskMutationService? _mutations;

    /// <summary>
    /// Ancestry answers between release commits. Commits are immutable, so a
    /// definite answer never changes; card badge polls then ask git only about
    /// a release pair they have not seen.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Repo, string Ancestor, string Descendant), bool>
        _ancestry = new();

    /// <summary>Failed Production runs whose failure card exists or was attempted in this process.</summary>
    private readonly HashSet<string> _failureCardsHandled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Signature, DeliveryLaneResponse Value)> _deliveryLaneCache =
        new(StringComparer.OrdinalIgnoreCase);

    public DeployService(ProjectEnvironmentService environments, DeployStore store, GitService git,
        TaskScannerService scanner, TimeProvider clock, ILogger<DeployService> logger,
        TaskMutationService? mutations)
    {
        _environments = environments;
        _store = store;
        _git = git;
        _scanner = scanner;
        _clock = clock;
        _logger = logger;
        _mutations = mutations;
    }

    public static IReadOnlyList<DeployEnvironment> MapEnvironments(string project,
        IEnumerable<EnvironmentDeclaration> declarations)
        => declarations.Where(item => item.Kind is "stage" or "production")
            .Select((item, index) => new DeployEnvironment(item.Id, project, item.Name, index,
                item.Kind == "stage" ? EnvironmentKinds.Staging : EnvironmentKinds.Production)
            { Protected = item.Protected, Automatic = item.Automatic, Requires = item.Requires })
            .ToArray();

    public IReadOnlyList<DeployEnvironment>? Environments(string project)
        => _environments.Declarations(project) is { } declarations
            ? MapEnvironments(project, declarations).Concat(PreviewEnvironments(project)).ToArray() : null;

    private IReadOnlyList<DeployEnvironment> PreviewEnvironments(string project)
        => _scanner.ScanAllJobs().Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)
                && task.Preview is { Status: not "removing" })
            .Select(task => new DeployEnvironment("preview-" + CardPreviewPolicy.Slug(task.Key ?? task.TaskKey),
                project, $"Preview · {task.Key ?? task.TaskKey}", -1, EnvironmentKinds.Preview))
            .ToArray();

    public IReadOnlyList<ProjectTenantDefinition>? Tenants(string project)
    {
        lock (_gate)
        {
            if (_environments.Declarations(project) is null) return null;
            var repo = _git.ResolveRepoRootForProject(project);
            var path = repo is null ? null : Path.Combine(repo, ProjectPreparationPaths.Definition);
            if (path is not null && File.Exists(path)
                && ProjectDefinitionReader.Parse(File.ReadAllText(path)) is { IsValid: true } definition
                && definition.Definition?.Deployment is { } deployment)
                return deployment.Tenants;
            return SyncLocked(project)?.Records.Values.OrderByDescending(item => item.Release.CreatedAtUtc)
                .FirstOrDefault()?.Tenants ?? [];
        }
    }

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
        string? versionTag = null, string? branch = null, string? requestedChannel = null, bool mayOverride = false)
    {
        const string invalid = "Artifact, commit, or stage and production declarations are invalid.";
        try
        {
            if (_environments.Declarations(project) is null)
                return DeployOutcome<Release>.NotFound($"Unknown project '{project}'.");
        }
        catch (JsonException ex)
        {
            return DeployOutcome<Release>.Invalid($"The current environment declaration diverges from project.yml: {ex.Message}");
        }
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
        if (declarations.Any(item => (item.Kind is "stage" or "production")
                && !_environments.HasVerifiedArtifact(project, artifact, commit, item)))
            return DeployOutcome<Release>.Invalid("Artifact provenance does not match the release template.");
        var id = ReleaseSnapshot.Id(project, commit, [artifact]);
        var repo = _git.ResolveRepoRootForProject(project);
        var existing = _store.ReadReleaseRecords(project);
        var known = existing.Any(item => item.Release.Id == id);
        var newestSeen = existing.LastOrDefault()?.Release;
        if (OrderRejection(repo, newestSeen, id, commit, known) is { } orderRejection)
            return DeployOutcome<Release>.Conflict(orderRejection.Error, orderRejection.Reason);
        var integrated = known || repo is null ? [] : IntegratedCards(project);
        var definitionRead = repo is null ? null : ReadDefinition(repo, commit);
        if (definitionRead is { IsValid: false })
            return DeployOutcome<Release>.Invalid($"The deployment definition at commit {ShortSha(commit)} is invalid: "
                + string.Join("; ", definitionRead.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        var definition = definitionRead?.Definition?.Deployment;
        var knownRecord = existing.FirstOrDefault(item => item.Release.Id == id);
        var (channel, channelError) = knownRecord is null
            ? ReleaseChannelPolicy.Select(definition, versionTag, branch, requestedChannel, mayOverride)
            : (null, requestedChannel is not null && requestedChannel != knownRecord.Release.Channel
                ? $"Release already belongs to channel '{knownRecord.Release.Channel}'." : null);
        if (channelError is not null) return DeployOutcome<Release>.Invalid(channelError);
        var lifecycle = knownRecord is { Lifecycle.Count: > 0 }
            ? knownRecord.Lifecycle : ReleaseChannelPolicy.Lifecycle(definition, channel);
        var firstPhase = lifecycle.FirstOrDefault();
        if (firstPhase is null || firstPhase.Phase != staging.Kind || !firstPhase.Automatic)
            return DeployOutcome<Release>.Invalid($"Channel '{channel?.Name ?? ReleaseSnapshot.DefaultChannel}' lifecycle must start with automatic staging.");

        lock (_gate)
        {
            var current = _store.ReadReleaseRecords(project);
            var newest = current.LastOrDefault()?.Release;
            if (newest?.Id != newestSeen?.Id && OrderRejection(repo, newest, id, commit,
                    current.Any(item => item.Release.Id == id)) is { } raced)
                return DeployOutcome<Release>.Conflict(raced.Error, raced.Reason);
            var now = _clock.GetUtcNow().UtcDateTime;
            var (record, created) = FindOrCreateRelease(project, id, commit, [artifact], declarations, definition,
                channel?.Name ?? ReleaseSnapshot.DefaultChannel, lifecycle, repo, integrated, now);
            var release = record.Release;
            if (!created && requestedChannel is not null && release.Channel != requestedChannel)
                return DeployOutcome<Release>.Conflict($"Release already belongs to channel '{release.Channel}'.");
            var audit = new List<DeployAuditEntry>();
            if (created)
                audit.Add(new(now, actor, "release-created", release.Id, null, null,
                    $"Release {release.Version} for commit {ShortSha(commit)} with {release.CardKeys.Count} card(s)."));
            var (stageVariables, stageSecrets) = ResolveVariables(record, staging.Kind, null);
            var job = _environments.QueueReleaseJob(project, artifact, commit, record.Environments, release.Id,
                stageVariables, stageSecrets);
            if (job is null)
            {
                Persist(project, _store.ReadDeployments(project), [], audit);
                return DeployOutcome<Release>.Invalid(invalid);
            }
            var runs = _store.ReadDeployments(project).ToList();
            var run = NewRun(release, staging, record.Environments, DeploymentStates.Queued, DeploymentTriggers.Deploy,
                actor, null, job, now, record.Lifecycle);
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

    /// <param name="artifact">
    /// Only releases that carry this artifact's digest (registry prefix ignored),
    /// so a host artifact maps to its release without reading the whole history.
    /// </param>
    /// <param name="notAfterUtc">Only releases created at or before this time, such as the host deploy time.</param>
    public DeployPage<Release>? ReleasePage(string project, int offset, int limit,
        string? artifact = null, DateTime? notAfterUtc = null)
    {
        lock (_gate)
        {
            var digest = artifact is null ? null : DigestOf(artifact);
            var releases = SyncLocked(project)?.Releases
                .Where(item => digest is null || item.ArtifactDigests.Any(value => DigestOf(value) == digest))
                .Where(item => notAfterUtc is null || item.CreatedAtUtc <= notAfterUtc)
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
            return releases is null ? null : Page(releases, offset, limit);
        }
    }

    public DeployPage<Release>? ReleasePageContaining(string project, string releaseId, int limit)
    {
        lock (_gate)
        {
            var releases = SyncLocked(project)?.Releases.OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
            if (releases is null) return null;
            var index = Array.FindIndex(releases, item => item.Id == releaseId);
            if (index < 0) return null;
            var pageSize = Math.Clamp(limit, 1, 50);
            return Page(releases, index / pageSize * pageSize, pageSize);
        }
    }

    private static string DigestOf(string artifact)
    {
        var at = artifact.LastIndexOf('@');
        return at >= 0 ? artifact[(at + 1)..] : artifact;
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

    public IReadOnlyList<Deployment>? Deployments(string project, string? environmentId, string? tenant = null)
    {
        lock (_gate)
        {
            return SyncLocked(project)?.Deployments(project)
                .Where(item => (environmentId is null || item.EnvironmentId == environmentId)
                    && (tenant is null || item.Tenant == tenant))
                .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public DeployPage<Deployment>? DeploymentPage(string project, string? environmentId, string? state,
        DateTime? sinceUtc, int offset, int limit, string? tenant = null)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return null;
            var runs = snapshot.Deployments(project)
                .Where(item => environmentId is null || item.EnvironmentId == environmentId)
                .Where(item => tenant is null || item.Tenant == tenant)
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
            return new DeploymentLogResponse(deploymentId, _store.ReadLog(project, deploymentId)
                .Where(item => step is null || item.Step == step).ToArray())
            { Tenant = snapshot.Runs.First(item => item.Id == deploymentId).Tenant };
        }
    }

    public bool AppendHostStepLog(string project, string jobId, DeployStepLogRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseToken) || string.IsNullOrWhiteSpace(request.Message)
            || request.Message.Length > 512 || request.Message.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.Step) || request.Step.Length > 80) return false;
        lock (_gate)
        {
            var job = _environments.FindClaimedJob(project, jobId, request.LeaseToken);
            if (job is null || !job.SshSteps.Select((step, index) => $"deploy[{index}]:{step.Step}")
                    .Contains(request.Step, StringComparer.Ordinal)) return false;
            var now = _clock.GetUtcNow().UtcDateTime;
            if (_hostLogRates.Count > 1024)
                foreach (var expired in _hostLogRates.Where(item => item.Value.Window < now - TimeSpan.FromHours(1))
                             .Select(item => item.Key).ToArray())
                {
                    _hostLogRates.Remove(expired);
                    _hostLogRunIds.Remove(expired);
                }
            var key = project + "/" + jobId;
            if (_hostLogRates.TryGetValue(key, out var rate) && rate.Window > now - TimeSpan.FromMinutes(1))
            {
                if (rate.Count >= 30) return false;
                _hostLogRates[key] = (rate.Window, rate.Count + 1);
            }
            else _hostLogRates[key] = (now, 1);
            if (!_hostLogRunIds.TryGetValue(key, out var runId))
            {
                runId = _store.ReadDeployments(project).FirstOrDefault(item => item.JobId == jobId)?.Id ?? jobId;
                _hostLogRunIds[key] = runId;
            }
            _store.AppendLog(project, [new(runId, request.Step, now, request.Message)]);
            return true;
        }
    }

    public IReadOnlyList<DeployAuditEntry>? Audit(string project)
    {
        lock (_gate)
        {
            return SyncLocked(project) is null ? null : _store.ReadAudit(project, AuditLimit);
        }
    }

    public IReadOnlyList<EnvironmentStatus>? Status(string project, string? tenant = null)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            var previews = _scanner.ScanAllJobs().Where(task =>
                    string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)
                    && task.Preview is { Status: not ("removing" or "expired" or "removed") })
                .Select(task => new EnvironmentStatus(
                    new DeployEnvironment("preview-" + CardPreviewPolicy.Slug(task.Key ?? task.TaskKey), project,
                        $"Preview · {task.Key ?? task.TaskKey}", -1, EnvironmentKinds.Preview),
                    null, null, null, [], task.Preview!.Status == "ready" ? EnvironmentHealth.Healthy
                        : task.Preview.Status == "failed" ? EnvironmentHealth.Unhealthy : EnvironmentHealth.Unknown));
            if (snapshot is null)
            {
                var previewOnly = previews.ToArray();
                return previewOnly.Length == 0 ? null : previewOnly;
            }
            var hostHealth = tenant is null
                ? snapshot.View.Environments.ToDictionary(item => item.Declaration.Id, item => item.Health,
                    StringComparer.Ordinal)
                : snapshot.Deployments(project).Where(item => item.Tenant == tenant
                    && item.State is DeploymentStates.Succeeded or DeploymentStates.Failed)
                    .GroupBy(item => item.EnvironmentId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.OrderBy(item => item.FinishedAtUtc)
                        .ThenBy(item => item.CreatedAtUtc)
                        .ThenBy(item => item.Id, StringComparer.Ordinal).Last().State switch
                        {
                            DeploymentStates.Succeeded => "healthy",
                            DeploymentStates.Failed => "failed",
                            _ => "unknown",
                        }, StringComparer.Ordinal);
            var selected = tenant is null ? snapshot.Environments : snapshot.Environments
                .Where(environment => (Tenants(project) ?? []).Any(item =>
                    item.Name == tenant && item.Environments.Contains(environment.Kind, StringComparer.Ordinal))).ToArray();
            var releases = tenant is null ? snapshot.Releases : snapshot.Records.Values
                .Where(record => record.Tenants.Any(item => item.Name == tenant))
                .Select(record => record.Release).ToArray();
            return EnvironmentStatusProjection.Project(selected, releases,
                snapshot.Deployments(project).Where(item => item.Tenant == tenant).ToArray(), hostHealth)
                .Select(item => item with { Tenant = tenant })
                .Concat(tenant is null ? previews : []).ToArray();
        }
    }

    public IReadOnlyList<CardDeploymentView>? Cards(string project)
        => Cards(project, _scanner.ScanAllJobsWithArchive()
            .Where(item => string.Equals(item.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Select(item => string.IsNullOrWhiteSpace(item.Key) ? item.Id : item.Key!));

    private IReadOnlyList<CardDeploymentView>? Cards(string project, IEnumerable<string> cardKeys)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return null;
            var repo = _git.ResolveRepoRootForProject(project);
            return CardDeploymentProjection.Project(project, snapshot.Environments,
                snapshot.Releases, snapshot.Deployments(project), cardKeys,
                (origin, target) => repo is not null && Contains(repo, origin.CommitSha, target.CommitSha));
        }
    }

    public IReadOnlyList<CardDeploymentView> CardMatrix()
    {
        var cards = new List<CardDeploymentView>();
        var byProject = _scanner.ScanAllJobsWithArchive()
            .ToLookup(item => item.ProjectName, StringComparer.OrdinalIgnoreCase);
        foreach (var project in _scanner.GetWatchPaths().Select(item => item.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (_environments.Declarations(project) is not { Count: > 0 }) continue;
                if (Cards(project, byProject[project].Select(item => string.IsNullOrWhiteSpace(item.Key)
                        ? item.Id : item.Key!)) is { } projected) cards.AddRange(projected);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Deploy card matrix skipped project {Project}: invalid deployment state", project);
            }
        }
        return cards;
    }

    public ReleaseNotesResponse? Notes(string project, string releaseId)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            var record = snapshot?.Records.GetValueOrDefault(releaseId);
            var release = record?.Release;
            if (release is null) return null;
            var currentTitles = _scanner.ScanAllJobsWithArchive()
                .Where(item => string.Equals(item.ProjectName, project, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);
            var cards = record!.CardTitles is { } captured
                ? captured.Concat(currentTitles.Where(item => !captured.ContainsKey(item.Key)))
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
                : currentTitles;
            var production = snapshot!.Environments.Where(item => item.Kind == EnvironmentKinds.Production)
                .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var preview = !snapshot.Deployments(project).Any(item => item.ReleaseId == releaseId
                && production.Contains(item.EnvironmentId) && item.State == DeploymentStates.Succeeded);
            return ReleaseNotesProjection.Project(release, cards, preview);
        }
    }

    /// <summary>Read-only release placement; the cache changes with releases, deploy runs and integration evidence.</summary>
    public DeliveryLaneResponse? DeliveryLanes(string project)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return null;
            var tasks = _scanner.ScanAllJobsWithArchive()
                .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
                .Select(task => (Task: task, Stamp: File.Exists(Path.Combine(task.FolderPath,
                    IntegrationVerificationStore.FileName))
                    ? File.GetLastWriteTimeUtc(Path.Combine(task.FolderPath, IntegrationVerificationStore.FileName)).Ticks : 0L))
                .ToArray();
            var runs = snapshot.Deployments(project);
            var signature = string.Join('|', snapshot.Environments.Select(item =>
                    item.Id + ':' + item.Name + ':' + item.Order))
                + "|" + string.Join('|', snapshot.Releases.Select(item => item.Id + ':' + item.CardKeys.Count))
                + "|" + string.Join('|', runs.Select(item => item.Id + ':' + item.State + ':' + item.FinishedAtUtc?.Ticks))
                + "|" + string.Join('|', tasks.Select(item => item.Task.Id + ':' + item.Task.Title + ':' + item.Stamp));
            if (_deliveryLaneCache.TryGetValue(project, out var cached) && cached.Signature == signature)
                return cached.Value;
            var integrated = tasks.Where(item => item.Stamp != 0)
                .Select(item => (item.Task, Verification: IntegrationVerificationStore.Read(item.Task.FolderPath)))
                .Where(item => item.Verification?.State == IntegrationVerificationStates.Verified)
                .ToDictionary(item => string.IsNullOrWhiteSpace(item.Task.Key) ? item.Task.Id : item.Task.Key!,
                    item => item.Task.Title, StringComparer.OrdinalIgnoreCase);
            var value = DeliveryLaneProjection.Project(project, snapshot.Environments, snapshot.Releases, runs, integrated);
            _deliveryLaneCache[project] = (signature, value);
            return value;
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
                var statuses = Status(project)?.Where(item => item.Environment.Kind is
                    EnvironmentKinds.Staging or EnvironmentKinds.Production).ToArray();
                if (statuses is { Length: > 0 })
                    rows.Add(new(project, statuses, null)
                    { TenantStatuses = (Tenants(project) ?? []).SelectMany(tenant => Status(project, tenant.Name) ?? [])
                        .Where(item => item.Environment.Kind is EnvironmentKinds.Staging or EnvironmentKinds.Production)
                        .ToArray() });
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
            return Start(project, snapshot, record, environment, DeployIntent.Deploy, request.Approve, actor,
                request.Tenant, request.GuidedFailure);
        }
    }

    public DeployOutcome<Deployment> Redeploy(string project, string environmentId, bool approve, DeployActor actor,
        string? tenant = null, bool? guidedFailure = null)
        => StartForEnvironment(project, environmentId, DeployIntent.Redeploy, approve, actor, tenant, guidedFailure);

    public DeployOutcome<Deployment> Rollback(string project, string environmentId, bool approve, DeployActor actor,
        string? tenant = null, bool? guidedFailure = null)
        => StartForEnvironment(project, environmentId, DeployIntent.Rollback, approve, actor, tenant, guidedFailure);

    /// <summary>
    /// Approves a waiting run. The lifecycle decides again, because another
    /// run may have reached the environment since the request: the approval is
    /// refused while another run is queued or running there, and a deploy that
    /// is no longer newer than the current release needs a rollback instead.
    /// Queuing the run closes the waiting runs it makes invalid.
    /// </summary>
    public DeployOutcome<Deployment> Approve(string project, string deploymentId, DeployActor actor,
        bool? guidedFailure = null)
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
            if (run.Tenant is not null && !target.TenantTargets.ContainsKey(run.Tenant))
                return DeployOutcome<Deployment>.Conflict($"Tenant '{run.Tenant}' has no target in {environment.Name} for this release.");
            var intent = StandardLifecyclePolicy.IntentFor(run.Trigger);
            var decision = StandardLifecyclePolicy.Decide(Facts(project, snapshot, release, environment, intent,
                approve: true, actor, excludeRunId: run.Id, tenant: run.Tenant));
            if (decision.Outcome == LifecycleOutcome.Reject) return Rejected(decision);

            var now = _clock.GetUtcNow().UtcDateTime;
            var guided = guidedFailure ?? run.GuidedFailure;
            var job = QueueJob(project, snapshot, run.Trigger, target with { GuidedFailure = guided }, release, actor.Id, run.Tenant);
            if (job is null) return DeployOutcome<Deployment>.Conflict("The deploy job could not be queued.");
            var approved = run with
            {
                State = DeploymentStates.Queued, ApprovedBy = actor.Id, ApprovedAtUtc = now, JobId = job.Id,
                GuidedFailure = guided,
            };
            var runs = snapshot.Runs.Select(item => item.Id == run.Id ? approved : item).ToList();
            var log = new List<DeploymentLogEntry>
            {
                new(run.Id, DeployStepNames.Approval, now, run.Tenant is null
                    ? $"Approved by {actor.Id}." : $"Tenant {run.Tenant} approved by {actor.Id}."),
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
            if (run!.State == DeploymentStates.WaitingForIntervention)
                return DecideIntervention(project, deploymentId, new InterventionDecisionRequest("abort"), actor);
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

    /// <summary>Resolve one failed step while the host job is no longer active.</summary>
    public DeployOutcome<Deployment> DecideIntervention(string project, string deploymentId,
        InterventionDecisionRequest request, DeployActor actor)
    {
        lock (_gate)
        {
            var found = FindRun(project, deploymentId, out var snapshot, out var run, out var release);
            if (found is not null) return found;
            var rejection = GuidedFailurePolicy.RejectDecision(run!.State, request.Action, actor.IsApprover,
                request.Reason);
            if (rejection is not null) return run.State != DeploymentStates.WaitingForIntervention
                ? DeployOutcome<Deployment>.Conflict(rejection)
                : !actor.IsApprover ? DeployOutcome<Deployment>.Forbidden(rejection)
                : DeployOutcome<Deployment>.Invalid(rejection);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (run.InterventionExpiresAtUtc is { } expires && now >= expires)
            {
                SyncLocked(project);
                return DeployOutcome<Deployment>.Conflict("The intervention window has expired.");
            }
            var updated = run with { InterventionDecidedBy = actor.Id, InterventionDecision = request.Action,
                InterventionExpiresAtUtc = null };
            if (request.Action == "retry")
            {
                var target = release!.Environments.FirstOrDefault(item => item.Id == run.EnvironmentId);
                if (target is null) return DeployOutcome<Deployment>.Conflict("The release has no environment snapshot.");
                var job = _environments.QueueRetry(project, target with { GuidedFailure = run.GuidedFailure },
                    release.Release.ArtifactDigests[0], actor.Id, run.JobId!,
                    retryStep: run.FailedStep == DeployStepNames.Health && run.SshStepNames.Count == 0
                        ? DeployStepNames.Health : null);
                if (job is null) return DeployOutcome<Deployment>.Conflict(
                    "The failed step cannot be retried after another job entered this environment.");
                updated = updated with { State = DeploymentStates.Queued, JobId = job.Id,
                    PriorJobIds = [.. run.PriorJobIds, run.JobId!], FailedStep = null, Evidence = null,
                    RetryStep = job.RetryStep,
                    StartedAtUtc = null, FinishedAtUtc = null, HostId = null, HostClaims = 0 };
            }
            else if (request.Action == "skip")
                updated = updated with { State = DeploymentStates.Succeeded, FinishedAtUtc = now };
            else
                updated = updated with { State = DeploymentStates.Cancelled, FinishedAtUtc = now,
                    CancelledBy = actor.Id };
            updated = updated with { Steps = DeploymentStepPlan.Build(updated) };
            var step = run.FailedStep ?? run.DeployStep;
            var detail = request.Action == "skip" ? $"Skipped {step} by {actor.Id}: {request.Reason!.Trim()}"
                : request.Action == "retry" ? $"Retry of {step} queued by {actor.Id}."
                : $"Aborted after {step} failed by {actor.Id}.";
            var runs = snapshot!.Runs.Select(item => item.Id == run.Id ? updated : item).ToArray();
            Persist(project, runs, [new(run.Id, step, now, detail)],
                [new(now, actor.Id, "intervention-" + request.Action, run.ReleaseId, run.Id,
                    run.EnvironmentId, detail)]);
            if (request.Action is ("abort" or "skip") && run.JobId is not null)
                _environments.RollbackFailedJob(project, run.JobId, rollback: request.Action == "abort");
            return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
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
        bool approve, DeployActor actor, string? tenant, bool? guidedFailure = null)
    {
        lock (_gate)
        {
            var snapshot = SyncLocked(project);
            if (snapshot is null) return DeployOutcome<Deployment>.NotFound($"Unknown project '{project}'.");
            var environment = snapshot.Environments.FirstOrDefault(item => item.Id == environmentId);
            if (environment is null) return DeployOutcome<Deployment>.NotFound($"Unknown environment '{environmentId}'.");
            var runs = snapshot.Deployments(project).Where(item => item.EnvironmentId == environmentId
                && item.Tenant == tenant).ToArray();
            var release = intent == DeployIntent.Rollback
                ? EnvironmentStatusProjection.PreviousRelease(runs, snapshot.ReleaseById)
                : EnvironmentStatusProjection.CurrentRelease(runs, snapshot.ReleaseById);
            if (release is null)
                return DeployOutcome<Deployment>.Conflict(intent == DeployIntent.Rollback
                    ? $"{environment.Name} has no previous release to roll back to."
                    : $"{environment.Name} has no successfully deployed release.");
            return Start(project, snapshot, snapshot.Records[release.Id], environment, intent, approve, actor,
                tenant, guidedFailure);
        }
    }

    private DeployOutcome<Deployment> Start(string project, Snapshot snapshot, ReleaseRecord record,
        DeployEnvironment environment, DeployIntent intent, bool approve, DeployActor actor,
        string? tenant = null, bool? guidedFailure = null)
    {
        var release = record.Release;
        if (tenant is not null && !record.Tenants.Any(item => item.Name == tenant
                && item.Environments.Contains(environment.Kind, StringComparer.Ordinal)))
            return DeployOutcome<Deployment>.Invalid($"Tenant '{tenant}' does not participate in {environment.Name} for this release.");
        var target = record.Environments.FirstOrDefault(item => item.Id == environment.Id);
        if (target is null) return MissingSnapshot(release, environment);
        if (tenant is not null && !target.TenantTargets.ContainsKey(tenant))
            return DeployOutcome<Deployment>.Invalid($"Tenant '{tenant}' has no target in {environment.Name} for this release.");
        var decision = StandardLifecyclePolicy.Decide(Facts(project, snapshot, release, environment, intent, approve,
            actor, excludeRunId: null, tenant: tenant));
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
            job = QueueJob(project, snapshot, trigger, target with { GuidedFailure = guidedFailure ?? target.GuidedFailure },
                release, decision.ApprovedBy ?? actor.Id, tenant);
            if (job is null) return DeployOutcome<Deployment>.Conflict("The deploy job could not be queued.");
        }
        var run = NewRun(release, environment, record.Environments,
            job is null ? DeploymentStates.AwaitingApproval : DeploymentStates.Queued, trigger, actor.Id,
            decision.ApprovedBy, job, now, record.Lifecycle, guidedFailure, tenant);
        var log = new List<DeploymentLogEntry>();
        if (job is null)
            log.Add(new(run.Id, DeployStepNames.Approval, now,
                tenant is null
                    ? $"Requested by {actor.Id}. Waiting for approval by {StandardLifecyclePolicy.ApprovalRoles}."
                    : $"Requested by {actor.Id} for tenant {tenant}. Waiting for approval by {StandardLifecyclePolicy.ApprovalRoles}."));
        else if (decision.ApprovedBy is not null)
            log.Add(new(run.Id, DeployStepNames.Approval, now, $"Approved by {decision.ApprovedBy}."));
        if (job is not null)
            log.Add(new(run.Id, run.DeployStep, now,
                tenant is null
                    ? $"Release {release.Version} queued for {environment.Name} by {actor.Id} ({trigger})."
                    : $"Release {release.Version} queued for {environment.Name} tenant {tenant} by {actor.Id} ({trigger})."));
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
                $"Release {release.Version} {(job is null ? "awaits approval" : "queued")} for {environment.Name}"
                + (tenant is null ? "" : $" tenant {tenant}") + $" ({trigger})."),
        };
        var runs = new List<DeploymentRecord>([.. snapshot.Runs, run]);
        if (job is not null) Supersede(project, snapshot, runs, run, intent, release, actor, log, audit, now);
        Persist(project, runs, log, audit);
        return DeployOutcome<Deployment>.Ok(SyncLocked(project)!.Deployments(project).First(item => item.Id == run.Id));
    }

    /// <summary>The lifecycle facts for one release in one environment, without the run being approved.</summary>
    private static LifecycleRequest Facts(string project, Snapshot snapshot, Release release, DeployEnvironment environment,
        DeployIntent intent, bool approve, DeployActor actor, string? excludeRunId, string? tenant)
    {
        if (snapshot.Records.TryGetValue(release.Id, out var record))
            environment = MapEnvironments(project, record.Environments)
                .FirstOrDefault(item => item.Id == environment.Id) ?? environment;
        var deployments = snapshot.Deployments(project).Where(item => item.Id != excludeRunId).ToArray();
        var here = deployments.Where(item => item.EnvironmentId == environment.Id && item.Tenant == tenant).ToArray();
        var tenantDeployments = deployments.Where(item => item.Tenant == tenant).ToArray();
        var releaseEnvironments = snapshot.Records.TryGetValue(release.Id, out var previousRecord)
            ? MapEnvironments(project, previousRecord.Environments) : snapshot.Environments;
        var previous = releaseEnvironments.Where(item => item.Order < environment.Order)
            .OrderBy(item => item.Order).LastOrDefault();
        var phases = snapshot.Records.TryGetValue(release.Id, out var lifecycleRecord) ? lifecycleRecord.Lifecycle : [];
        var required = phases.FirstOrDefault(item => item.Phase == environment.Kind)?.Requires;
        var prerequisite = phases.Count == 0 ? previous
            : required is null ? null : releaseEnvironments.FirstOrDefault(item => item.Kind == required);
        return new(intent, environment, actor, approve,
            prerequisite is null || EnvironmentStatusProjection.SucceededIn(tenantDeployments, release.Id, prerequisite.Id),
            EnvironmentStatusProjection.Position(release, EnvironmentStatusProjection.CurrentRelease(here, snapshot.ReleaseById)),
            EnvironmentStatusProjection.PreviousRelease(here, snapshot.ReleaseById)?.Id == release.Id,
            here.Any(item => item.ReleaseId == release.Id && DeploymentStates.IsActive(item.State)),
            here.Any(item => item.State is DeploymentStates.Queued or DeploymentStates.Running)) { Phases = phases };
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
            if (waiting.Id == queued.Id || waiting.EnvironmentId != queued.EnvironmentId || waiting.Tenant != queued.Tenant
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
        => run.Tenant is null && run.State == DeploymentStates.AwaitingApproval && run.Trigger == DeploymentTriggers.Deploy
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
        Release release, string actor, string? tenant)
    {
        var artifact = release.ArtifactDigests[0];
        var record = snapshot.Records[release.Id];
        var environment = target.Kind == "stage" ? EnvironmentKinds.Staging : EnvironmentKinds.Production;
        var (values, secrets) = ResolveVariables(record, environment, tenant);
        if (tenant is null && trigger == DeploymentTriggers.Deploy && snapshot.View.Promotions.Any(item => item.Artifact == artifact
                && item.ProductionId == target.Id && item.Status == "pending"))
        {
            var (decided, job) = _environments.DecidePromotion(project, artifact, true, actor, target, release.Id, values, secrets);
            if (decided) return job;
        }
        return _environments.QueueDeployment(project, target, artifact, actor,
            rollback: trigger == DeploymentTriggers.Rollback, tenant: tenant, releaseId: release.Id,
            variables: values, secretVariableRefs: secrets);
    }

    private static (IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, string> Secrets)
        ResolveVariables(ReleaseRecord record, string environment, string? tenant)
    {
        var selectedTenant = record.Tenants.FirstOrDefault(item => item.Name == tenant);
        var resolved = record.Variables.Select(item => item.Name).Distinct(StringComparer.Ordinal)
            .Select(name => ProjectVariableResolution.Resolve(record.Variables, name, environment,
                selectedTenant, record.Release.Channel))
            .Where(item => item is not null).ToArray();
        var values = resolved.Where(item => item!.Value is not null)
            .ToDictionary(item => item!.Name, item => item!.Value!, StringComparer.Ordinal);
        var secrets = resolved.Where(item => item!.Secret is not null)
            .ToDictionary(item => item!.Name, item => item!.Secret!, StringComparer.Ordinal);
        return (values, secrets);
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
        ProjectDeploymentDefinition? definition, string channel, IReadOnlyList<ProjectLifecyclePhase> lifecycle,
        string? repo, IReadOnlyList<IntegratedCard> integrated, DateTime now)
    {
        var existing = _store.ReadReleaseRecords(project);
        if (existing.FirstOrDefault(item => item.Release.Id == id) is { } known) return (known, false);
        var previous = existing.LastOrDefault()?.Release;
        var priorKeys = existing.Where(item => repo is not null &&
                Contains(repo, item.Release.CommitSha, commit))
            .SelectMany(item => item.Release.CardKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cardKeys = repo is null ? [] : CardKeys(repo, commit, previous?.CommitSha, integrated, priorKeys);
        var release = new Release(id, project, ReleaseSnapshot.NextVersion(existing.Select(item => item.Release), now),
            commit, artifacts, ReleaseSnapshot.ProcessVersion(definition, declarations),
            ReleaseSnapshot.VariablesVersion(definition, declarations),
            cardKeys, now, channel);
        var titles = integrated.Where(card => cardKeys.Contains(card.Key, StringComparer.OrdinalIgnoreCase))
            .GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);
        return _store.AppendRelease(project, new(release, declarations, titles) { Lifecycle = lifecycle, Tenants = definition?.Tenants ?? [], Variables = definition?.Variables ?? [] });
    }

    private ProjectDefinitionReadResult? ReadDefinition(string repo, string commit)
        => _git.GetFileAtCommit(repo, commit, ProjectPreparationPaths.Definition) is { } yaml
            ? ProjectDefinitionReader.Parse(yaml) : null;

    private sealed record IntegratedCard(string Key, string Title, IReadOnlyList<string> Shas,
        string IntegrationBranch);

    /// <summary>Cards of the project with an integration verification and the commits it names.</summary>
    private IReadOnlyList<IntegratedCard> IntegratedCards(string project)
        => _scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Select(task => (Task: task, Record: IntegrationVerificationStore.Read(task.FolderPath)))
            .Where(item => item.Record is { State: IntegrationVerificationStates.Verified })
            .Select(item => new IntegratedCard(
                string.IsNullOrWhiteSpace(item.Task.Key) ? item.Task.Id : item.Task.Key,
                item.Task.Title,
                [.. item.Record!.Sha is { } sha ? [sha] : Array.Empty<string>(), .. item.Record.DeliveryShas],
                item.Record.IntegrationBranch))
            .ToArray();

    /// <summary>
    /// The integration verification is the durable card mapping. A card joins
    /// the release that adds a commit its verification names, or a commit whose
    /// message keeps its key. A rewritten merge, rebase or squash may keep
    /// neither, so the release also adds the cards behind an integration-branch
    /// commit whose tree the release delta carries unchanged. Cards of an
    /// ancestor release never join again.
    /// </summary>
    private IReadOnlyList<string> CardKeys(string repo, string commit, string? previousCommit,
        IReadOnlyList<IntegratedCard> integrated, IReadOnlySet<string> priorKeys)
    {
        var window = previousCommit is null ? FirstReleaseCommitWindow : ReleaseCommitWindow;
        var delta = _git.ListRevisionDetails(repo, commit, previousCommit, window);
        if (delta.Count == 0) return [];
        var revisions = delta.Select(item => item.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mentioned = ReleaseCardAttribution.FindKeys(delta.Select(item => item.Message));
        var candidates = integrated.Where(card => !priorKeys.Contains(card.Key)).ToArray();
        var direct = candidates.Where(card => card.Shas.Any(revisions.Contains) || mentioned.Contains(card.Key))
            .Select(card => card.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rewritten = candidates.Where(card => !direct.Contains(card.Key))
            .GroupBy(card => card.IntegrationBranch, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var carried = RewrittenIntegration(repo, group.Key, delta, previousCommit, window);
                return group.Where(card => card.Shas.Any(carried.Contains)).Select(card => card.Key);
            });
        return direct.Concat(rewritten)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Integration-branch commits the release carries as rewritten content: the
    /// history of the newest branch commit whose tree equals a delta commit's
    /// tree, down to the branch commit the previous release carried the same way.
    /// </summary>
    private IReadOnlySet<string> RewrittenIntegration(string repo, string branch,
        IReadOnlyList<RevisionDetail> delta, string? previousCommit, int window)
    {
        if (string.IsNullOrWhiteSpace(branch)) return new HashSet<string>();
        var history = _git.ListRevisionDetails(repo, "origin/" + branch, null, ReleaseCommitWindow, includeMessages: false);
        if (history.Count == 0)
            history = _git.ListRevisionDetails(repo, branch, null, ReleaseCommitWindow, includeMessages: false);
        var trees = delta.Select(item => item.Tree).ToHashSet(StringComparer.Ordinal);
        if (history.FirstOrDefault(item => trees.Contains(item.Tree)) is not { } carried)
            return new HashSet<string>();
        var previousTree = previousCommit is null ? null
            : _git.ListRevisionDetails(repo, previousCommit, null, 1, includeMessages: false).FirstOrDefault()?.Tree;
        var boundary = history.FirstOrDefault(item => item.Tree == previousTree)?.Sha ?? previousCommit;
        return _git.ListRevisions(repo, carried.Sha, boundary, window).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool Contains(string repo, string ancestor, string descendant)
    {
        if (string.Equals(ancestor, descendant, StringComparison.OrdinalIgnoreCase)) return true;
        if (_ancestry.TryGetValue((repo, ancestor, descendant), out var known)) return known;
        var answer = _git.TryIsAncestor(repo, ancestor, descendant);
        if (answer is { } definite) _ancestry[(repo, ancestor, descendant)] = definite;
        return answer == true;
    }

    private static DeploymentRecord NewRun(Release release, DeployEnvironment environment,
        IEnumerable<EnvironmentDeclaration> declarations, string state, string trigger, string requestedBy,
        string? approvedBy, DeployJob? job, DateTime now, IReadOnlyList<ProjectLifecyclePhase>? phases = null,
        bool? guidedFailure = null, string? tenant = null)
    {
        var declaration = declarations.FirstOrDefault(item => item.Id == environment.Id);
        var template = declaration?.Template ?? "container";
        var run = new DeploymentRecord
        {
            Id = job?.Id ?? Guid.NewGuid().ToString("N"), ReleaseId = release.Id, EnvironmentId = environment.Id,
            Tenant = tenant,
            State = state, Trigger = trigger,
            DeployStep = declaration?.SshSteps.FirstOrDefault()?.Step == "static-site"
                ? DeployStepNames.PublishStatic : DeployStepNames.ForTemplate(template),
            NeedsApproval = phases is { Count: > 0 }
                ? phases.FirstOrDefault(item => item.Phase == environment.Kind)?.Approval.Count > 0
                : declaration?.Protected ?? StandardLifecyclePolicy.RequiresApproval(environment),
            RequestedBy = requestedBy, ApprovedBy = approvedBy, ApprovedAtUtc = approvedBy is null ? null : now,
            JobId = job?.Id, CreatedAtUtc = now,
            GuidedFailure = guidedFailure ?? declaration?.GuidedFailure ?? false,
            InterventionTimeoutMinutes = declaration?.InterventionTimeoutMinutes ?? 60,
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
            view.Jobs, view.Promotions, _clock.GetUtcNow().UtcDateTime));
        if (result.Changed) Persist(project, result.Deployments, result.Log, result.Audit);
        CreateProductionFailureCards(project, environments, records, result.Deployments);
        var snapshot = new Snapshot(environments, records, result.Deployments, view);
        foreach (var run in result.Deployments.Where(item => item.InterventionDecision is "abort" or "skip" or "timeout"
                     && DeploymentStates.IsFinished(item.State) && item.JobId is not null))
            if (view.Jobs.FirstOrDefault(item => item.Id == run.JobId) is { GuidedResolved: false })
                _environments.RollbackFailedJob(project, run.JobId!, rollback: run.InterventionDecision != "skip");
        foreach (var record in records.Where(item => item.Lifecycle.Count > 1))
        {
            foreach (var phase in record.Lifecycle.Skip(1).Where(item => item.Automatic && item.Approval.Count == 0))
            {
                var environment = environments.FirstOrDefault(item => item.Kind == phase.Phase);
                if (environment is null || result.Deployments.Any(item => item.ReleaseId == record.Release.Id
                        && item.EnvironmentId == environment.Id)) continue;
                if (phase.Requires is { } required && !environments.Any(item => item.Kind == required
                        && EnvironmentStatusProjection.SucceededIn(snapshot.Deployments(project), record.Release.Id, item.Id)))
                    continue;
                if (Start(project, snapshot, record, environment, DeployIntent.Deploy, false,
                        new DeployActor("lifecycle", true)).Kind == DeployOutcomeKind.Ok)
                    return SyncLocked(project);
            }
        }
        return snapshot;
    }

    /// <summary>
    /// Creates one backlog bug card per failed Production run. Each run is
    /// handled once per process: storage is read only when a run first fails,
    /// and the stable card id keeps a restart from creating a duplicate.
    /// </summary>
    private void CreateProductionFailureCards(string project, IReadOnlyList<DeployEnvironment> environments,
        IReadOnlyList<ReleaseRecord> records, IReadOnlyList<DeploymentRecord> runs)
    {
        var production = environments.Where(item => item.Kind == EnvironmentKinds.Production)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var pending = runs.Where(item => (item.State == DeploymentStates.Failed
                    || (item.State == DeploymentStates.Cancelled && item.InterventionDecision == "abort"
                        && item.FailedStep is not null))
                && production.Contains(item.EnvironmentId)
                && !_failureCardsHandled.Contains(project + "/" + item.Id))
            .ToArray();
        if (pending.Length == 0) return;
        foreach (var run in pending) _failureCardsHandled.Add(project + "/" + run.Id);
        var watchPath = _scanner.GetWatchPaths()
            .FirstOrDefault(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;
        if (_mutations is null || watchPath is null)
        {
            _logger.LogWarning("Cannot create Production failure cards for {Project} deployments {DeploymentIds}: "
                + "task mutations or the project watch path are unavailable", project,
                string.Join(", ", pending.Select(item => item.Id)));
            return;
        }
        // The published task index can lag a new card. Read storage once so a
        // restart cannot mint a duplicate for a run that already has its card.
        var existing = _scanner.ScanAllJobsRaw()
            .Where(item => WatchPathComparison.PathsEqual(item.WatchPath, watchPath))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var run in pending.Where(item => !existing.Contains(FailureCardId(item.Id))))
        {
            var release = records.FirstOrDefault(item => item.Release.Id == run.ReleaseId)?.Release;
            if (release is null) continue;
            var cards = string.Join(", ", release.CardKeys);
            var excerpt = string.Join("\n", _store.ReadLog(project, run.Id).TakeLast(5)
                .Select(item => $"- {item.Step}: {item.Message}"));
            try
            {
                var cardId = _mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
                {
                    Id = FailureCardId(run.Id),
                    Title = $"Investigate Production deployment of release {release.Version}",
                    PromptMarkdown = $"# Production deployment failed\n\nRelease: {release.Version} (`{release.Id}`)"
                        + $"\nDeployment: `{run.Id}`\nIncluded cards: {cards}\n\nRecent deployment log:\n{excerpt}\n",
                    WatchPath = watchPath,
                    TargetState = TaskStates.Backlog,
                    TaskType = TaskTypes.Bug,
                    Tags = ["production-deployment"],
                    CreationSource = "deployment",
                    CreatedBy = "system",
                });
                if (cardId is null)
                    _logger.LogWarning("Could not create Production failure card for deployment {DeploymentId}", run.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A transient storage failure retries on the next reconciliation.
                _failureCardsHandled.Remove(project + "/" + run.Id);
                _logger.LogWarning(ex, "Could not create Production failure card for deployment {DeploymentId}", run.Id);
            }
        }
    }

    private static string FailureCardId(string deploymentId) => "production-deploy-failed-" + deploymentId;

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
            run.Trigger, run.RequestedBy, run.CreatedAtUtc, run.InterventionExpiresAtUtc, run.FailedStep,
            run.InterventionDecidedBy, run.InterventionDecision) { Tenant = run.Tenant };
}
