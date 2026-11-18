using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>Requests the API saw arrive through an edge, a CDN and a browser. Timestamps only.</summary>
public sealed record StudioIngressSnapshot(DateTime? EdgeSeenAt, DateTime? CdnSeenAt, DateTime? BrowserSeenAt);

/// <summary>What the API knows about the Orchestrator Engine from its readiness contact.</summary>
public sealed record StudioEngineFact(string State, bool Required, DateTime? LastContact, string? EngineId);

/// <summary>A data location the Studio server keeps, before the projection gives it its machine id.</summary>
public sealed record StudioDataLocationFact(string Role, string Label, long? SizeBytes, bool BackupCovered,
    bool? GitRemoteCovered, DateTime? MeasuredAt, string? Detail, StructureMountFact? Mount);

/// <summary>
/// AGT-3954: the Studio server as the API observes it from inside: the place
/// the API process runs, the machine's kernel counters, the components it has
/// evidence of, and the data locations that hold installation state.
/// </summary>
/// <param name="Hostname">The API process's host name; inside a container it is the short container id.</param>
/// <param name="ConfiguredServerMachineId"><c>Structure:ServerMachineId</c>, when the operator names the machine.</param>
public sealed record StudioSelfFacts(
    HostMachineDto Machine,
    string Hostname,
    string? ConfiguredServerMachineId,
    string Version,
    DateTime StartedAt,
    HostTelemetrySnapshotDto? Telemetry,
    IReadOnlyList<StudioDataLocationFact> DataLocations,
    StudioIngressSnapshot Ingress,
    StudioEngineFact Engine);

/// <summary>
/// Records which kinds of front door a request came through. One volatile
/// write per request; nothing is stored about the request itself.
/// </summary>
public sealed class StudioIngressObserver(TimeProvider clock)
{
    private long _edge, _cdn, _browser;

    public void Observe(HttpRequest request)
    {
        var now = clock.GetUtcNow().UtcTicks;
        var headers = request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Forwarded-Proto"))
            Volatile.Write(ref _edge, now);
        if (headers.ContainsKey("CF-Ray") || headers.ContainsKey("CF-Connecting-IP"))
            Volatile.Write(ref _cdn, now);
        if (headers.TryGetValue("Sec-Fetch-Site", out var site) && site == "same-origin"
            && request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            Volatile.Write(ref _browser, now);
    }

    public StudioIngressSnapshot Snapshot()
    {
        static DateTime? At(long ticks) => ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        return new(At(Volatile.Read(ref _edge)), At(Volatile.Read(ref _cdn)), At(Volatile.Read(ref _browser)));
    }
}

/// <summary>
/// Samples the Studio server for the structure snapshot. Counters are read on
/// each snapshot refresh; directory sizes are slow and refresh at most every
/// <see cref="SizeInterval"/>. Disk measurement goes through
/// <see cref="IHostFilesystemProbe"/> so a test host never measures its own disk.
/// </summary>
public sealed class StudioSelfObserver
{
    public const string ServerMachineIdKey = "Structure:ServerMachineId";
    public static readonly TimeSpan SizeInterval = TimeSpan.FromMinutes(10);

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;
    private readonly IHostFilesystemProbe _probe;
    private readonly ProjectRegistry _projects;
    private readonly StudioIngressObserver _ingress;
    private readonly OrchestrationEngineReadiness _engine;
    private readonly string _version;
    private readonly DateTime _startedAt;
    private readonly Lazy<HostMachineDto> _machine;
    private readonly Func<DateTime, LinuxHostCounterReading?> _counters;
    private readonly object _gate = new();
    private LinuxHostCounterReading? _previous;
    private (long Receive, long Transmit, DateTime At)? _previousNetwork;
    private IReadOnlyList<StudioDataLocationFact> _locations = [];
    private DateTime _locationsAt = DateTime.MinValue;
    private Task? _measuring;

    /// <param name="counters">Kernel counter source; a test host pins it so the machine running the suite is not sampled.</param>
    public StudioSelfObserver(IConfiguration configuration, TimeProvider clock, IHostFilesystemProbe probe,
        ProjectRegistry projects, StudioIngressObserver ingress, OrchestrationEngineReadiness engine,
        StableReleaseIdentity release, Func<DateTime, LinuxHostCounterReading?>? counters = null)
    {
        _counters = counters ?? LinuxHostCounters.Read;
        _configuration = configuration;
        _clock = clock;
        _probe = probe;
        _projects = projects;
        _ingress = ingress;
        _engine = engine;
        _version = release.Version;
        _startedAt = clock.GetUtcNow().UtcDateTime;
        _machine = new(DetectMachine);
    }

