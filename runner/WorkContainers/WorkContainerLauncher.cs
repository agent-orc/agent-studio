using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AgentRunner.WorkContainers;

/// <summary>Root-controlled launch prerequisites and authenticated attempt or daemon authority.</summary>
public sealed record WorkContainerLauncherContext(IReadOnlySet<string> WorkZoneNetworks)
{
    public WorkContainerAttemptCapability? AttemptCapability { get; init; }
    public bool DaemonCapability { get; init; }
    internal WorkContainerLimits? ReservedLimits { get; init; }
    internal bool ContainerCreated { get; init; }
    internal bool StartRequested { get; init; }
    internal bool PreparationPending { get; init; }
    public RoleImageManifest ApprovedImages { get; init; } = RoleImageManifest.Empty();
    public IReadOnlySet<string> BindSources { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> Volumes { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// SM-11 narrow launcher. The host service user is not in the
/// container-runtime group; it reaches Docker or Podman only through the
/// root-owned <c>agent-work-launcher</c> wrapper, which runs this policy
/// before the runtime. Every call the work-container executor makes has
/// exactly one admitted shape. A refusal names the first violation; anything
/// unknown is refused.
/// </summary>
public static class WorkContainerLauncherPolicy
{
    private static readonly Regex AttemptName = new("^agent-work-[a-z0-9][a-z0-9._-]{0,120}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CacheName = new("^agent-cache-(npm|nuget|repo)-[a-z0-9][a-z0-9._-]{0,127}-v[0-9]{1,9}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LabelPair = new("^agent-studio\\.[a-z0-9.-]{1,64}=[^\\n]{0,512}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LabelFilter = new("^label=agent-studio\\.[a-z0-9.-]{1,64}=[^\\n]{0,512}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> CreateValueFlags = new(StringComparer.Ordinal)
    {
        "--name", "--user", "--cap-drop", "--security-opt", "--restart", "--cpus", "--memory", "--memory-swap",
        "--entrypoint", "--pids-limit", "--network", "--tmpfs", "--workdir", "--label", "--mount", "--env",
    };

    public static string? Refusal(IReadOnlyList<string> args, WorkContainerLauncherContext context)
    {
        if (ShapeRefusal(args, context) is { } refusal) return refusal;
        if (context.AttemptCapability is { } scope)
        {
            if (scope.Refusal(args) is { } outsideScope) return outsideScope;
            if (!RequiresReservation(args)) return null;
            if (context.ReservedLimits is not { } limits) return "reservation-missing-or-stale.";
            if (args[0] == "start")
                return context.ContainerCreated && !context.StartRequested ? null : "container-not-ready-to-start.";
            if (Materializes(args) && context.ContainerCreated) return "reservation-already-materialized.";
            if (Materializes(args) && context.PreparationPending) return "reservation-preparation-incomplete.";
            if (args[0] == "create" && LimitFlags(limits).Chunk(2).Any(pair => Values(args, pair[0]).Single() != pair[1]))
                return "limits-outside-reservation.";
            return null;
        }
        if (!context.DaemonCapability) return "attempt-capability-required.";
        return args[0] is "version" or "image" or "ps" or "logs" or "rm" or "inspect"
               || args is ["volume", "ls" or "rm", ..]
            ? null : "operation-requires-attempt-capability.";
    }

    internal static bool RequiresReservation(IReadOnlyList<string> args)
        => args is ["pull" or "start", ..] || Materializes(args);

    internal static bool Materializes(IReadOnlyList<string> args)
        => args is ["create" or "run", ..] or ["volume", "create", ..];

    internal static string[] LimitFlags(WorkContainerLimits limits)
    {
        var memory = limits.MemoryBytes.ToString(CultureInfo.InvariantCulture);
        return ["--cpus", limits.Cpus, "--memory", memory, "--memory-swap", memory,
            "--pids-limit", limits.PidsLimit.ToString(CultureInfo.InvariantCulture)];
    }

    private static string? ShapeRefusal(IReadOnlyList<string> args, WorkContainerLauncherContext context)
    {
        if (args.Count == 0) return "empty: no runtime command.";
        var a = args.ToArray();
        return a switch
        {
            ["version", "--format", "{{.Server.Version}}"] => null,
            ["image", "inspect", "--format", "{{.Id}}", var image] => Approved(image, context),
            ["pull", "--quiet", var image] => Approved(image, context),
            ["start", var name] => Attempt(name) ?? ExecutionOnly(name),
            ["wait", var name] => Attempt(name),
            ["logs", "--timestamps", var name] => Attempt(name),
            ["inspect", "--format", "{{json .Mounts}}", var name] => Attempt(name),
            ["rm", "--force", "--volumes", var name] => Attempt(name),
            ["volume", "rm", "--force", var name] => Attempt(name),
            ["volume", "create", .. var rest] => VolumeCreate(rest),
            ["volume", "ls", .. var rest] => Listing(rest),
            ["ps", "--all", "--no-trunc", .. var rest] => Listing(rest),
            ["exec", "--interactive", var name, "/bin/sh", "-c", var script] =>
                Attempt(name) ?? ExecutionOnly(name) ?? (script == WorkContainerLaunchPlanner.InjectTokenScript ? null : "exec-command: only the attempt secret injection may run in a container."),
            ["create", .. var rest] => Create(rest, context),
            ["run", .. var rest] => Prepare(rest, context),
            _ => $"verb-refused: '{a[0]}' is not a work-container executor call.",
        };
    }

    private static string? Approved(string image, WorkContainerLauncherContext context, string? role = null)
    {
        if (!RoleImageManifest.IsPinned(image)) return "image-unpinned: work container images must be digest pinned.";
        return (role is null
            ? context.ApprovedImages.Roles.Values.Any(pin => pin.Current == image)
            : context.ApprovedImages.Resolve(role) == image)
            ? null : "image-unapproved: the root-controlled release does not authorize this role image.";
    }

    private static string? RoleImage(string image, IEnumerable<string> labels, WorkContainerLauncherContext context)
    {
        var roles = labels.Where(label => label.StartsWith(WorkContainerLaunchPlanner.RoleLabel + "=", StringComparison.Ordinal)).ToArray();
        if (roles.Length != 1) return "role-required: exactly one approved role capability is required.";
        return Approved(image, context, roles[0][(WorkContainerLaunchPlanner.RoleLabel.Length + 1)..]);
    }

    private static string? Attempt(string name)
        => AttemptName.IsMatch(name) ? null : $"name-refused: '{name}' is not an agent-work attempt resource.";

    private static string? ExecutionOnly(string name)
        => name.EndsWith("-prepare", StringComparison.Ordinal)
            ? "prepare-control-refused: only the privileged launcher may start or exec in preparation containers." : null;

    private static string? VolumeCreate(string[] rest)
    {
        if (rest.Length == 0) return "volume-name-missing.";
        if ((rest.Length - 1) % 2 != 0) return "volume-option-refused: dangling option.";
        for (var index = 0; index < rest.Length - 1; index += 2)
            if (rest[index] != "--label" || !LabelPair.IsMatch(rest[index + 1]))
                return "volume-option-refused: only agent-studio labels are admitted.";
        return Attempt(rest[^1]);
    }

    private static string? Listing(string[] rest)
    {
        if (rest.Length < 2 || rest[^2] != "--format" || rest[^1] != "{{json .}}") return "listing-format-refused.";
        for (var index = 0; index < rest.Length - 2; index += 2)
            if (rest[index] != "--filter" || index + 1 >= rest.Length - 2 || !LabelFilter.IsMatch(rest[index + 1]))
                return "listing-filter-refused: only agent-studio label filters are admitted.";
        return rest.Contains($"label={WorkContainerLaunchPlanner.ManagedLabel}=1", StringComparer.Ordinal)
            ? null : "listing-filter-refused: the managed work-container label is required.";
    }

    private static string? Create(string[] rest, WorkContainerLauncherContext context)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var readOnly = false;
        var index = 0;
        for (; index < rest.Length && rest[index].StartsWith('-'); index++)
        {
            var flag = rest[index];
            if (flag == "--read-only") { readOnly = true; continue; }
            if (!CreateValueFlags.Contains(flag) || index + 1 >= rest.Length)
                return $"create-option-refused: '{flag}' is not admitted for a work container.";
            if (!values.TryGetValue(flag, out var list)) values[flag] = list = [];
            list.Add(rest[++index]);
        }
        string? Single(string flag) => values.TryGetValue(flag, out var list) && list.Count == 1 ? list[0] : null;

        if (Single("--entrypoint") != "/usr/bin/env") return "entrypoint-refused: the trusted entrypoint must be explicit.";
        if (!readOnly) return "read-only-required: the container root must be read-only.";
        if (Single("--name") is not { } name || Attempt(name) is { } badName) return "name-refused: an agent-work attempt name is required.";
        if (Single("--user") is not { } user || !AttemptUser(user)) return "user-refused: the container must run as an attempt UID.";
        if (Single("--cap-drop") != "ALL") return "capabilities-refused: every capability must be dropped.";
        if (Single("--security-opt") != "no-new-privileges") return "security-option-refused: only no-new-privileges is admitted.";
        if (Single("--restart") != "no") return "restart-refused: a work container never restarts.";
        if (Single("--workdir") is { } workdir && workdir != WorkContainerLaunchPlanner.WorkDirectory) return "workdir-refused.";
        if (Single("--network") is not { } network) return "network-required.";
        if (context.WorkZoneNetworks.Count == 0) return "zone-policy-unavailable: no applied and verified work zone on this host.";
        if (!context.WorkZoneNetworks.Contains(network)) return $"network-outside-work-zone: '{network}' is not a work-zone network.";
        foreach (var flag in new[] { "--cpus", "--memory", "--memory-swap", "--pids-limit" })
            if (Single(flag) is not { } limit || !decimal.TryParse(limit, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                return $"limit-refused: {flag} must be one positive value.";
        var expectedTmpfs = new HashSet<string>(StringComparer.Ordinal)
        {
            "/tmp:rw,nosuid,nodev,size=1g",
            $"{WorkContainerLaunchPlanner.SecretDirectory}:rw,noexec,nosuid,nodev,size=1m,mode=0700,uid={user.Split(':')[0]},gid={WorkContainerIdentity.GroupId}",
        };
        var tmpfs = values.GetValueOrDefault("--tmpfs") ?? [];
        if (tmpfs.Count != expectedTmpfs.Count || !expectedTmpfs.SetEquals(tmpfs))
            return "tmpfs-refused: only the fixed scratch and attempt secret tmpfs capabilities are admitted.";
        foreach (var label in values.GetValueOrDefault("--label") ?? [])
            if (!LabelPair.IsMatch(label)) return "label-refused: only agent-studio labels are admitted.";
        if (MountSet(values.GetValueOrDefault("--mount") ?? [], name, context, preparation: false) is { } badMount) return badMount;
        if (index >= rest.Length) return "image-missing.";
        if (RoleImage(rest[index], values.GetValueOrDefault("--label") ?? [], context) is { } badImage) return badImage;
        var command = rest[(index + 1)..];
        return command is ["/bin/sh", "-c", var script, "agent-work", ..] && script == WorkContainerLaunchPlanner.EntryScript
            ? null
            : "entry-refused: a work container starts only with the executor entry script.";
    }

    private static bool AttemptUser(string user)
    {
        var parts = user.Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            && WorkContainerIdentity.IsAttemptUid(uid)
            && parts[1] == WorkContainerIdentity.GroupId.ToString(CultureInfo.InvariantCulture);
    }

    internal static WorkContainerMount? ParseMount(string specification)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var readOnly = false;
        foreach (var part in specification.Split(','))
        {
            if (part == "readonly" && !readOnly) { readOnly = true; continue; }
            var equal = part.IndexOf('=');
            if (equal <= 0) return null;
            var key = part[..equal];
            if (key is not ("type" or "source" or "target") || !fields.TryAdd(key, part[(equal + 1)..])) return null;
        }
        return fields.Count == 3 ? new(fields["type"], fields["source"], fields["target"], readOnly) : null;
    }

    internal static IEnumerable<string> Values(IReadOnlyList<string> args, string flag)
    {
        // Stop at the image. Work-command arguments are caller data, not
        // runtime flags, and can themselves contain names such as --mount.
        if (args.Count == 0 || args[0] is not ("run" or "create")) yield break;
        for (var index = 1; index < args.Count && args[index].StartsWith('-'); index++)
        {
            if (args[index] is "--rm" or "--read-only" or "--pull=never") continue;
            if (index + 1 >= args.Count) yield break;
            if (args[index] == flag) yield return args[index + 1];
            index++;
        }
    }

    private static string? MountSet(IEnumerable<string> specifications, string container,
        WorkContainerLauncherContext context, bool preparation)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        string? cacheSet = null;
        foreach (var specification in specifications)
        {
            var mount = ParseMount(specification);
            if (mount is null || !targets.Add(mount.Target)) return "mount-refused: malformed or duplicate target.";
            try { WorkContainerLaunchPlanner.ValidateMount(mount); }
            catch (ArgumentException ex) { return "mount-refused: " + ex.Message; }
            if (mount.Type == "bind")
            {
                if (preparation || !context.BindSources.Contains(mount.Source))
                    return "mount-refused: no root-controlled source capability for this bind.";
            }
            else
            {
                if (!context.Volumes.Contains(mount.Source)) return "mount-refused: volume has no daemon capability.";
                if (mount.Target == "/work")
                {
                    if (mount.Source != container) return "mount-refused: only this attempt's volume may target /work.";
                }
                else
                {
                    var prefix = "agent-cache-" + mount.Target["/cache/".Length..] + "-";
                    if (!CacheName.IsMatch(mount.Source) || !mount.Source.StartsWith(prefix, StringComparison.Ordinal))
                        return "mount-refused: cache volume must match its fixed target.";
                    var suffix = mount.Source[prefix.Length..];
                    cacheSet ??= suffix;
                    if (cacheSet != suffix) return "mount-refused: caches must belong to one repository and version.";
                }
            }
        }
        return new[] { "/work", "/cache/npm", "/cache/nuget", "/cache/repo" }.All(targets.Contains)
            ? null : "mount-refused: the complete attempt and cache volume set is required.";
    }

    /// <summary>Inspect the daemon's effective mounts before any helper starts, including image VOLUME metadata.</summary>
    internal static string? EffectiveMountsRefusal(string json, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> volumePaths)
    {
        try
        {
            var expected = Values(arguments, "--mount").Select(ParseMount).ToDictionary(mount => mount!.Target, mount => mount!, StringComparer.Ordinal);
            var tmpfs = Values(arguments, "--tmpfs").Select(value => value.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var target = item.GetProperty("Destination").GetString()!;
                var type = item.GetProperty("Type").GetString();
                if (type == "tmpfs" && tmpfs.Remove(target)) continue;
                if (!expected.Remove(target, out var mount) || type != mount.Type
                    || item.GetProperty("RW").GetBoolean() == mount.ReadOnly) return "effective-mount-refused: unexpected target, type or write access.";
                if (type == "bind" && item.GetProperty("Source").GetString() != mount.Source)
                    return "effective-mount-refused: bind source differs from the capability.";
                if (type == "volume" && (item.GetProperty("Name").GetString() != mount.Source
                    || !volumePaths.TryGetValue(mount.Source, out var path) || item.GetProperty("Source").GetString() != path))
                    return "effective-mount-refused: volume differs from the daemon capability.";
            }
            // Docker stores legacy --tmpfs mounts in HostConfig.Tmpfs and
            // omits them from .Mounts. Every host-backed mount is mandatory;
            // any tmpfs entry a runtime does report must match the request.
            return expected.Count == 0 ? null : "effective-mount-refused: required mounts are missing.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return "effective-mount-refused: invalid runtime inspection.";
        }
    }

    /// <summary>The one-shot ownership step: root without network, CHOWN and FOWNER only, the fixed script.</summary>
    private static string? Prepare(string[] rest, WorkContainerLauncherContext context)
    {
        var expected = new[]
        {
            "--rm", "--user", "0:0", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--cap-add", "CHOWN", "--cap-add", "FOWNER", "--security-opt", "no-new-privileges",
        };
        if (rest.Length < expected.Length + 2 || !rest.Take(expected.Length).SequenceEqual(expected, StringComparer.Ordinal))
            return "run-refused: only the attempt ownership step may run.";
        var index = expected.Length;
        if (rest[index] != "--name" || Attempt(rest[index + 1]) is not null || !rest[index + 1].EndsWith("-prepare", StringComparison.Ordinal))
            return "run-refused: the ownership step needs its agent-work prepare name.";
        var container = rest[index + 1][..^"-prepare".Length];
        index += 2;
        var mounts = new List<string>();
        var labels = new List<string>();
        var entrypoints = new List<string>();
        for (; index < rest.Length && rest[index].StartsWith('-'); index += 2)
        {
            if (index + 1 >= rest.Length) return "run-refused: dangling option.";
            var value = rest[index + 1];
            var refusal = rest[index] switch
            {
                "--label" => Collect(labels, value, LabelPair.IsMatch(value)),
                "--mount" => Collect(mounts, value, true),
                "--entrypoint" => Collect(entrypoints, value, value == "/usr/bin/env"),
                _ => $"run-refused: option '{rest[index]}' is not admitted.",
            };
            if (refusal is not null) return refusal;
        }
        if (index >= rest.Length) return "image-missing.";
        if (entrypoints.Count != 1) return "entrypoint-refused: the trusted entrypoint must be explicit.";
        if (MountSet(mounts, container, context, preparation: true) is { } badMount) return badMount;
        if (RoleImage(rest[index], labels, context) is { } badImage) return badImage;
        return rest[(index + 1)..] is ["/bin/sh", "-c", var script, "agent-prepare", var uid]
               && script == WorkContainerLaunchPlanner.PrepareScript
               && int.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
               && WorkContainerIdentity.IsAttemptUid(parsed)
            ? null
            : "run-refused: the ownership step runs only its fixed script for an attempt UID.";
    }
    private static string? Collect(List<string> values, string value, bool valid)
    {
        values.Add(value);
        return valid ? null : "run-option-refused.";
    }
}

/// <summary>Daemon-issued authority. Resource and cache names are derived, never supplied by the socket caller.</summary>
public sealed record WorkContainerAttemptCapability(
    WorkContainerAttempt Attempt, int CacheVersion, IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Operations)
{
    public WorkContainerLimits? Limits { get; init; }
    public const string RepositoryLabel = "agent-studio.repository";
    public string Container => $"agent-work-{WorkContainerLaunchPlanner.Sanitize(Attempt.AttemptId)}-f{Attempt.Fence.ToString(CultureInfo.InvariantCulture)}";
    public string PrepareContainer => Container + "-prepare";
    public IReadOnlySet<string> Volumes => new HashSet<string>(new[] { Container }.Concat(
        new[] { "npm", "nuget", "repo" }.Select(cache => WorkContainerLaunchPlanner.CacheVolume(cache, Attempt.RepositoryKey, CacheVersion))), StringComparer.Ordinal);

    public static WorkContainerAttemptCapability For(WorkContainerAttempt attempt, WorkContainerLaunchPlan plan, int cacheVersion)
        => new(attempt, cacheVersion, plan.Labels,
            ["version", "image", "pull", "create", "run", "volume", "start", "wait", "logs", "inspect", "rm", "exec"])
            { Limits = plan.Limits };

    internal IReadOnlyDictionary<string, string> ResourceLabels(string name)
    {
        var labels = name == Container || name == PrepareContainer
            ? new Dictionary<string, string>(Labels) { [RepositoryLabel] = Attempt.RepositoryKey }
            : new Dictionary<string, string>
            {
                [WorkContainerLaunchPlanner.ManagedLabel] = "1", [RepositoryLabel] = Attempt.RepositoryKey,
            };
        // The preparation container uses the same reservation as execution.
        // Keep its grant label out of the executor inventory to avoid counting it twice.
        if (name == PrepareContainer) labels.Remove(WorkContainerLaunchPlanner.GrantLabel);
        return labels;
    }

    internal string? Refusal(IReadOnlyList<string> args)
    {
        if (!Operations.Contains(args[0])) return "operation-outside-capability.";
        var name = args switch
        {
            ["start" or "wait", var resource] => resource,
            ["logs", _, var resource] => resource,
            ["inspect", _, _, var resource] => resource,
            ["rm", _, _, var resource] => resource,
            ["exec", _, var resource, ..] => resource,
            ["volume", "rm", _, var resource] => resource,
            ["volume", "create", ..] => args[^1],
            _ => WorkContainerLauncherPolicy.Values(args, "--name").SingleOrDefault(),
        };
        if (name is not null && name != Container && name != PrepareContainer) return "resource-outside-capability.";
        if ((args[0] == "create" || args is ["volume", ..]) && name is not null && name != Container)
            return "resource-kind-outside-capability.";
        if (args[0] == "run" && name != PrepareContainer) return "resource-kind-outside-capability.";
        if (args[0] is "image" or "pull" && args[^1] != Labels[WorkContainerLaunchPlanner.ImageLabel])
            return "image-outside-capability.";
        if (args is ["volume", "ls", ..] || args[0] == "ps") return "inventory-requires-daemon-capability.";
        if (args[0] is "create" or "run" || args is ["volume", "create", ..])
        {
            var labels = WorkContainerLauncherPolicy.Values(args, "--label").ToArray();
            // volume has a two-word verb, unlike create/run.
            if (args[0] == "volume") labels = args.Skip(2).Where((_, index) => index % 2 == 1).ToArray();
            var expected = Labels.Where(pair => args[0] != "run" || pair.Key != WorkContainerLaunchPlanner.GrantLabel).ToArray();
            if (expected.Any(pair => labels.Count(label => label == $"{pair.Key}={pair.Value}") != 1)
                || labels.Length != expected.Length) return "labels-outside-capability.";
        }
        foreach (var mount in WorkContainerLauncherPolicy.Values(args, "--mount").Select(WorkContainerLauncherPolicy.ParseMount).OfType<WorkContainerMount>())
        {
            if (mount.Type == "volume" && !Volumes.Contains(mount.Source)) return "cache-outside-capability.";
            if (mount.Type == "bind" && mount.Source != Attempt.RepositorySource) return "source-outside-capability.";
        }
        if (args[0] == "create" && WorkContainerLauncherPolicy.Values(args, "--user").SingleOrDefault()
            != Labels[WorkContainerIdentity.UidLabel] + ":" + WorkContainerIdentity.GroupId.ToString(CultureInfo.InvariantCulture))
            return "identity-outside-capability.";
        if (args[0] == "run" && args[^1] != Labels[WorkContainerIdentity.UidLabel]) return "identity-outside-capability.";
        foreach (var env in WorkContainerLauncherPolicy.Values(args, "--env"))
            if (env.StartsWith("AGENT_REPOSITORY_KEY=", StringComparison.Ordinal) && env != "AGENT_REPOSITORY_KEY=" + Attempt.RepositoryKey)
                return "repository-outside-capability.";
        return null;
    }
}

/// <summary>Persisted bearer hashes and fence tombstones, shared by socket-activated launcher processes.</summary>
public sealed class WorkContainerLauncherAuthority
{
    public sealed record Entry(string Hash, WorkContainerAttemptCapability Scope, bool Revoked = false,
        WorkContainerGrant? Grant = null, DateTimeOffset? ExpiresAt = null, bool NeedsCleanup = false,
        bool ContainerCreated = false, bool StartRequested = false, bool PreparationPending = false)
    {
        internal bool HoldsReservation(DateTimeOffset now) =>
            Grant is not null && (NeedsCleanup || (!Revoked && ExpiresAt > now));
    }

    public List<Entry> Entries { get; init; } = [];
    public bool AdmissionInitialized { get; set; }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool Matches(string? supplied, string expected)
        => supplied is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(supplied)), Encoding.UTF8.GetBytes(Hash(expected)));

    public string Issue(WorkContainerAttemptCapability scope, WorkContainerGrant? grant = null, DateTimeOffset? expires = null)
    {
        WorkContainerLaunchPlanner.Validate(scope.Attempt, scope.Labels[WorkContainerLaunchPlanner.ImageLabel]);
        if (scope.CacheVersion < 1 || scope.Attempt.Fence < 1) throw new InvalidDataException("Invalid capability scope.");
        if (Entries.Any(current => SameAttempt(current.Scope, scope) && (current.Scope.Attempt.Fence > scope.Attempt.Fence
            || (current.Scope.Attempt.Fence == scope.Attempt.Fence && (current.Revoked
                || current.Scope.Attempt.LeaseId != scope.Attempt.LeaseId
                || current.Scope.Attempt.RepositoryKey != scope.Attempt.RepositoryKey)))))
            throw new InvalidDataException("Stale attempt fence or conflicting lease.");
        var identityLabels = new Dictionary<string, string>
        {
            [WorkContainerLaunchPlanner.ManagedLabel] = "1", [WorkContainerLaunchPlanner.HostLabel] = scope.Attempt.HostId,
            [WorkContainerLaunchPlanner.RoleLabel] = scope.Attempt.Role, [WorkContainerLaunchPlanner.AttemptLabel] = scope.Attempt.AttemptId,
            [WorkContainerLaunchPlanner.LeaseLabel] = scope.Attempt.LeaseId,
            [WorkContainerLaunchPlanner.FenceLabel] = scope.Attempt.Fence.ToString(CultureInfo.InvariantCulture),
            [WorkContainerLaunchPlanner.EpochLabel] = scope.Attempt.AuthorityEpoch.ToString(CultureInfo.InvariantCulture),
        };
        if (identityLabels.Any(pair => scope.Labels.GetValueOrDefault(pair.Key) != pair.Value)
            || Entries.Any(entry => entry.Scope.Container == scope.Container && !SameAttempt(entry.Scope, scope)))
            throw new InvalidDataException("Capability labels or resource identities conflict.");
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Entries.Add(new(Hash(token), scope, Grant: grant, ExpiresAt: expires));
        return token;
    }

    private static bool SameAttempt(WorkContainerAttemptCapability a, WorkContainerAttemptCapability b)
        => a.Attempt.HostId == b.Attempt.HostId && a.Attempt.AttemptId == b.Attempt.AttemptId;

    public WorkContainerAttemptCapability? Resolve(string? token) => ResolveEntry(token)?.Scope;

    internal Entry? ResolveEntry(string? token)
    {
        var entry = FindEntry(token);
        if (entry is null || entry.Revoked || Entries.Any(other => SameAttempt(other.Scope, entry.Scope)
            && (other.Scope.Attempt.Fence > entry.Scope.Attempt.Fence
                || (other.Scope.Attempt.Fence == entry.Scope.Attempt.Fence && other.Revoked)))) return null;
        return entry;
    }

    internal Entry? FindEntry(string? token)
    {
        if (token is null) return null;
        var hash = Hash(token);
        return Entries.SingleOrDefault(item => item.Hash == hash);
    }

    internal long AllocationVersion(string hostId) => Entries
        .Where(entry => entry.Scope.Attempt.HostId == hostId && entry.Grant is not null)
        .Select(entry => entry.Grant!.AllocationVersion).DefaultIfEmpty().Max();

    internal string? ReservationRefusal(WorkContainerAttemptCapability scope, WorkContainerGrant? grant,
        WorkContainerSettings settings, HostMemory memory, DateTimeOffset now)
    {
        if (grant is null || scope.Limits is not { } requested) return "reservation-grant-required.";
        if (grant.AllocationVersion < AllocationVersion(scope.Attempt.HostId)) return "allocation-stale.";
        var held = Entries.Where(entry => entry.HoldsReservation(now)).ToArray();
        if (!int.TryParse(scope.Labels.GetValueOrDefault(WorkContainerIdentity.UidLabel), NumberStyles.None,
                CultureInfo.InvariantCulture, out var uid) || !WorkContainerIdentity.IsAttemptUid(uid))
            return "reservation-uid-refused.";
        if (held.Any(entry => SameAttempt(entry.Scope, scope)
            || entry.Scope.Labels.GetValueOrDefault(WorkContainerIdentity.UidLabel)
                == scope.Labels.GetValueOrDefault(WorkContainerIdentity.UidLabel)))
            return "reservation-already-held: attempt or UID is in use.";
        var reservedCpu = held.Sum(entry => (long)entry.Grant!.HalfCores);
        if (reservedCpu > settings.ProfileMaxHalfCores)
            return "host-grant-exhausted.";
        var reservedMemory = held.Sum(entry => entry.Scope.Limits!.MemoryBytes);
        // Charge reserved memory at its limit even when a container has yet to allocate it.
        var available = memory.AvailableBytes - reservedMemory;
        var admission = WorkContainerAdmissionPolicy.Decide(grant,
            new((int)reservedCpu, available, memory.TotalBytes), settings);
        if (!admission.Admitted) return admission.Reason;
        var ceiling = admission.Limits!;
        if (requested.HalfCores != ceiling.HalfCores || requested.PidsLimit != ceiling.PidsLimit
            || requested.MemoryBytes is < (128L << 20) || requested.MemoryBytes > ceiling.MemoryBytes
            || scope.Labels.GetValueOrDefault(WorkContainerLaunchPlanner.GrantLabel) != requested.HalfCores.ToString(CultureInfo.InvariantCulture))
            return "limits-outside-grant.";
        return reservedMemory > settings.ProfileMaxMemoryBytes - requested.MemoryBytes
            ? "host-memory-exhausted." : null;
    }

    internal void Update(Entry previous, Entry updated) => Entries[Entries.IndexOf(previous)] = updated;

    public void Reconcile(WorkContainerLauncherReconciliation reconciliation)
    {
        var byAttempt = reconciliation.Authority.GroupBy(item => item.AttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Fence).First(), StringComparer.Ordinal);
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            var attempt = entry.Scope.Attempt;
            if (attempt.HostId != reconciliation.HostId) continue;
            var live = byAttempt.GetValueOrDefault(attempt.AttemptId);
            if (live is null || !live.Live || live.Fence != attempt.Fence || live.LeaseId != attempt.LeaseId)
                Entries[index] = entry with { Revoked = true };
        }
    }

