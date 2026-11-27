using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.WorkContainers;

/// <summary>What the root-side launcher trusts: the networks of the applied, verified work zone.</summary>
public sealed record WorkContainerLauncherContext(IReadOnlySet<string> WorkZoneNetworks)
{
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
        for (var index = 0; index < rest.Length - 1; index += 2)
            if (rest[index] != "--label" || !LabelPair.IsMatch(rest[index + 1]))
                return "volume-option-refused: only agent-studio labels are admitted.";
        if ((rest.Length - 1) % 2 != 0) return "volume-option-refused: dangling option.";
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
        for (var index = 1; index < args.Count && args[index].StartsWith('-'); index++)
        {
            if (args[index] is "--rm" or "--read-only") continue;
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

/// <summary>One launcher call: the runtime arguments and, for the secret injection only, stdin.</summary>
public sealed record WorkContainerLauncherRequest(IReadOnlyList<string> Arguments, string? Stdin);

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
        string socketPath, IReadOnlyList<string> arguments, string? stdin, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = JsonSerializer.Serialize(new WorkContainerLauncherRequest(arguments, stdin), Json) + "\n";
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
    private const int MaximumRequestCharacters = 1 << 20;
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
        {
            var line = await input.ReadLineAsync(ct);
            WorkContainerLauncherRequest? request = null;
            try
            {
                if (line is { Length: > 0 and <= MaximumRequestCharacters })
                    request = JsonSerializer.Deserialize<WorkContainerLauncherRequest>(line, Json);
            }
            catch (JsonException)
            {
                request = null;
            }
            var response = request?.Arguments is { } arguments
                ? await ExecuteAsync(arguments, arguments is ["exec", ..] ? request.Stdin : null, ct)
                : new WorkContainerLauncherResponse(RefusedExitCode, "", "agent-work-launcher refused: malformed request.");
            if (response.ExitCode == RefusedExitCode) error.WriteLine(response.StdErr.TrimEnd());
            output.WriteLine(JsonSerializer.Serialize(response, Json));
            await output.FlushAsync(ct);
            return 0;
        }
        var direct = await ExecuteAsync(args, args is ["exec", ..] ? await input.ReadToEndAsync(ct) : null, ct);
        output.Write(direct.StdOut);
        error.Write(direct.StdErr);
        return direct.ExitCode;
    }

    private static async Task<WorkContainerLauncherResponse> ExecuteAsync(
        IReadOnlyList<string> arguments, string? stdin, CancellationToken ct)
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
            // Candidate names, not runtime authority. ExecuteAdmittedAsync must
            // resolve these to safe daemon-created volumes before any launch.
            Volumes = WorkContainerLauncherPolicy.Values(arguments, "--mount")
                .Select(WorkContainerLauncherPolicy.ParseMount).OfType<WorkContainerMount>()
                .Where(mount => mount.Type == "volume").Select(mount => mount.Source).ToHashSet(StringComparer.Ordinal),
        };
        if (WorkContainerLauncherPolicy.Refusal(arguments, context) is { } refusal)
            return Refused(refusal);
        try
        {
            var executable = await ResolveRuntimeAsync(runtime, RuntimeSearchDirectories, ct);
            return await ExecuteAdmittedAsync(arguments, stdin,
                (call, data, token) => ProcessRunner.RunAsync(executable, call, stdin: data, ct: token), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Refused("capability-unavailable: " + ex.Message);
        }
    }

    // Match the root wrapper's sanitized PATH. Never search a caller's PATH.
    internal static readonly string[] RuntimeSearchDirectories =
        ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    internal static async Task<string> ResolveRuntimeAsync(string runtime, IEnumerable<string> directories, CancellationToken ct,
        Func<string, CancellationToken, Task<bool>>? trusted = null)
    {
        if (runtime is not ("docker" or "podman")) throw new InvalidDataException("Unsupported work runtime.");
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, runtime);
            if (!File.Exists(candidate)) continue;
            // Root installations commonly use a symlink in /usr/local/bin.
            // Verify the alias path as well as the resolved binary's ancestors.
            if (!await (trusted ?? TrustedRuntimeDirectory)(directory, ct))
                throw new InvalidDataException("Runtime search directory is not root-controlled.");
            var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            if (!await (trusted ?? TrustedRuntimeFile)(resolved, ct))
                throw new InvalidDataException("Runtime executable is not root-controlled.");
            return resolved;
        }
        throw new IOException("The configured work runtime is not installed in the trusted system search path.");
    }

    private static Task<bool> TrustedRuntimeDirectory(string path, CancellationToken ct)
        => RoleImageManifest.RootControlledPathAsync(path, directory: true, ct);

    private static Task<bool> TrustedRuntimeFile(string path, CancellationToken ct)
        => RoleImageManifest.RootControlledPathAsync(path, directory: false, ct);

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
        var snapshot = new SourceSnapshot(directory, arguments);
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
                if (rewritten[index] is "--rm" or "--read-only") continue;
                if (rewritten[index] == "--mount" && WorkContainerLauncherPolicy.ParseMount(rewritten[index + 1])?.Type == "bind")
                    rewritten[index + 1] = specification;
                index++;
            }
            return new SourceSnapshot(directory, rewritten);
        }
        catch { snapshot.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory(string path, uint mode);

    private static WorkContainerLauncherResponse Refused(string reason)
        => new(RefusedExitCode, "", "agent-work-launcher refused: " + reason);

    internal static async Task<WorkContainerLauncherResponse> ExecuteAdmittedAsync(
        IReadOnlyList<string> arguments, string? stdin,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run, CancellationToken ct,
        string sourceSnapshotRoot = SourceSnapshotRoot)
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
                var created = await run(["volume", "create", "--driver", "local", "--label",
                    WorkContainerLaunchPlanner.ManagedLabel + "=1", mount.Source], null, ct);
                if (created.ExitCode != 0) return Refused("volume-capability-unavailable: daemon volume creation failed.");
                inspection = await Inspect();
            }
            if (inspection.ExitCode != 0 || VolumeCapabilityRefusal(inspection.StdOut, mount.Source, out var path) is { })
                return Refused("volume-capability-unavailable: only local daemon volumes without driver options are admitted.");
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
        try
        {
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
            return new(result.ExitCode, result.StdOut, result.StdErr);
        }
        finally
        {
            if (!keep) await run(["rm", "--force", "--volumes", name], null, CancellationToken.None);
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