    /// <summary>Completes once the directory sizes started by the last <see cref="Observe"/> are in.</summary>
    public Task MeasuredAsync() { lock (_gate) return _measuring ?? Task.CompletedTask; }

    public StudioSelfFacts Observe()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        HostTelemetrySnapshotDto? telemetry;
        IReadOnlyList<StudioDataLocationFact> locations;
        lock (_gate)
        {
            telemetry = Sample(now);
            if (now - _locationsAt >= SizeInterval && _measuring is null)
            {
                _locationsAt = now;
                _measuring = Task.Run(() => MeasureLocations(now)).ContinueWith(measured =>
                {
                    lock (_gate)
                    {
                        if (measured.IsCompletedSuccessfully) _locations = measured.Result;
                        else Diagnostics.SilentCatch.Warn(measured.Exception!, "StudioSelfObserver: data locations unavailable");
                        _measuring = null;
                    }
                }, TaskScheduler.Default);
            }
            locations = _locations;
        }
        return new(_machine.Value, Environment.MachineName,
            _configuration[ServerMachineIdKey] is { Length: > 0 } configured ? configured.Trim() : null,
            _version, _startedAt, telemetry, locations, _ingress.Snapshot(),
            new(_engine.State, _engine.Required, _engine.LastContact?.UtcDateTime, _engine.LastEngineId));
    }

    private HostTelemetrySnapshotDto? Sample(DateTime now)
    {
        var reading = _counters(now);
        if (reading is null) return null;
        var cpu = LinuxHostCounters.CpuPercent(_previous, reading);
        _previous = reading;
        // Inside a container the network namespace is the container's own, not the machine's.
        HostNetworkUsageDto? network = null;
        if (_machine.Value.Kind != HostMachineKinds.Container)
        {
            network = HostNetworkCounters.Rate(reading.Network, now, _previousNetwork);
            _previousNetwork = reading.Network is { } value ? (value.Receive, value.Transmit, now) : null;
        }
        return new HostTelemetrySnapshotDto(now, cpu, reading.Load1, reading.Load5, reading.Load15,
            reading.MemoryTotalBytes is { } total && reading.MemoryAvailableBytes is { } available
                ? Math.Max(0, total - available) : null,
            reading.MemoryTotalBytes, null, null, null, null, reading.CpuCores, 0,
            Filesystems: _locations.Select(location => location.Mount).OfType<StructureMountFact>()
                .GroupBy(mount => mount.Mount, StringComparer.Ordinal).Select(group => group.First())
                .Select(mount => new HostFilesystemUsageDto(mount.Purpose, mount.Mount, mount.FreeBytes, mount.TotalBytes))
                .ToArray(),
            Network: network);
    }