    public void Revoke(string token)
    {
        var index = Entries.FindIndex(item => item.Hash == Hash(token));
        if (index >= 0) Entries[index] = Entries[index] with { Revoked = true };
    }
}

/// <summary>One async execution flow, including decorated runtimes, carries only its own attempt bearer.</summary>
internal sealed class WorkContainerLauncherSession : IDisposable
{
    private static readonly AsyncLocal<WorkContainerLauncherSession?> Current = new();
    private readonly WorkContainerLauncherSession? _previous = Current.Value;
    private readonly string? _socket;
    internal string? Token { get; private set; }
    internal static string? CurrentToken => Current.Value?.Token;
    internal WorkContainerLauncherSession(string? socket)
    {
        _socket = socket;
        Current.Value = this;
    }

    internal async Task AuthorizeAsync(WorkContainerAttempt attempt, WorkContainerLaunchPlan plan, int cacheVersion,
        WorkContainerGrant grant, CancellationToken ct)
    {
        if (_socket is null) return;
        var daemon = await WorkContainerLauncherCommand.ReadDaemonCapabilityAsync(ct);
        var result = await WorkContainerLauncherClient.RunAsync(_socket, ["capability", "issue"], null, ct,
            daemon, WorkContainerAttemptCapability.For(attempt, plan, cacheVersion), grant: grant);
        if (!result.Success) throw new InvalidDataException(result.StdErr);
        Token = result.StdOut;
    }

