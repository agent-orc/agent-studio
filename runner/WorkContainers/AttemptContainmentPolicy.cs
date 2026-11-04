using System.Text.Json;

namespace AgentRunner.WorkContainers;

public enum ContainmentVerdict { Process, Container, Refuse }

/// <summary>What the host proves before it runs an attempt (SM-11).</summary>
public sealed record AttemptContainmentFacts(
    bool IsWindows,
    bool IsLinux,
    WorkContainerMode Mode,
    bool CanaryGo,
    bool LauncherInstalled,
    bool ServiceUserHasRuntimeAccess,
    bool RoleImagePinned,
    bool ZonePolicyReady,
    bool NetworkInWorkZone);

/// <summary>
/// <see cref="Enforced"/> means the host has passed its canary and contained
/// mode is the default: the containment capability becomes a claim
/// requirement, so a <see cref="ContainmentVerdict.Refuse"/> stops claims on
/// the Task Server instead of falling back to the process executor.
/// </summary>
public sealed record AttemptContainmentDecision(ContainmentVerdict Verdict, bool Enforced, string Reason);

/// <summary>
/// SM-11 (security-model Dossier, decision SM-D6 option H2): on Linux the
/// work-container executor is the default once this host's DP-S3 canary
/// verdict is go. Before that the process executor keeps running, as it does
/// after an explicit operator rollback (<c>off</c> or <c>shadow</c>). After it,
/// a missing prerequisite refuses work: the service user may not reach the
/// container runtime except through the narrow launcher, the role image must
/// be digest pinned, and the applied work-zone egress policy must cover the
/// container network. Windows keeps its Job Object process executor.
/// </summary>
public static class AttemptContainmentPolicy
{
    public static AttemptContainmentDecision Decide(AttemptContainmentFacts facts)
    {
        if (facts.IsWindows)
            return Process("windows-process-executor: Job Object bounded process tree; Docker not required.");
        if (!facts.IsLinux)
            return Process("container-executor-linux-only.");
        switch (facts.Mode)
        {
            case WorkContainerMode.Off:
                return Process("container-mode-off: operator rollback to the process executor.");
            case WorkContainerMode.Shadow:
                return Process("container-shadow: process executor runs; container plan recorded.");
            case WorkContainerMode.Canary:
                return facts.RoleImagePinned
                    ? new(ContainmentVerdict.Container, false, "container-canary.")
                    : Process("role-image-unpinned: canary falls back to the process executor.");
        }
        if (!facts.CanaryGo)
            return Process("canary-verdict-missing: the container executor becomes the default after this host records a go verdict (work-container proof).");
        if (facts.ServiceUserHasRuntimeAccess)
            return Refuse("runtime-group-member: the host service user can reach the container runtime directly; remove it from the runtime group and use the launcher.");
        if (!facts.LauncherInstalled)
            return Refuse("launcher-missing: the narrow work-container launcher socket is not available.");
        if (!facts.RoleImagePinned)
            return Refuse("role-image-unpinned: no digest-pinned role image for this attempt.");
        if (!facts.ZonePolicyReady)
            return Refuse("zone-policy-unavailable: the work-zone egress policy is not applied and verified on this host.");
        if (!facts.NetworkInWorkZone)
            return Refuse("network-outside-work-zone: the work container network is not a network of the applied work zone.");
        return new(ContainmentVerdict.Container, true, "contained-default: fresh container per attempt with its own UID.");
    }

    private static AttemptContainmentDecision Process(string reason) => new(ContainmentVerdict.Process, false, reason);

    private static AttemptContainmentDecision Refuse(string reason) => new(ContainmentVerdict.Refuse, true, reason);
}

/// <summary>The host's go/no-go record from <c>agent-host work-container proof</c>.</summary>
public static class WorkContainerCanaryVerdict
{
    public const string FileName = "rollout-verdict.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record Entry(bool Go, string HostId, DateTime RecordedAtUtc, IReadOnlyList<RolloutCheck> Checks);