    private IReadOnlyList<StudioDataLocationFact> MeasureLocations(DateTime now)
    {
        var backups = BackupOptions.From(_configuration);
        var candidates = new List<(string Role, string Label, string Path, bool File, string? Detail)>();
        var workspace = Path.GetFullPath(SecretStorePaths.WorkspaceRoot(_configuration));
        candidates.Add(("workspace-repository", "Workspace repository", workspace, false, null));
        var checkouts = backups.Sources.FirstOrDefault(source => source.Projects)?.Path
                        ?? CommonRoot(_projects.List().Where(project => !project.Archived)
                            .Select(project => project.RepositoryPath ?? project.RootPath)
                            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!)));
        if (checkouts is not null) candidates.Add(("project-checkouts", "Project checkouts", checkouts, false, null));
        try { candidates.Add(("application-state", "Application state", LocalStateRoot.Resolve(), false, null)); }
        catch (LocalStateRootUnavailableException ex) { Diagnostics.SilentCatch.Note(ex, "StudioSelfObserver: no local state root to report"); }
        if (backups.SecretKeyFile is { } secretKey)
            candidates.Add(("secrets-key", "Secrets key", secretKey, true,
                "Excluded from backup archives by design; keep its escrow copy."));
        foreach (var source in backups.Sources.Where(source =>
                     source.Name.Contains("caddy", StringComparison.OrdinalIgnoreCase)))
            candidates.Add(("edge-certificates", "Edge certificates", source.Path, false, null));
        foreach (var source in backups.Sources.Where(source => candidates.All(candidate =>
                     !string.Equals(candidate.Path, source.Path, StringComparison.Ordinal))))
            candidates.Add((source.Name, $"Backup source {source.Name}", source.Path, false, null));

        var present = candidates.Where(candidate => candidate.File ? File.Exists(candidate.Path) : Directory.Exists(candidate.Path))
            .GroupBy(candidate => candidate.Role, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var measured = _probe.Measure(present.Select(candidate => (candidate.Role, candidate.Path)),
            present.Where(candidate => !candidate.File).Select(candidate => (candidate.Role, candidate.Path)));
        return present.Select(candidate =>
        {
            var mount = measured.FirstOrDefault(item => item.OwnedBytes?.ContainsKey(candidate.Role) == true)
                        ?? measured.FirstOrDefault(item => item.Purpose == candidate.Role);
            long? size = candidate.File ? new FileInfo(candidate.Path).Length
                : mount?.OwnedBytes?.TryGetValue(candidate.Role, out var bytes) == true ? bytes : null;
            var covered = backups.Configured && candidate.Role != "secrets-key"
                && backups.Sources.Any(source => BackupCollector.Inside(candidate.Path, source.Path));
            bool? remote = candidate.Role is "project-checkouts" or "workspace-repository"
                ? GitRemotes(candidate.Path) : null;
            return new StudioDataLocationFact(candidate.Role, candidate.Label, size, covered, remote, now,
                candidate.Detail,
                mount is null ? null : new StructureMountFact(candidate.Role, mount.Path,
                    Math.Max(0, mount.TotalBytes - mount.FreeBytes), mount.FreeBytes, mount.TotalBytes));
        }).ToArray();
    }

    /// <summary>
    /// True when every checkout at or one or two levels below the location has
    /// an <c>origin</c> remote in its Git config. Reads files only; never runs Git.
    /// </summary>
    private static bool? GitRemotes(string root)
    {
        var configs = new[] { root }.Concat(SafeDirectories(root))
            .Concat(SafeDirectories(root).SelectMany(SafeDirectories))
            .Select(directory => Path.Combine(directory, ".git", "config")).Where(File.Exists).Take(200).ToArray();
        if (configs.Length == 0) return null;
        return configs.All(config =>
        {
            try { return File.ReadLines(config).Any(line => line.Trim().StartsWith("[remote ", StringComparison.Ordinal)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        });
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? CommonRoot(IEnumerable<string> paths)
    {
        string? common = null;
        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (parent is null) return null;
            common = common is null ? parent : Common(common, parent);
            if (common is null) return null;
        }
        return common;

        static string? Common(string left, string right)
        {
            var a = left.Split(Path.DirectorySeparatorChar);
            var b = right.Split(Path.DirectorySeparatorChar);
            var shared = a.Zip(b).TakeWhile(pair => pair.First == pair.Second).Count();
            var value = string.Join(Path.DirectorySeparatorChar, a.Take(shared));
            return value.Length <= 1 ? null : value;
        }
    }

    /// <summary>Same markers an agent host reads, without spawning a process.</summary>
    private static HostMachineDto DetectMachine()
    {
        static string? Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        var evidence = new HostMachineEvidence(Hostname: Environment.MachineName);
        if (OperatingSystem.IsLinux())
            evidence = evidence with
            {
                DockerEnvFile = File.Exists("/.dockerenv"),
                ContainerEnvFile = Read("/run/.containerenv"),
                ContainerVariable = Environment.GetEnvironmentVariable("container"),
                InitCgroup = Read("/proc/1/cgroup"),
                DmiVendor = Read("/sys/class/dmi/id/sys_vendor")?.Trim(),
                DmiProduct = Read("/sys/class/dmi/id/product_name")?.Trim(),
                CpuHypervisorFlag = Read("/proc/cpuinfo")?.Split('\n').Any(line =>
                    line.StartsWith("flags", StringComparison.Ordinal)
                    && line.Contains(" hypervisor", StringComparison.Ordinal)) == true,
                BootId = Read("/proc/sys/kernel/random/boot_id")?.Trim(),
            };
        return HostMachineClassifier.Classify(evidence);
    }
}
