using System.Globalization;

namespace AgentRunner.WorkContainers;

/// <summary>
/// Rollout switch for the DP-S3 Linux work container executor. <c>Off</c> keeps
/// today's process and cgroup executor. <c>Shadow</c> keeps that executor and
/// records the container plan and admission verdict next to each attempt.
/// <c>Canary</c> lets the host agent run claimed work in a fresh container.
/// </summary>
public enum WorkContainerMode { Off, Shadow, Canary }

/// <summary>
/// Host-local executor configuration. The CPU and memory ceilings are the
/// operator-owned host profile ceiling (AGT-3019 safety floor); the per-attempt
/// grant comes from the versioned host allocation and may never exceed them.
/// </summary>
public sealed record WorkContainerSettings(
    WorkContainerMode Mode,
    string Runtime,
    string ImageManifestPath,
    string StateDirectory,
    int ProfileMaxHalfCores,
    long ProfileMaxMemoryBytes,
    long MemoryBytesPerHalfCore,
    int CacheVersion,
    string Network)
{
    public const string ModeVariable = "RUNNER_WORK_CONTAINER_MODE";
    public const string RuntimeVariable = "RUNNER_WORK_CONTAINER_RUNTIME";
    public const string ImageManifestVariable = "RUNNER_WORK_CONTAINER_IMAGES";
    public const string StateDirectoryVariable = "RUNNER_WORK_CONTAINER_STATE_DIR";
    public const string MaxCpusVariable = "RUNNER_WORK_CONTAINER_MAX_CPUS";
    public const string MaxMemoryVariable = "RUNNER_WORK_CONTAINER_MAX_MEMORY_MB";
    public const string MemoryPerHalfCoreVariable = "RUNNER_WORK_CONTAINER_MEMORY_MB_PER_HALF_CORE";
    public const string CacheVersionVariable = "RUNNER_WORK_CONTAINER_CACHE_VERSION";
    public const string NetworkVariable = "RUNNER_WORK_CONTAINER_NETWORK";

    /// <summary>AGT-3019 keeps two cores per host for the system and the host agent.</summary>
    public const int HostReserveHalfCores = 4;

    public const long DefaultMemoryMegabytesPerHalfCore = 2048;
    public const string DefaultImageManifestPath = "/etc/agent-host/work-container-images.json";

    public static WorkContainerSettings FromEnvironment(
        Func<string, string?> read, int hostCores, long hostMemoryBytes, string defaultStateDirectory)
    {
        var mode = (read(ModeVariable) ?? "off").Trim().ToLowerInvariant() switch
        {
            "" or "off" or "0" => WorkContainerMode.Off,
            "shadow" => WorkContainerMode.Shadow,
            "canary" => WorkContainerMode.Canary,
            var other => throw new InvalidOperationException($"{ModeVariable} must be off, shadow or canary, not '{other}'."),
        };
        var runtime = (read(RuntimeVariable) ?? "docker").Trim();
        if (runtime is not ("docker" or "podman"))
            throw new InvalidOperationException($"{RuntimeVariable} must be docker or podman.");

        // Default host ceiling: every core minus the AGT-3019 host reserve.
        var defaultHalfCores = Math.Max(1, Math.Max(1, hostCores) * 2 - HostReserveHalfCores);
        var maxHalfCores = ReadDecimal(read, MaxCpusVariable) is { } cpus
            ? (int)Math.Floor(cpus * 2)
            : defaultHalfCores;
        if (maxHalfCores < 1) throw new InvalidOperationException($"{MaxCpusVariable} must allow at least half a core.");
        var maxMemory = ReadDecimal(read, MaxMemoryVariable) is { } memory
            ? (long)(memory * 1024 * 1024)
            : Math.Max(0, hostMemoryBytes - Math.Max(4L << 30, hostMemoryBytes * 15 / 100));
        var perHalfCore = (long)((ReadDecimal(read, MemoryPerHalfCoreVariable) ?? DefaultMemoryMegabytesPerHalfCore) * 1024 * 1024);
        if (perHalfCore < 256L << 20)
            throw new InvalidOperationException($"{MemoryPerHalfCoreVariable} must be at least 256.");
        var cacheVersion = (int)(ReadDecimal(read, CacheVersionVariable) ?? 1);
        if (cacheVersion < 1) throw new InvalidOperationException($"{CacheVersionVariable} must be positive.");
        var network = (read(NetworkVariable) ?? "bridge").Trim();
        if (network.Length == 0 || network is "host" || network.StartsWith("container:", StringComparison.Ordinal))
            throw new InvalidOperationException($"{NetworkVariable} may not share the host or another container namespace.");

        return new WorkContainerSettings(
            mode,
            runtime,
            read(ImageManifestVariable) is { Length: > 0 } manifest ? manifest : DefaultImageManifestPath,
            read(StateDirectoryVariable) is { Length: > 0 } state ? state : defaultStateDirectory,
            maxHalfCores,
            maxMemory,
            perHalfCore,
            cacheVersion,
            network);
    }

    /// <summary>
    /// Physical host cores. <see cref="Environment.ProcessorCount"/> honours the
    /// caller's own cgroup quota, but work containers run beside the host agent,
    /// not inside its cgroup, so the host ceiling comes from the online CPU set.
    /// </summary>
    public static int HostCores()
    {
        const string online = "/sys/devices/system/cpu/online";
        if (!OperatingSystem.IsLinux() || !File.Exists(online)) return Environment.ProcessorCount;
        return CountCpuList(File.ReadAllText(online)) is > 0 and var count ? count : Environment.ProcessorCount;
    }

    /// <summary>Counts a kernel CPU list such as <c>0-5,8,10-11</c>.</summary>
    internal static int CountCpuList(string list)
    {
        var count = 0;
        foreach (var part in list.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (!int.TryParse(range[0], out var first)) return 0;
            var last = first;
            if (range.Length == 2 && !int.TryParse(range[1], out last)) return 0;
            count += last - first + 1;
        }
        return count;
    }

    private static decimal? ReadDecimal(Func<string, string?> read, string name)
    {
        var raw = read(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"{name} must be a positive number.");
    }
}
