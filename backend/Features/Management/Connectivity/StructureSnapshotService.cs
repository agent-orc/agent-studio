using System.Net.Http.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>Sends a structure delta to the open views. The Host owns the SignalR transport.</summary>
public interface IStructureDeltaPublisher
{
    Task PublishAsync(StructureDelta delta, CancellationToken ct);
}

/// <summary>One read of the structure: the snapshot, or why it cannot be built.</summary>
public sealed record StructureRead(StructureResponse? Response, string? Error);

/// <summary>
/// AGT-3954: the cached operations structure. A background refresh collects
/// the host reports, gate heartbeats, links, release drift and the Studio's
/// own facts every <see cref="RefreshInterval"/>, rebuilds the workspace
/// projection once, and publishes what changed as a <see cref="StructureDelta"/>.
/// Requests never read host facts themselves: they get the cached workspace
/// snapshot, or a scoped projection cached per scope until the facts change.
/// </summary>
public sealed class StructureSnapshotService : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleAfter = RefreshInterval * 3;

    private readonly IConfiguration _configuration;
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceRegistry _workspaces;
    private readonly ProjectEnvironmentService _environments;
    private readonly GitService _git;
    private readonly V1ReviewExecutorRegistry _hosts;
    private readonly LinkSupervisor _links;
    private readonly EnvironmentHealthService _health;
    private readonly HostReleaseDriftWatchdog _releases;
    private readonly OrchestrationEngineReadiness _engine;
    private readonly PullGateStore _gates;
    private readonly TimeProvider _clock;
    private readonly IHttpClientFactory _clients;
    private readonly AttemptAuthorityService _authority;
    private readonly ReviewClaimUnclaimableLog _unclaimable;
    private readonly StudioSelfObserver _self;
    private readonly IStructureDeltaPublisher? _publisher;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<(string? Project, string? Environment), StructureResponse> _scoped = [];
    private Inputs? _inputs;
    private StructureResponse? _current;
    private string? _error;
    private DateTime _refreshedAt = DateTime.MinValue;
    private long _revision;

    /// <param name="RepositoryRoots">Resolved once per refresh: resolving runs Git, which a read request must not.
    /// Declaration files and health reads resolve the same roots, so they are collected here too.</param>
    private sealed record Inputs(InstallationConnectivityManifest? Manifest, StructurePlacementRead Placement,
        StructureHostFacts Hosts, IReadOnlyList<StructureDeploymentFact> Deployments,
        IReadOnlyList<StructureHealthFact> Health, string EngineState, DateTime Now,
        IReadOnlyDictionary<string, string?> RepositoryRoots,
        IReadOnlyDictionary<string, IReadOnlyList<EnvironmentDeclarationFile>?> Declarations,
        IReadOnlyDictionary<string, ProjectEnvironmentHealthResponse?> HealthReads);

    public StructureSnapshotService(IConfiguration configuration, ProjectRegistry projects, WorkspaceRegistry workspaces,
        ProjectEnvironmentService environments, GitService git, V1ReviewExecutorRegistry hosts, LinkSupervisor links,
        EnvironmentHealthService health, HostReleaseDriftWatchdog releases, OrchestrationEngineReadiness engine,
        PullGateStore gates, TimeProvider clock, IHttpClientFactory clients, AttemptAuthorityService authority,
        ReviewClaimUnclaimableLog unclaimable, StudioSelfObserver self, IStructureDeltaPublisher? publisher = null)
    {
        _configuration = configuration;
        _projects = projects;
        _workspaces = workspaces;
        _environments = environments;
        _git = git;
        _hosts = hosts;
        _links = links;
        _health = health;
        _releases = releases;
        _engine = engine;
        _gates = gates;
        _clock = clock;
        _clients = clients;
        _authority = authority;
        _unclaimable = unclaimable;
        _self = self;
        _publisher = publisher;
        // Revisions continue across restarts, so a client never mistakes a new
        // process's snapshot for the one it already holds.
        _revision = clock.GetUtcNow().ToUnixTimeMilliseconds();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _clock);
        do
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            { SilentCatch.Warn(ex, "StructureSnapshotService: refresh failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// The workspace snapshot when no scope is asked for; otherwise the scoped
    /// projection, built once per facts revision from the collected facts. A
    /// read never collects facts itself: a stale snapshot starts a detached
    /// producer refresh and is served as it is, and only before the very first
    /// snapshot does a read wait (bounded) for that refresh.
    /// </summary>
    public async Task<StructureRead> ReadAsync(string? projectId, string? environment, CancellationToken ct)
    {
        Task? pending = null;
        bool first;
        lock (_gate)
        {
            first = _inputs is null;
            if (_clock.GetUtcNow().UtcDateTime - _refreshedAt > StaleAfter) pending = StartDetachedRefresh();
        }
        if (first && pending is not null)
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (TimeoutException ex) { SilentCatch.Note(ex, "StructureSnapshotService: first snapshot still collecting"); }
        }
        lock (_gate)
        {
            if (_inputs is null) return new(null, "Structure facts are still being collected.");
            if (_error is not null) return new(null, _error);
            if (projectId is null && environment is null) return new(_current, null);
            if (_scoped.TryGetValue((projectId, environment), out var cached) && cached.Revision == _revision)
                return new(cached, null);
            var built = Build(_inputs!, projectId, environment) with
            {
                Revision = _revision, GeneratedAt = _current?.GeneratedAt,
            };
            if (_scoped.Count > 64) _scoped.Clear();
            _scoped[(projectId, environment)] = built;
            return new(built, null);
        }
    }

    private Task? _detached;

    /// <summary>Runs the producer refresh outside the read request's Git scope; one at a time.</summary>
    private Task StartDetachedRefresh()
    {
        if (_detached is { IsCompleted: false } running) return running;
        using (AgentStudio.Git.GitReadRequestGuard.Detach())
        using (ExecutionContext.SuppressFlow())
            _detached = Task.Run(async () =>
            {
                try { await RefreshAsync(CancellationToken.None); }
                catch (Exception ex) { SilentCatch.Warn(ex, "StructureSnapshotService: detached refresh failed"); }
            });
        return _detached;
    }

    /// <summary>Collects facts once and publishes the change against the previous snapshot.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            var inputs = await CollectAsync(ct);
            StructureDelta? delta = null;
            lock (_gate)
            {
                _inputs = inputs;
                _refreshedAt = inputs.Now;
                _error = inputs.Manifest is null && ManifestError() is { } error ? error : null;
                if (_error is not null) return;
                var next = Build(inputs, null, null);
                var previous = _current;
                if (previous is not null && StructureIdentity.Same(previous with { Revision = 0, GeneratedAt = null },
                        next with { Revision = 0, GeneratedAt = null }))
                    return;
                _current = next with { Revision = ++_revision, GeneratedAt = inputs.Now };
                if (previous is not null) delta = StructureIdentity.Diff(previous, _current);
            }
            if (delta is { IsEmpty: false } && _publisher is not null)
                await _publisher.PublishAsync(delta, ct);
        }
        finally { _refresh.Release(); }
    }

    private string? ManifestError()
    {
        var path = _configuration[ConnectivityManifestLoader.ManifestPathKey];
        return string.IsNullOrWhiteSpace(path) ? null : "Connectivity manifest is unavailable.";
    }

    private StructureResponse Build(Inputs inputs, string? projectId, string? environment)
    {
        var selected = _projects.List().Where(project => !project.Archived).ToArray();
        var declared = StructureProjection.Build(inputs.Placement.Placement, inputs.Manifest?.InstallationId, selected,
            name => inputs.Declarations.GetValueOrDefault(name), projectId, environment,
            project => inputs.RepositoryRoots.GetValueOrDefault(project.Id),
            _workspaces.List().Where(workspace => projectId is null ||
                    _projects.FindById(projectId)?.WorkspaceId == workspace.Id)
                .SelectMany(workspace => workspace.Systems).ToArray(),
            (project, environmentId, checkId) => inputs.HealthReads.GetValueOrDefault(project.DisplayName)?.Environments
                .FirstOrDefault(item => item.EnvironmentId == environmentId)?.Checks.FirstOrDefault(item => item.Id == checkId)
                is { } check ? new StructureProbe(check.Kind, check.Status) : null);
        var observed = ObservedStructureProjection.Apply(declared, inputs.Hosts, inputs.Deployments, inputs.Health,
            inputs.EngineState, inputs.Now);
        // A scoped view keeps what the declared projection selected, plus what
        // reports place inside those declared machines; other hosts stay out.
        if (projectId is not null || environment is not null)
        {
            var keep = observed.Nodes.Where(node => node.Provenance == "declared").Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            for (var added = true; added;)
            {
                added = false;
                foreach (var node in observed.Nodes.Where(node => node.Provenance != "declared"
                             && node.ParentId is { } parent && parent != "installation" && keep.Contains(parent)))
                    added |= keep.Add(node.Id);
            }
            observed = observed with
            {
                Nodes = observed.Nodes.Where(node => keep.Contains(node.Id)).ToArray(),
                Links = observed.Links.Where(link => keep.Contains(link.From) && keep.Contains(link.To)).ToArray(),
            };
        }
        return observed with { Findings = inputs.Placement.Findings.Count > 0 ? inputs.Placement.Findings : null };
    }

    private async Task<Inputs> CollectAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        InstallationConnectivityManifest? manifest = null;
        var path = _configuration[ConnectivityManifestLoader.ManifestPathKey];
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { manifest = InstallationConnectivityManifest.Parse(await File.ReadAllTextAsync(Path.GetFullPath(path), ct)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { SilentCatch.Warn(ex, "StructureSnapshotService: connectivity manifest unavailable"); }
        }
        var placement = StructurePlacementLoader.Read(_configuration, manifest);
        IReadOnlyList<RunnerCapabilitySnapshotDto> hosts = _hosts.ListCapabilitySnapshots()
            .Select(snapshot => HeldCapabilityProjection.Merge(_hosts.HeldCapabilities(snapshot.RunnerId),
                _unclaimable.ForExecutor(snapshot.RunnerId)) is { Count: > 0 } held
                ? snapshot with { HeldCapabilities = held } : snapshot)
            .ToArray();
        if (TaskServerPlaneProxy.IsConfigured(_configuration))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                hosts = await _clients.CreateClient(TaskServerPlaneProxy.ClientName)
                    .GetFromJsonAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts", deadline.Token) ?? [];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                SilentCatch.Warn(ex, "StructureSnapshotService: remote host reports unavailable");
                hosts = _inputs?.Hosts.Hosts ?? [];
            }
        }
        var running = hosts.Select(host => host.RunnerId).Distinct(StringComparer.Ordinal).ToDictionary(id => id,
            id => (IReadOnlyList<string>)_authority.ListActiveAttemptsOfExecutor(id).Select(attempt => attempt.TaskKey)
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var refused = hosts.Select(host => host.RunnerId).Distinct(StringComparer.Ordinal).ToDictionary(id => id,
            id => (IReadOnlyList<string>)_unclaimable.ForExecutor(id).Select(item => item.TaskKey)
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var selected = _projects.List().Where(project => !project.Archived).ToArray();
        var roots = selected.ToDictionary(project => project.Id,
            project => _git.ResolveRepoRootForProject(project.DisplayName), StringComparer.Ordinal);
        var declarations = selected.GroupBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => _environments.DeclarationFiles(group.Key), StringComparer.OrdinalIgnoreCase);
        var healthReads = selected.GroupBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => _health.Read(group.Key), StringComparer.OrdinalIgnoreCase);
        var deployments = selected.SelectMany(project =>
            _environments.Read(project.DisplayName)?.Environments.Select(view =>
                new StructureDeploymentFact(project.Id, view)) ?? []).ToArray();
        var health = _health.Overview().Environments.Join(selected,
            view => view.Project, project => project.DisplayName,
            (view, project) => new StructureHealthFact(project.Id, view)).ToArray();
        return new(manifest, placement,
            new StructureHostFacts(hosts, _gates.Hosts(), _links.Snapshot(), _releases.Current, _self.Observe(),
                running, refused),
            deployments, health, _engine.State, now, roots, declarations, healthReads);
    }
}
