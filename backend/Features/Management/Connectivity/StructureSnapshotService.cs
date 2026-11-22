using System.Collections.Concurrent;
using System.Net.Http.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Host;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>One read of the cached structure: the projection, or the reason it is unavailable.</summary>
public sealed record StructureRead(StructureResponse? Response, string? Error = null);

/// <summary>Everything one refresh reads from the running installation, read once for all scopes.</summary>
public sealed record StructureObservedInputs(IReadOnlyList<RunnerCapabilitySnapshotDto> Hosts,
    IReadOnlyList<RunnerLinkResource> Links, HostReleaseDriftSnapshot? Releases, string EngineState,
    IReadOnlyList<PullGateHost> Gates, StructureObservedContext Context);

/// <summary>
/// The structure projection, cached and kept current in the background
/// (AGT-3954). A request never rebuilds it per client and never asks a host:
/// host reports, the declared part and the data locations are read on this
/// service's own cadence, and only while a view reads or subscribes. Each
/// refresh compares the new projection with the previous one and publishes
/// the changed nodes and links as one small <see cref="StructureDelta"/>.
/// </summary>
public sealed class StructureSnapshotService : BackgroundService
{
    /// <summary>Longest age of a projection a read returns without rebuilding it.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    /// <summary>Declarations change by edit or deploy, not by the second.</summary>
    public static readonly TimeSpan DeclaredMaxAge = TimeSpan.FromSeconds(30);
    /// <summary>A scope nobody read or subscribed to for this long stops refreshing.</summary>
    public static readonly TimeSpan DemandWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SelfSampleInterval = TimeSpan.FromSeconds(15);

    private readonly IConfiguration _configuration;
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceRegistry _workspaces;
    private readonly ProjectEnvironmentService _environments;
    private readonly GitService _git;
    private readonly V1ReviewExecutorRegistry _hosts;
    private readonly AttemptAuthorityService? _authority;
    private readonly ReviewClaimUnclaimableLog? _unclaimable;
    private readonly LinkSupervisor _links;
    private readonly EnvironmentHealthService _health;
    private readonly HostReleaseDriftWatchdog _releases;
    private readonly OrchestrationEngineReadiness _engine;
    private readonly PullGateStore _gates;
    private readonly TimeProvider _clock;
    private readonly IHttpClientFactory _clients;
    private readonly StructurePlacementSource _placement;
    private readonly StudioSelfObserver _self;
    private readonly StructureDataLocationService _data;
    private readonly ILogger<StructureSnapshotService> _logger;
    private readonly ConcurrentDictionary<(string Project, string Environment), Channel> _channels = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private (StructureObservedInputs Inputs, DateTime At)? _inputs;
    private (StudioSelfObservation Value, DateTime At)? _selfSample;
    private int _measuring;

    public StructureSnapshotService(IConfiguration configuration, ProjectRegistry projects, WorkspaceRegistry workspaces,
        ProjectEnvironmentService environments, GitService git, V1ReviewExecutorRegistry hosts, LinkSupervisor links,
        EnvironmentHealthService health, HostReleaseDriftWatchdog releases, OrchestrationEngineReadiness engine,
        PullGateStore gates, TimeProvider clock, IHttpClientFactory clients, StructurePlacementSource placement,
        StudioSelfObserver self, StructureDataLocationService data, ILogger<StructureSnapshotService> logger,
        AttemptAuthorityService? authority = null, ReviewClaimUnclaimableLog? unclaimable = null)
    {
        _configuration = configuration; _projects = projects; _workspaces = workspaces; _environments = environments;
        _git = git; _hosts = hosts; _links = links; _health = health; _releases = releases; _engine = engine;
        _gates = gates; _clock = clock; _clients = clients; _placement = placement; _self = self; _data = data;
        _logger = logger; _authority = authority; _unclaimable = unclaimable;
    }

    /// <summary>Raised on the refresh worker for every scope whose projection changed.</summary>
    public event Action<StructureDelta>? DeltaPublished;

    public static string ScopeName(string? projectId) => projectId is null ? "workspace" : $"project:{projectId}";

    /// <summary>Hub group of one scope and environment filter.</summary>
    public static string GroupName(string? projectId, string? environment)
        => $"structure:{ScopeName(projectId).ToLowerInvariant()}:{environment ?? ""}";

    /// <summary>Keeps a scope refreshing; a subscribed view calls this through its reads.</summary>
    public long Touch(string? projectId, string? environment)
    {
        var channel = ChannelFor(projectId, environment);
        channel.DemandUntil = _clock.GetUtcNow().UtcDateTime + DemandWindow;
        return channel.Current?.Revision ?? 0;
    }