    public static void Record(string stateDirectory, RolloutVerdict verdict, string hostId, DateTime recordedAtUtc)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = System.IO.Path.Combine(stateDirectory, FileName);
        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(verdict.Go, hostId, recordedAtUtc, verdict.Checks), Json));
        File.Move(temporary, path, true);
    }

    /// <summary>An absent, unreadable or failing record is never go.</summary>
    public static bool IsGo(string stateDirectory)
    {
        try
        {
            var path = System.IO.Path.Combine(stateDirectory, FileName);
            return File.Exists(path)
                && JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json) is { Go: true } record
                && record.Checks.Count > 0 && record.Checks.All(check => check.Passed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}

/// <summary>Reads the containment facts from this host. Every read is local and cheap.</summary>
public static class AttemptContainmentHost
{
    public static AttemptContainmentFacts Gather(
        WorkContainerSettings settings, string role, string? zonePolicyStatePath, TimeProvider clock)
    {
        string? image = null;
        try { image = RoleImageManifest.TryLoad(settings.ImageManifestPath)?.Resolve(role); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            image = null;
        }
        var zonePath = string.IsNullOrWhiteSpace(zonePolicyStatePath) ? ZonePolicyStateStore.DefaultPath : zonePolicyStatePath;
        var zone = ZonePolicyCapability.Describe(zonePath, ZoneKinds.Work, clock, ZonePolicyStateStore.CurrentBootId());
        return new AttemptContainmentFacts(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            settings.Mode,
            WorkContainerCanaryVerdict.IsGo(settings.StateDirectory),
            LauncherInstalled(settings.Launcher),
            WorkContainerRuntimeAccess.CurrentUserCanReachRuntime(),
            RoleImageManifest.IsPinned(image),
            zone is { Status: "ready" },
            WorkZoneNetworks(zonePath).Contains(settings.Network));
    }

    /// <summary>The networks of the applied work zone, or none when the state is unreadable.</summary>
    public static IReadOnlySet<string> WorkZoneNetworks(string zonePolicyStatePath)
        => ZonePolicyStateStore.TryRead(zonePolicyStatePath) is { State: { } state }
            ? state.Zones.Where(zone => zone.Kind == ZoneKinds.Work).SelectMany(zone => zone.Networks)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static bool LauncherInstalled(string? launcher)
    {
        if (string.IsNullOrWhiteSpace(launcher) || !File.Exists(launcher)) return false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return true;
        // The socket is root-owned and group-restricted (0660); a socket every
        // local user may connect to is already a refusal.
        return (File.GetUnixFileMode(launcher) & UnixFileMode.OtherWrite) == 0;
    }
}

/// <summary>
/// Whether the current process can drive a container runtime without the
/// launcher: root, a member of the <c>docker</c> group, or the owner of a
/// rootless Docker socket. Any of these is root-equivalent for an agent.
/// </summary>
public static class WorkContainerRuntimeAccess
{
    public static readonly string[] RuntimeGroups = ["docker"];

    public static bool CurrentUserCanReachRuntime()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (Environment.IsPrivilegedProcess) return true;
        try
        {
            var groups = File.Exists("/proc/self/status")
                ? File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("Groups:", StringComparison.Ordinal))
                : null;
            var runtime = File.Exists("/etc/group") ? File.ReadAllText("/etc/group") : "";
            if (HasRuntimeGroup(groups ?? "", runtime)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtimeDirectory) && File.Exists(System.IO.Path.Combine(runtimeDirectory, "docker.sock"));
    }

    /// <summary>Matches the <c>Groups:</c> line of <c>/proc/self/status</c> against the runtime groups in <c>/etc/group</c>.</summary>
    internal static bool HasRuntimeGroup(string statusGroupsLine, string etcGroup)
    {
        var mine = statusGroupsLine.Replace("Groups:", "", StringComparison.Ordinal)
            .Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var line in etcGroup.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(':');
            if (fields.Length >= 3 && RuntimeGroups.Contains(fields[0], StringComparer.Ordinal) && mine.Contains(fields[2].Trim()))
                return true;
        }
        return false;
    }
}