    internal async Task RevokeAsync()
    {
        if (_socket is null || Token is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await WorkContainerLauncherClient.RunAsync(_socket, ["capability", "revoke"], null, deadline.Token, Token);
        if (!result.Success) throw new InvalidDataException("Launcher attempt capability revocation failed.");
    }

    internal static async Task ReconcileAuthorityAsync(string? socket, string hostId,
        IReadOnlyList<WorkAttemptAuthority> authority, CancellationToken ct, bool sweep = false)
    {
        if (socket is null) return;
        var daemon = await WorkContainerLauncherCommand.ReadDaemonCapabilityAsync(ct);
        var result = await WorkContainerLauncherClient.RunAsync(socket, ["capability", sweep ? "sweep" : "reconcile"],
            null, ct, daemon, reconciliation: new(hostId, authority));
        if (!result.Success) throw new InvalidDataException("Launcher authority reconciliation failed.");
    }

    public void Dispose()
    {
        Token = null;
        Current.Value = _previous;
    }
}

/// <summary>One launcher call: the runtime arguments and, for the secret injection only, stdin.</summary>
public sealed record WorkContainerLauncherReconciliation(string HostId, IReadOnlyList<WorkAttemptAuthority> Authority);

public sealed record WorkContainerLauncherRequest(IReadOnlyList<string> Arguments, string? Stdin,
    string? Capability = null, WorkContainerAttemptCapability? Scope = null,
    WorkContainerLauncherReconciliation? Reconciliation = null, WorkContainerGrant? Grant = null);

public sealed record WorkContainerLauncherResponse(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// The host agent's side of the SM-11 launcher: one JSON request line over the
/// root-owned launcher socket, one JSON response. Socket activation needs no
/// setuid step, so it also works from units with <c>NoNewPrivileges=true</c>.
/// </summary>
public static class WorkContainerLauncherClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<ProcessResult> RunAsync(
        string socketPath, IReadOnlyList<string> arguments, string? stdin, CancellationToken ct,
        string? capability = null, WorkContainerAttemptCapability? scope = null,
        WorkContainerLauncherReconciliation? reconciliation = null, WorkContainerGrant? grant = null)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = JsonSerializer.Serialize(new WorkContainerLauncherRequest(arguments, stdin, capability, scope, reconciliation, grant), Json) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), ct);
            await stream.FlushAsync(ct);
            socket.Shutdown(SocketShutdown.Send);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var response = JsonSerializer.Deserialize<WorkContainerLauncherResponse>(await reader.ReadToEndAsync(ct), Json);
            return response is null
                ? new ProcessResult(125, "", "work-container launcher returned no response.")
                : new ProcessResult(response.ExitCode, response.StdOut, response.StdErr);
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException)
        {
            return new ProcessResult(125, "", $"work-container launcher unavailable at {socketPath}: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>agent-host work-container launch</c>: the root side of the launcher,
/// started per connection by <c>agent-work-launcher.socket</c> through the
/// root-owned <c>/usr/local/sbin/agent-work-launcher</c> wrapper. It reads the
/// work-zone networks from the applied zone state, refuses every call outside
/// <see cref="WorkContainerLauncherPolicy"/>, and otherwise runs the runtime
/// CLI with the same arguments, passing stdin only to the secret injection.
/// <c>--stdio</c> reads one <see cref="WorkContainerLauncherRequest"/> line;
/// without it the remaining arguments are the runtime call (operator use).
/// </summary>
public static class WorkContainerLauncherCommand
{
    public const int RefusedExitCode = 126;
    internal const int WaitPendingExitCode = 75;
    private const int MaximumRequestCharacters = 1 << 20;
    private static readonly TimeSpan SocketIoTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(40);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
        {
            error.WriteLine("work-container launch runs only as root through agent-work-launcher.");
            return 2;
        }
        if (args is ["--stdio"])
            return await RunStdioAsync(input, output, error, (request, token) => ExecuteAsync(request, token), SocketIoTimeout, ct);
        var direct = await ExecuteAsync(new(args, null), ct, trustedOperator: true);
        output.Write(direct.StdOut);
        error.Write(direct.StdErr);
        return direct.ExitCode;
    }

    internal static async Task<int> RunStdioAsync(TextReader input, TextWriter output, TextWriter error,
        Func<WorkContainerLauncherRequest, CancellationToken, Task<WorkContainerLauncherResponse>> execute,
        TimeSpan ioTimeout, CancellationToken ct)
    {
        WorkContainerLauncherRequest? request = null;
        try
        {
            // Console.In serializes reads synchronously. Bound the wait even
            // if a peer never supplies a byte. The oneshot process then exits.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ioTimeout);
            var token = deadline.Token;
            var line = await Task.Run(() => ReadRequestAsync(input, token), token).WaitAsync(token);
            if (line is { Length: > 0 })
                request = JsonSerializer.Deserialize<WorkContainerLauncherRequest>(line, Json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RefusedExitCode;
        }
        catch (JsonException) { }
        var response = request?.Arguments is { Count: > 0 } arguments && arguments.All(arg => arg is not null)
            ? await execute(request with { Stdin = arguments is ["exec", ..] ? request.Stdin : null }, ct)
            : Refused("malformed request.");
        if (response.ExitCode == RefusedExitCode) error.WriteLine(response.StdErr.TrimEnd());
        try
        {
            await Task.Run(() =>
            {
                output.WriteLine(JsonSerializer.Serialize(response, Json));
                output.Flush();
            }, ct).WaitAsync(ioTimeout, ct);
        }
        catch (TimeoutException) { return RefusedExitCode; }
        return 0;
    }

    private static async Task<string?> ReadRequestAsync(TextReader input, CancellationToken ct)
    {
        var line = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct);
            var end = Array.IndexOf(buffer, '\n', 0, read);
            var count = end < 0 ? read : end;
            if (count > MaximumRequestCharacters - line.Length) return null;
            line.Append(buffer, 0, count);
            if (read == 0 || end >= 0) return line.ToString();
        }
    }

    private static async Task<WorkContainerLauncherResponse> ExecuteAsync(
        WorkContainerLauncherRequest request, CancellationToken ct, bool trustedOperator = false)
    {
        var runtime = Environment.GetEnvironmentVariable(WorkContainerSettings.RuntimeVariable) ?? "docker";
        if (runtime is not ("docker" or "podman"))
            return new(2, "", $"{WorkContainerSettings.RuntimeVariable} must be docker or podman.");
        var zonePath = Environment.GetEnvironmentVariable("RUNNER_ZONE_POLICY_STATE") is { Length: > 0 } configured
            ? configured : ZonePolicyStateStore.DefaultPath;
        var zone = ZonePolicyCapability.Describe(zonePath, ZoneKinds.Work, TimeProvider.System, ZonePolicyStateStore.CurrentBootId());
        var networks = zone is { Status: "ready" }
            ? AttemptContainmentHost.WorkZoneNetworks(zonePath)
            : new HashSet<string>(StringComparer.Ordinal);
        RoleImageManifest release;
        try { release = await RoleImageManifest.LoadLauncherReleaseAsync(RoleImageManifest.LauncherReleasePath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Cleanup, wait and inventory stay available when release admission
            // fails. Image operations still fail closed against an empty release.
            release = RoleImageManifest.Empty();
        }
        var context = new WorkContainerLauncherContext(networks)
        {
            ApprovedImages = release,
            BindSources = (release.Sources ?? []).ToHashSet(StringComparer.Ordinal),
        };
        try
        {
            var daemon = await ReadDaemonCapabilityAsync(ct);
            if (trustedOperator) request = request with { Capability = daemon };
            PrepareAuthorityDirectory();
            using var executable = ResolveRuntime(runtime, RuntimeSearchDirectories, ct);
            return await ExecuteRequestAsync(request, context, AuthorityDirectory, daemon, runtime,
                GetHostBudget, TimeProvider.System,
                (call, data, token) => ProcessRunner.RunAsync(executable.ExecutionPath, call, stdin: data, ct: token), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Refused("capability-unavailable: " + ex.Message);
        }
    }

    // Provisioned by the operator: root-owned, readable only by the host daemon's
    // group, never mounted into a work container. Do not accept caller paths.
    public const string DaemonCapabilityPath = "/etc/agent-host/work-container-daemon-capability";
    private const string AuthorityDirectory = "/var/lib/agent-work-launcher/authority";

    internal static async Task<string> ReadDaemonCapabilityAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Launcher capabilities require Linux.");
        using var handle = RoleImageManifest.OpenRootControlled(DaemonCapabilityPath, directory: false);
        if ((File.GetUnixFileMode(RoleImageManifest.DescriptorPath(handle)) & UnixFileMode.OtherRead) != 0)
            throw new InvalidDataException("Daemon capability must not be world-readable.");
        using var reader = new StreamReader(RoleImageManifest.DescriptorPath(handle));
        var secret = (await reader.ReadToEndAsync(ct)).Trim();
        if (secret.Length < 64 || secret.Length > 256 || secret.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Invalid daemon capability configuration.");
        return secret;
    }

    private static void PrepareAuthorityDirectory()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Launcher authority requires Linux.");
        using (RoleImageManifest.OpenRootControlled("/var/lib", directory: true)) { }
        const string parent = "/var/lib/agent-work-launcher";
        Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (RoleImageManifest.OpenRootControlled(parent, directory: true)) { }
        Directory.CreateDirectory(AuthorityDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (RoleImageManifest.OpenRootControlled(AuthorityDirectory, directory: true)) { }
    }

    private const string WorkSlice = "agent-work.slice";
    private const string WorkCgroup = "/agent.slice/" + WorkSlice;

    private static (WorkContainerSettings Settings, HostMemory Memory) GetHostBudget()
    {
        var memory = HostMemory.Read();
        var settings = WorkContainerSettings.FromEnvironment(Environment.GetEnvironmentVariable,
            WorkContainerSettings.HostCores(), memory.TotalBytes, AuthorityDirectory);
        var cgroup = "/sys/fs/cgroup" + WorkCgroup;
        var cpu = File.ReadAllText(Path.Combine(cgroup, "cpu.max")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (long.TryParse(cpu[0], out var quota) && long.TryParse(cpu[1], out var period) && period > 0)
            settings = settings with { ProfileMaxHalfCores = (int)Math.Min(settings.ProfileMaxHalfCores, quota * 2 / period) };
        if (long.TryParse(File.ReadAllText(Path.Combine(cgroup, "memory.max")), out var bytes))
            settings = settings with { ProfileMaxMemoryBytes = Math.Min(settings.ProfileMaxMemoryBytes, bytes) };
        return (settings, memory);
    }

    internal static async Task<WorkContainerLauncherResponse> ExecuteRequestAsync(
        WorkContainerLauncherRequest request, WorkContainerLauncherContext context, string stateDirectory, string daemonSecret,
        string runtime, Func<(WorkContainerSettings Settings, HostMemory Memory)> hostBudget, TimeProvider clock,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run, CancellationToken ct,
        Func<string, bool>? diskAvailable = null)
    {
        var arguments = request.Arguments;
        var issue = arguments is ["capability", "issue"];
        var revoke = arguments is ["capability", "revoke"];
        var sweep = arguments is ["capability", "sweep"];
        var materializes = WorkContainerLauncherPolicy.Materializes(arguments);
        var start = arguments is ["start", ..];
        // Keep prepare, create, start and removal ordered with terminal release.
        // Pull and wait have separate deadlines and never hold this lock.
        var changesResources = materializes || start || revoke || sweep
            || arguments is ["capability", "reconcile"] or ["rm", ..] or ["volume", "rm", ..];
        var path = Path.Combine(stateDirectory, "capabilities.json");
        var authorityLock = Path.Combine(stateDirectory, "authority.lock");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(OperationTimeout(arguments));
        var token = deadline.Token;
        async Task<ProcessResult> RunBounded(IReadOnlyList<string> call, string? data, CancellationToken operation)
        {
            // Compensation runs on its own timeout after the main request has expired.
            using var cleanup = operation.CanBeCanceled ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runtimeToken = cleanup?.Token ?? operation;
            return await run(call, data, runtimeToken).WaitAsync(runtimeToken);
        }
        try
        {
            if (changesResources)
            {
                // Reject unauthenticated calls before they join the mutation queue.
                await using var check = await HostFileOperationLock.AcquireAsync(authorityLock, token);
                var state = await ReadAuthorityAsync(path, token);
                var auth = Authenticate(request, context, state, daemonSecret, clock.GetUtcNow());
                if (auth.Response is { ExitCode: not 0 } refusal) return refusal;
                if (auth.Response is null && WorkContainerLauncherPolicy.Refusal(arguments, auth.Context) is { } bad)
                    return Refused(bad);
            }
            await using var mutation = changesResources
                ? await HostFileOperationLock.AcquireAsync(Path.Combine(stateDirectory, "mutation.lock"), token) : null;
            WorkContainerLauncherAuthority.Entry? entry;
            await using (await HostFileOperationLock.AcquireAsync(authorityLock, token))
            {
                var authority = await ReadAuthorityAsync(path, token);
                var now = clock.GetUtcNow();
                entry = revoke ? authority.FindEntry(request.Capability) : authority.ResolveEntry(request.Capability);
                var budget = issue && WorkContainerLauncherAuthority.Matches(request.Capability, daemonSecret) ? hostBudget() : default;
                var auth = Authenticate(request, context, authority, daemonSecret, now, budget.Settings, budget.Memory);
                if (auth.Response is { } response)
                {
                    if (response.ExitCode != 0) return response;
                    if (issue && !authority.AdmissionInitialized)
                    {
                        // At upgrade, drain previously created containers whose
                        // grants cannot be reconstructed from capability records.
                        var previous = await ReadInventoryAsync(RunBounded, token, includeVolumes: false);
                        if (previous.Containers.Any(container => container.State is not ("exited" or "dead")))
                            return Refused("unreserved-containers-present: drain previous work before enabling admission.");
                        authority.AdmissionInitialized = true;
                    }
                    await SaveAuthorityAsync(path, authority, token);
                    if (sweep || (revoke && entry?.NeedsCleanup == true))
                    {
                        var pending = authority.Entries.Where(item => item.NeedsCleanup && (item.Revoked || item.ExpiresAt <= now && !item.StartRequested)
                            && (sweep ? item.Scope.Attempt.HostId == request.Reconciliation!.HostId : item.Hash == entry!.Hash)).ToArray();
                        if (pending.Length > 0)
                        {
                            var inventory = await ReadInventoryAsync(RunBounded, token);
                            foreach (var item in pending)
                                if (!inventory.Containers.Any(container => container.Resource.Name == item.Scope.Container
                                        || container.Resource.Name == item.Scope.PrepareContainer)
                                    && !inventory.Volumes.Any(volume => volume.Name == item.Scope.Container))
                                    authority.Update(item, item with { NeedsCleanup = false });
                            await SaveAuthorityAsync(path, authority, token);
                        }
                    }
                    return response;
                }
                context = auth.Context;
                if (WorkContainerLauncherPolicy.Refusal(arguments, context) is { } refused) return Refused(refused);
            }

            string? cgroupParent = null;
            if (arguments is ["pull", ..] || materializes)
            {
                var pulling = arguments[0] == "pull";
                await using var pullSlot = pulling ? TryAcquireOperation(Path.Combine(stateDirectory, "pull.lock")) : null;
                if (pulling && pullSlot is null) return new(WaitPendingExitCode, "", "");
                var envelope = await RuntimeEnvelopeAsync(runtime, stateDirectory, materializes && arguments[0] != "volume",
                    diskAvailable ?? HasDiskHeadroom, RunBounded, token);
                cgroupParent = envelope.Parent;
                if (pulling)
                {
                    var present = await RunBounded(["image", "inspect", "--format", "{{.Id}}", arguments[^1]], null, token);
                    if (present.Success) return new(0, present.StdOut, "");
                    return await PullAsync(arguments, envelope.Storage, diskAvailable ?? HasDiskHeadroom, RunBounded, token);
                }
            }

            if (materializes || start)
            {
                await using var held = await HostFileOperationLock.AcquireAsync(authorityLock, token);
                var authority = await ReadAuthorityAsync(path, token);
                var auth = Authenticate(request, context, authority, daemonSecret, clock.GetUtcNow());
                if (auth.Response is { } response) return response;
                if (WorkContainerLauncherPolicy.Refusal(arguments, auth.Context) is { } refusal) return Refused(refusal);
                var current = authority.ResolveEntry(request.Capability)!;
                authority.Update(current, current with
                {
                    NeedsCleanup = true, StartRequested = start, PreparationPending = arguments[0] == "run",
                });
                await SaveAuthorityAsync(path, authority, token);
            }
            await using var waitSlot = arguments[0] == "wait"
                ? TryAcquireOperation(Path.Combine(stateDirectory, $"wait-{WaitLockIndex(arguments[1])}.lock")) : null;
            if (arguments[0] == "wait" && waitSlot is null) return new(WaitPendingExitCode, "", "");
            var result = await ExecuteAuthorizedAsync(arguments, request.Stdin, context, RunBounded, token, cgroupParent: cgroupParent);
            if (arguments[0] is "create" or "run" && result.ExitCode == 0)
            {
                await using var held = await HostFileOperationLock.AcquireAsync(authorityLock, token);
                var authority = await ReadAuthorityAsync(path, token);
                var current = authority.ResolveEntry(request.Capability)!;
                authority.Update(current, current with { ContainerCreated = arguments[0] == "create", PreparationPending = false });
                await SaveAuthorityAsync(path, authority, token);
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return arguments is ["wait", ..]
                ? new(WaitPendingExitCode, "", "")
                : Refused("operation-deadline-exceeded.");
        }
    }

    private static TimeSpan OperationTimeout(IReadOnlyList<string> args) => args switch
    {
        ["pull", ..] => TimeSpan.FromMinutes(30),
        ["wait", ..] => TimeSpan.FromSeconds(20),
        ["version", ..] => TimeSpan.FromSeconds(8),
        ["run" or "create", ..] => TimeSpan.FromSeconds(45),
        _ => TimeSpan.FromSeconds(30),
    };

    private static FileStream? TryAcquireOperation(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private static byte WaitLockIndex(string container)
        => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(container))[0];

    private static async Task<WorkContainerLauncherAuthority> ReadAuthorityAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkContainerLauncherAuthority>(file, Json, ct)
            ?? throw new InvalidDataException("Invalid launcher authority state.");
    }

    private static async Task SaveAuthorityAsync(string path, WorkContainerLauncherAuthority authority, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Launcher authority requires Linux.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.WriteThrough, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                await JsonSerializer.SerializeAsync(file, authority, Json, ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<ManagedWorkInventory> ReadInventoryAsync(
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run, CancellationToken ct, bool includeVolumes = true)
    {
        var filter = "label=" + WorkContainerLaunchPlanner.ManagedLabel + "=1";
        var containers = await run(["ps", "--all", "--no-trunc", "--filter", filter, "--format", "{{json .}}"], null, ct);
        var volumes = includeVolumes
            ? await run(["volume", "ls", "--filter", filter, "--format", "{{json .}}"], null, ct)
            : new ProcessResult(0, "", "");
        if (!InventoryComplete(containers) || !InventoryComplete(volumes))
            throw new InvalidDataException("A complete runtime inventory is required for launcher admission or release.");
        var inventory = CliWorkContainerRuntime.ParseInventory(containers.StdOut, volumes.StdOut);
        if (inventory.Containers.Any(container => string.IsNullOrEmpty(container.Resource.Name) || container.State == "unknown")
            || inventory.Volumes.Any(volume => string.IsNullOrEmpty(volume.Name)))
            throw new InvalidDataException("Runtime inventory contains incomplete resource records.");
        return inventory;
    }

    private static bool InventoryComplete(ProcessResult result)
        => result.Success && result.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .All(line => line.StartsWith('{'));

    private static async Task<(string? Parent, string Storage)> RuntimeEnvelopeAsync(string runtime, string stateDirectory, bool needsCgroup,
        Func<string, bool> diskAvailable,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run, CancellationToken ct)
    {
        var format = runtime == "docker" ? "{{.CgroupDriver}}\n{{.DockerRootDir}}" : "{{.Host.CgroupManager}}\n{{.Store.GraphRoot}}";
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromSeconds(8));
        var info = await run(["info", "--format", format], null, probe.Token);
        var fields = info.StdOut.Trim().Split('\n', 2, StringSplitOptions.TrimEntries);
        if (!info.Success || fields.Length != 2 || !Path.IsPathFullyQualified(fields[1]))
            throw new InvalidDataException("Runtime storage location is unavailable.");
        if (!diskAvailable(fields[1]) || !diskAvailable(stateDirectory))
            throw new InvalidDataException("Insufficient free space for work-container storage.");
        if (!needsCgroup) return (null, fields[1]);
        var parent = fields[0] switch
        {
            "systemd" => WorkSlice,
            "cgroupfs" => WorkCgroup,
            _ => throw new InvalidDataException("Runtime cgroup manager cannot place work in the host work slice."),
        };
        return (parent, fields[1]);
    }

    private static async Task<WorkContainerLauncherResponse> PullAsync(IReadOnlyList<string> arguments, string storage,
        Func<string, bool> diskAvailable, Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run,
        CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var pull = run(arguments, null, stop.Token);
        try
        {
            while (!pull.IsCompleted)
            {
                var tick = timer.WaitForNextTickAsync(ct).AsTask();
                if (await Task.WhenAny(pull, tick) == pull) break;
                await tick;
                if (!diskAvailable(storage))
                {
                    stop.Cancel();
                    try { await pull; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    return Refused("storage-headroom-lost-during-pull.");
                }
            }
            var result = await pull;
            return new(result.ExitCode, result.StdOut, result.StdErr);
        }
        finally { stop.Cancel(); }
    }

    private static bool HasDiskHeadroom(string path)
    {
        var storage = new DriveInfo(path);
        return storage.AvailableFreeSpace >= Math.Max(2L << 30, storage.TotalSize / 20);
    }

    internal static (WorkContainerLauncherContext Context, WorkContainerLauncherResponse? Response) Authenticate(
        WorkContainerLauncherRequest request, WorkContainerLauncherContext context,
        WorkContainerLauncherAuthority authority, string daemonSecret, DateTimeOffset now = default,
        WorkContainerSettings? settings = null, HostMemory? memory = null)
    {
        var daemon = WorkContainerLauncherAuthority.Matches(request.Capability, daemonSecret);
        if (request.Arguments is ["capability", "issue"])
        {
            if (!daemon || request.Scope is null) return (context, Refused("issuance-requires-daemon-capability."));
            if (request.Scope is not { Attempt: not null, Labels: not null, Operations: not null })
                return (context, Refused("invalid-or-stale-capability-scope."));
            if (settings is null || memory is null) return (context, Refused("reservation-policy-unavailable."));
            try
            {
                var refusal = authority.ReservationRefusal(request.Scope, request.Grant, settings, memory, now);
                if (refusal is not null) return (context, Refused(refusal));
                if (context.ApprovedImages.Resolve(request.Scope.Attempt.Role) is not { } approved
                    || approved != request.Scope.Labels.GetValueOrDefault(WorkContainerLaunchPlanner.ImageLabel))
                    return (context, Refused("image-outside-release."));
                return (context, new(0, authority.Issue(request.Scope, request.Grant, now + ReservationLifetime), ""));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or KeyNotFoundException)
            {
                return (context, Refused("invalid-or-stale-capability-scope."));
            }
        }
        if (request.Arguments is ["capability", "reconcile" or "sweep"])
        {
            if (!daemon || request.Reconciliation is null) return (context, Refused("reconciliation-requires-daemon-capability."));
            if (request.Arguments[1] == "reconcile") authority.Reconcile(request.Reconciliation);
            return (context, new(0, "", ""));
        }
        if (request.Arguments is ["capability", "revoke"])
        {
            if (daemon || authority.FindEntry(request.Capability) is null)
                return (context, Refused("revocation-requires-attempt-capability."));
            authority.Revoke(request.Capability!);
            return (context, new(0, "", ""));
        }
        var entry = daemon ? null : authority.ResolveEntry(request.Capability);
        var scope = entry?.Scope;
        if (!daemon && scope is null) return (context, Refused("attempt-capability-missing-or-stale."));
        var active = entry?.Grant is { } grant && entry.ExpiresAt > now
            && grant.AllocationVersion == authority.AllocationVersion(entry.Scope.Attempt.HostId);
        return (context with
        {
            DaemonCapability = daemon, AttemptCapability = scope,
            Volumes = scope?.Volumes ?? new HashSet<string>(StringComparer.Ordinal),
            ReservedLimits = active ? scope?.Limits : null,
            ContainerCreated = entry?.ContainerCreated == true,
            StartRequested = entry?.StartRequested == true,
            PreparationPending = entry?.PreparationPending == true,
        }, null);
    }

    /// <summary>Policy plus runtime ownership checks. No resource output escapes before both succeed.</summary>
    internal static async Task<WorkContainerLauncherResponse> ExecuteAuthorizedAsync(
        IReadOnlyList<string> arguments, string? stdin, WorkContainerLauncherContext context,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run, CancellationToken ct,
        string sourceSnapshotRoot = SourceSnapshotRoot, string? cgroupParent = null)
    {
        if (WorkContainerLauncherPolicy.Refusal(arguments, context) is { } refusal) return Refused(refusal);
        var volume = arguments is ["volume", "rm" or "create", ..];
        var name = arguments switch
        {
            ["start" or "wait", var resource] => resource,
            ["logs", _, var resource] => resource,
            ["inspect", _, _, var resource] => resource,
            ["rm", _, _, var resource] => resource,
            ["exec", _, var resource, ..] => resource,
            ["volume", "rm" or "create", ..] => arguments[^1],
            _ => null,
        };
        if (name is not null)
        {
            var inspection = await run(volume
                ? ["volume", "inspect", "--format", "{{json .}}", name]
                : ["inspect", "--format", "{{json .Config.Labels}}", name], null, ct);
            if (inspection.ExitCode == 0)
            {
                if (ResourceLabelsRefusal(inspection.StdOut, name, volume, context.AttemptCapability) is { } badLabels)
                    return Refused(badLabels);
            }
            else if (arguments is not ["volume", "create", ..])
                return Refused("resource-inspection-unavailable.");
        }
        var rewritten = arguments.ToList();
        if (context.AttemptCapability is { } scope && (arguments[0] is "create" or "run" || arguments is ["volume", "create", ..]))
            rewritten.InsertRange(arguments[0] == "volume" ? 2 : 1,
                ["--label", WorkContainerAttemptCapability.RepositoryLabel + "=" + scope.Attempt.RepositoryKey]);
        if (arguments[0] == "run")
        {
            var limits = context.ReservedLimits!;
            rewritten.InsertRange(1, WorkContainerLauncherPolicy.LimitFlags(new(1, Math.Min(limits.MemoryBytes, 256L << 20), 64)));
        }
        if (cgroupParent is not null) rewritten.InsertRange(1, ["--cgroup-parent", cgroupParent, "--pull=never"]);
        return await ExecuteAdmittedAsync(rewritten, stdin, run, ct, sourceSnapshotRoot, context.AttemptCapability);
    }

    internal static string? ResourceLabelsRefusal(string json, string name, bool volume, WorkContainerAttemptCapability? scope)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var labels = volume ? document.RootElement.GetProperty("Labels") : document.RootElement;
            var actual = labels.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
            if (scope is not null)
                return scope.ResourceLabels(name).All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value)
                    ? null : "runtime-labels-outside-capability.";
            // Trusted reconciliation also covers pre-upgrade orphans, but only
            // actual managed resources whose daemon labels agree with the name.
            if (actual.GetValueOrDefault(WorkContainerLaunchPlanner.ManagedLabel) != "1"
                || !actual.TryGetValue(WorkContainerLaunchPlanner.AttemptLabel, out var attempt)
                || string.IsNullOrWhiteSpace(actual.GetValueOrDefault(WorkContainerLaunchPlanner.LeaseLabel))
                || !long.TryParse(actual.GetValueOrDefault(WorkContainerLaunchPlanner.FenceLabel), out var fence) || fence < 1)
                return "runtime-resource-unmanaged.";
            var expected = $"agent-work-{WorkContainerLaunchPlanner.Sanitize(attempt)}-f{fence.ToString(CultureInfo.InvariantCulture)}";
            return name == expected || (!volume && name == expected + "-prepare") ? null : "runtime-name-label-mismatch.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return "runtime-labels-invalid.";
        }
    }

    // Match the root wrapper's sanitized PATH. Never search a caller's PATH.
    internal static readonly string[] RuntimeSearchDirectories =
        ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    internal sealed class RuntimeExecutable(SafeFileHandle handle) : IDisposable
    {
        // Child processes close inherited descriptors. Address the launcher's
        // still-open descriptor instead of /proc/self/fd or a mutable filename.
        public string ExecutionPath { get; } = $"/proc/{Environment.ProcessId}/fd/{handle.DangerousGetHandle().ToInt64()}";
        public void Dispose() => handle.Dispose();
    }

    internal static RuntimeExecutable ResolveRuntime(string runtime, IEnumerable<string> directories, CancellationToken ct,
        Func<string, SafeFileHandle>? openExecutable = null)
    {
        if (runtime is not ("docker" or "podman")) throw new InvalidDataException("Unsupported work runtime.");
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory, runtime);
            try
            {
                return new RuntimeExecutable((openExecutable ?? OpenRuntimeExecutable)(candidate));
            }
            catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 })
            {
                // Only absence permits falling through to the next system
                // directory. An untrusted earlier installation fails closed.
            }
        }
        throw new IOException("The configured work runtime is not installed in the trusted system search path.");
    }

    private static SafeFileHandle OpenRuntimeExecutable(string path)
        => RoleImageManifest.OpenRootControlled(path, directory: false, executable: true);

    private const string SourceSnapshotRoot = "/var/lib/agent-work-launcher/sources";

    private sealed class SourceSnapshot(string directory, IReadOnlyList<string> arguments) : IDisposable
    {
        public IReadOnlyList<string> Arguments { get; } = arguments;
        public bool Retained { get; set; }
        public void Dispose()
        {
            if (!Retained) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<SourceSnapshot?> SnapshotSourceAsync(IReadOnlyList<string> arguments, string root, CancellationToken ct)
    {
        var bind = WorkContainerLauncherPolicy.Values(arguments, "--mount")
            .Select(WorkContainerLauncherPolicy.ParseMount).SingleOrDefault(mount => mount?.Type == "bind");
        if (bind is null) return null;
        if (!RoleImageManifest.CanonicalSource(bind.Source)) throw new InvalidDataException("Noncanonical source capability.");
        using var source = RoleImageManifest.OpenRootControlled(bind.Source, directory: true);
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Source capabilities require Linux.");
        // Creation is under an already trusted ancestor. Existing directories
        // must pass the same ownership check before they can hold snapshots.
        var ancestor = root;
        while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor)!;
        using (RoleImageManifest.OpenRootControlled(ancestor, directory: true)) { }
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (RoleImageManifest.OpenRootControlled(root, directory: true)) { }
        var name = WorkContainerLauncherPolicy.Values(arguments, "--name").Single();
        var directory = Path.Combine(root, name);
        // Exclusive creation also fences concurrent requests for the same name.
        if (MakeDirectory(directory, 0x1c0) != 0) throw new IOException("Source snapshot already exists or cannot be created.");
        try
        {
            var target = Path.Combine(directory, "source.git");
            Directory.CreateDirectory(target);
            await RoleImageManifest.WalkSourceAsync(source, target, owner: 0, ct);
            var specification = $"type=bind,source={target},target={bind.Target},readonly";
            var rewritten = arguments.ToArray();
            // Rewrite runtime options only, never the work command's data.
            for (var index = 1; index < rewritten.Length && rewritten[index].StartsWith('-'); index++)
            {
                if (rewritten[index] is "--rm" or "--read-only" or "--pull=never") continue;
                if (rewritten[index] == "--mount" && WorkContainerLauncherPolicy.ParseMount(rewritten[index + 1])?.Type == "bind")
                    rewritten[index + 1] = specification;
                index++;
            }
            return new SourceSnapshot(directory, rewritten);
        }
        catch { Directory.Delete(directory, recursive: true); throw; }
    }

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory(string path, uint mode);

    private static WorkContainerLauncherResponse Refused(string reason)
        => new(RefusedExitCode, "", "agent-work-launcher refused: " + reason);

    internal static async Task<WorkContainerLauncherResponse> ExecuteAdmittedAsync(
        IReadOnlyList<string> arguments, string? stdin,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run, CancellationToken ct,
        string sourceSnapshotRoot = SourceSnapshotRoot, WorkContainerAttemptCapability? scope = null)
    {
        if (arguments[0] is not ("create" or "run"))
        {
            var direct = await run(arguments, stdin, ct);
            if (direct.ExitCode == 0 && arguments is ["rm", "--force", "--volumes", var removed])
            {
                var directory = Path.Combine(sourceSnapshotRoot, removed);
                if (Directory.Exists(directory))
                {
                    using (RoleImageManifest.OpenRootControlled(directory, directory: true)) { }
                    Directory.Delete(directory, recursive: true);
                }
            }
            return new(direct.ExitCode, direct.StdOut, direct.StdErr);
        }
        using var snapshot = await SnapshotSourceAsync(arguments, sourceSnapshotRoot, ct);
        arguments = snapshot?.Arguments ?? arguments;
        var mounts = WorkContainerLauncherPolicy.Values(arguments, "--mount")
            .Select(WorkContainerLauncherPolicy.ParseMount).OfType<WorkContainerMount>().ToArray();
        var volumePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mount in mounts.Where(mount => mount.Type == "volume"))
        {
            async Task<ProcessResult> Inspect() => await run(["volume", "inspect", "--format", "{{json .}}", mount.Source], null, ct);
            var inspection = await Inspect();
            if (inspection.ExitCode != 0)
            {
                // Never use implicit creation or caller-provided driver options.
                var labels = scope?.ResourceLabels(mount.Source) ?? new Dictionary<string, string> { [WorkContainerLaunchPlanner.ManagedLabel] = "1" };
                var createVolume = new List<string> { "volume", "create", "--driver", "local" };
                foreach (var pair in labels) createVolume.AddRange(["--label", pair.Key + "=" + pair.Value]);
                createVolume.Add(mount.Source);
                var created = await run(createVolume, null, ct);
                if (created.ExitCode != 0) return Refused("volume-capability-unavailable: daemon volume creation failed.");
                inspection = await Inspect();
            }
            if (inspection.ExitCode != 0 || VolumeCapabilityRefusal(inspection.StdOut, mount.Source, out var path) is { })
                return Refused("volume-capability-unavailable: only local daemon volumes without driver options are admitted.");
            if (scope is not null && ResourceLabelsRefusal(inspection.StdOut, mount.Source, volume: true, scope) is { } badLabels)
                return Refused(badLabels);
            volumePaths.Add(mount.Source, path!);
        }
        var preparation = arguments[0] == "run";
        var name = WorkContainerLauncherPolicy.Values(arguments, "--name").Single();
        IReadOnlyList<string> create = preparation
            ? new[] { "create" }.Concat(arguments.Skip(1).Where(value => value != "--rm")).ToArray()
            : arguments;
        var result = await run(create, null, ct);
        if (result.ExitCode != 0) return new(result.ExitCode, result.StdOut, result.StdErr);
        var keep = false;
        var preparationRemoved = false;
        try
        {
            if (scope is not null)
            {
                var ownership = await run(["inspect", "--format", "{{json .Config.Labels}}", name], null, ct);
                if (ownership.ExitCode != 0 || ResourceLabelsRefusal(ownership.StdOut, name, volume: false, scope) is { })
                    return Refused("runtime-labels-outside-capability.");
            }
            var inspection = await run(["inspect", "--format", "{{json .Mounts}}", name], null, ct);
            if (inspection.ExitCode != 0) return Refused("effective-mount-refused: runtime inspection failed.");
            if (WorkContainerLauncherPolicy.EffectiveMountsRefusal(inspection.StdOut, arguments, volumePaths) is { } badMount)
                return Refused(badMount);
            if (!preparation)
            {
                keep = true;
                // Docker resolves bind paths at start, which is a later socket
                // call. Retain the private snapshot until successful removal.
                if (snapshot is not null) snapshot.Retained = true;
                return new(result.ExitCode, result.StdOut, result.StdErr);
            }
            // Inspect the root preparation container before it can execute, too.
            result = await run(["start", "--attach", name], null, ct);
            var removal = await run(["rm", "--force", "--volumes", name], null, CancellationToken.None);
            if (!removal.Success) return Refused("preparation-cleanup-incomplete.");
            preparationRemoved = true;
            return new(result.ExitCode, result.StdOut, result.StdErr);
        }
        finally
        {
            if (!keep && !preparationRemoved) await run(["rm", "--force", "--volumes", name], null, CancellationToken.None);
        }
    }

    internal static string? VolumeCapabilityRefusal(string json, string name, out string? path)
    {
        path = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var volume = document.RootElement;
            if (volume.GetProperty("Name").GetString() != name || volume.GetProperty("Driver").GetString() != "local"
                || volume.GetProperty("Scope").GetString() != "local") return "volume-driver-refused.";
            if (!volume.TryGetProperty("Options", out var options)
                || (options.ValueKind != JsonValueKind.Null && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any())))
                return "volume-options-refused.";
            path = volume.GetProperty("Mountpoint").GetString();
            return path is { Length: > 1 } && Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path
                ? null : "volume-path-refused.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return "volume-inspection-invalid.";
        }
    }
}