    /// <summary>The cached projection of a scope; rebuilt here only when no refresh kept it current.</summary>
    public async Task<StructureRead> ReadAsync(string? projectId, string? environment, CancellationToken cancellationToken)
    {
        var channel = ChannelFor(projectId, environment);
        var now = _clock.GetUtcNow().UtcDateTime;
        channel.DemandUntil = now + DemandWindow;
        if (channel.Current is { } current && channel.Error is null && now - channel.BuiltAt <= RefreshInterval + RefreshInterval)
            return new(current);
        await RefreshAsync(channel, publish: true, cancellationToken);
        return channel.Error is { } error ? new(null, error) : new(channel.Current);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshDueAsync(stoppingToken);
    }

    /// <summary>One background pass: every scope in demand is rebuilt once and its delta published.</summary>
    public async Task RefreshDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var (key, channel) in _channels)
        {
            if (channel.DemandUntil < now)
            {
                // Forget idle scopes so a renamed project or environment does not stay cached.
                if (now - channel.DemandUntil > TimeSpan.FromMinutes(10)) _channels.TryRemove(key, out _);
                continue;
            }
            try { await RefreshAsync(channel, publish: true, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { _logger.LogWarning(exception, "structure-refresh-failed scope={Scope}", key); }
        }
    }

    private Channel ChannelFor(string? projectId, string? environment)
        => _channels.GetOrAdd((projectId ?? "", environment ?? ""), key => new Channel(
            key.Project.Length == 0 ? null : key.Project, key.Environment.Length == 0 ? null : key.Environment,
            // A revision that keeps growing across restarts never matches a client's older one by chance.
            _clock.GetUtcNow().ToUnixTimeMilliseconds()));

    private async Task RefreshAsync(Channel channel, bool publish, CancellationToken cancellationToken)
    {
        await channel.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            // A caller that waited behind another build takes that build.
            if (channel.Current is not null && channel.Error is null && now - channel.BuiltAt < TimeSpan.FromSeconds(1)) return;
            if (channel.Declared is null || now - channel.DeclaredAt > DeclaredMaxAge)
            {
                var (declared, error) = BuildDeclared(channel.ProjectId, channel.Environment);
                channel.Error = error;
                if (declared is null) { channel.BuiltAt = now; return; }
                channel.Declared = declared;
                channel.DeclaredAt = now;
            }
            var inputs = await InputsAsync(cancellationToken);
            var selected = _projects.List().Where(project => !project.Archived).ToArray();
            var deployments = selected.SelectMany(project =>
                _environments.Read(project.DisplayName)?.Environments.Select(view =>
                    new StructureDeploymentFact(project.Id, view)) ?? []).ToArray();
            var health = _health.Overview().Environments.Join(selected,
                view => view.Project, project => project.DisplayName,
                (view, project) => new StructureHealthFact(project.Id, view)).ToArray();
            var whole = channel.ProjectId is null && channel.Environment is null;
            var built = ObservedStructureProjection.Apply(channel.Declared, inputs.Hosts, inputs.Links, deployments,
                health, inputs.Releases, inputs.EngineState, now, inputs.Gates,
                inputs.Context with { IncludeObservedHosts = whole });
            var previous = channel.Current;
            var revision = previous?.Revision ?? channel.Seed;
            var candidate = built with { Revision = revision, GeneratedAt = now };
            var delta = previous is null ? null
                : StructureDelta.Between(ScopeName(channel.ProjectId), channel.Environment, previous, candidate);
            if (delta is { IsEmpty: true })
            {
                channel.BuiltAt = now;
                return;
            }
            channel.Current = candidate with { Revision = revision + 1 };
            channel.BuiltAt = now;
            if (publish && delta is not null)
                DeltaPublished?.Invoke(delta with { Revision = revision + 1 });
        }
        finally { channel.Gate.Release(); }
    }

    private (StructureResponse? Declared, string? Error) BuildDeclared(string? projectId, string? environment)
    {
        InstallationConnectivityManifest? manifest = null;
        var path = _configuration[ConnectivityManifestLoader.ManifestPathKey];
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { manifest = InstallationConnectivityManifest.Parse(File.ReadAllText(Path.GetFullPath(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { return (null, "Connectivity manifest is unavailable."); }
        }
        var placement = _placement.Read();
        if (placement.Error is { } placementError) return (null, placementError);
        var selected = _projects.List().Where(p => !p.Archived).ToArray();
        return (StructureProjection.Build(manifest, selected, _environments.DeclarationFiles, projectId, environment,
            project => _git.ResolveRepoRootForProject(project.DisplayName),
            _workspaces.List().Where(workspace => projectId is null ||
                _projects.FindById(projectId)?.WorkspaceId == workspace.Id)
                .SelectMany(workspace => workspace.Systems).ToArray(),
            ManagedServiceProbes(_health), placement.Placement), null);
    }

    /// <summary>Host reports and the other observations, read at most once per refresh interval for all scopes.</summary>
    private async Task<StructureObservedInputs> InputsAsync(CancellationToken cancellationToken)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (_inputs is { } cached && now - cached.At < RefreshInterval - TimeSpan.FromSeconds(1)) return cached.Inputs;
            var hosts = await HostsAsync(cancellationToken);
            Dictionary<string, IReadOnlyList<string>>? running = null;
            if (_authority is not null && !TaskServerPlaneProxy.IsConfigured(_configuration))
                running = hosts.Select(host => host.RunnerId).Distinct(StringComparer.Ordinal).ToDictionary(id => id,
                    id => (IReadOnlyList<string>)_authority.ListActiveAttemptsOfExecutor(id).Select(attempt => attempt.TaskKey)
                        .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            if (_selfSample is not { } sample || now - sample.At >= SelfSampleInterval)
                _selfSample = sample = (_self.Observe(), now);
            // Sizes are walked off the refresh path; a walk in progress is not started twice.
            if (Interlocked.CompareExchange(ref _measuring, 1, 0) == 0)
                _ = Task.Run(() =>
                {
                    try { _data.Refresh(sample.Value.MachineId, CancellationToken.None); }
                    finally { Interlocked.Exchange(ref _measuring, 0); }
                }, CancellationToken.None);
            var inputs = new StructureObservedInputs(hosts, _links.Snapshot(), _releases.Current, _engine.State,
                _gates.Hosts(), new(sample.Value, _data.Current, running));
            _inputs = (inputs, now);
            return inputs;
        }
        finally { _inputGate.Release(); }
    }

    private async Task<IReadOnlyList<RunnerCapabilitySnapshotDto>> HostsAsync(CancellationToken cancellationToken)
    {
        if (TaskServerPlaneProxy.IsConfigured(_configuration))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return await _clients.CreateClient(TaskServerPlaneProxy.ClientName)
                    .GetFromJsonAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts",
                        deadline.Token) ?? [];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                SilentCatch.Warn(ex, "StructureProjection: remote host reports unavailable");
                // The last reports stay visible and turn stale by their own timestamps.
                return _inputs?.Inputs.Hosts ?? [];
            }
        }
        var snapshots = _hosts.ListCapabilitySnapshots();
        if (_authority is null || _unclaimable is null) return snapshots;
        var pending = _authority.ListPendingReviewAttempts().Select(attempt => attempt.AttemptId)
            .ToHashSet(StringComparer.Ordinal);
        return snapshots.Select(snapshot => HeldCapabilityProjection.Merge(
                _hosts.HeldCapabilities(snapshot.RunnerId), _unclaimable.ForExecutor(snapshot.RunnerId),
                _hosts.RegisteredReviewCapabilities(snapshot.RunnerId, snapshot.InstanceId), pending) is { Count: > 0 } held
            ? snapshot with { HeldCapabilities = held } : snapshot).ToArray();
    }

    private static Func<ProjectRecord, string, string, StructureProbe?> ManagedServiceProbes(EnvironmentHealthService health)
    {
        var read = new Dictionary<string, ProjectEnvironmentHealthResponse?>(StringComparer.OrdinalIgnoreCase);
        return (project, environment, checkId) =>
        {
            if (!read.TryGetValue(project.DisplayName, out var response))
                read[project.DisplayName] = response = health.Read(project.DisplayName);
            var check = response?.Environments.FirstOrDefault(item => item.EnvironmentId == environment)?
                .Checks.FirstOrDefault(item => item.Id == checkId);
            return check is null ? null : new StructureProbe(check.Kind, check.Status);
        };
    }

    private sealed class Channel(string? projectId, string? environment, long seed)
    {
        public string? ProjectId { get; } = projectId;
        public string? Environment { get; } = environment;
        public long Seed { get; } = seed;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public StructureResponse? Declared;
        public DateTime DeclaredAt;
        public volatile StructureResponse? Current;
        public DateTime BuiltAt;
        public DateTime DemandUntil;
        public volatile string? Error;
    }
}
