using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.WorkContainers;

/// <summary>What the root-side launcher trusts: the networks of the applied, verified work zone.</summary>
public sealed record WorkContainerLauncherContext(IReadOnlySet<string> WorkZoneNetworks);

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
        "--pids-limit", "--network", "--tmpfs", "--workdir", "--label", "--mount", "--env",
    };

    public static string? Refusal(IReadOnlyList<string> args, WorkContainerLauncherContext context)
    {
        if (args.Count == 0) return "empty: no runtime command.";
        var a = args.ToArray();
        return a switch
        {
            ["version", "--format", "{{.Server.Version}}"] => null,
            ["image", "inspect", "--format", "{{.Id}}", var image] => Pinned(image),
            ["pull", "--quiet", var image] => Pinned(image),
            ["start" or "wait", var name] => Attempt(name),
            ["logs", "--timestamps", var name] => Attempt(name),
            ["rm", "--force", "--volumes", var name] => Attempt(name),
            ["volume", "rm", "--force", var name] => Attempt(name),
            ["volume", "create", .. var rest] => VolumeCreate(rest),
            ["volume", "ls", .. var rest] => Listing(rest),
            ["ps", "--all", "--no-trunc", .. var rest] => Listing(rest),
            ["exec", "--interactive", var name, "/bin/sh", "-c", var script] =>
                Attempt(name) ?? (script == WorkContainerLaunchPlanner.InjectTokenScript ? null : "exec-command: only the attempt secret injection may run in a container."),
            ["create", .. var rest] => Create(rest, context),
            ["run", .. var rest] => Prepare(rest),
            _ => $"verb-refused: '{a[0]}' is not a work-container executor call.",
        };
    }

    private static string? Pinned(string image)
        => RoleImageManifest.IsPinned(image) ? null : "image-unpinned: work container images must be digest pinned.";

    private static string? Attempt(string name)
        => AttemptName.IsMatch(name) ? null : $"name-refused: '{name}' is not an agent-work attempt resource.";

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
        foreach (var tmpfs in values.GetValueOrDefault("--tmpfs") ?? [])
            if (!(tmpfs.StartsWith("/tmp:", StringComparison.Ordinal) || tmpfs.StartsWith(WorkContainerLaunchPlanner.SecretDirectory + ":", StringComparison.Ordinal))
                || !tmpfs.Contains("nosuid", StringComparison.Ordinal) || !tmpfs.Contains("nodev", StringComparison.Ordinal))
                return "tmpfs-refused: only /tmp and the attempt secret directory, nosuid and nodev.";
        foreach (var label in values.GetValueOrDefault("--label") ?? [])
            if (!LabelPair.IsMatch(label)) return "label-refused: only agent-studio labels are admitted.";
        foreach (var mount in values.GetValueOrDefault("--mount") ?? [])
            if (Mount(mount, name) is { } refusal) return refusal;
        if (index >= rest.Length) return "image-missing.";
        if (Pinned(rest[index]) is { } unpinned) return unpinned;
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

    private static string? Mount(string specification, string container)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var readOnly = false;
        foreach (var part in specification.Split(','))
        {
            if (part == "readonly") { readOnly = true; continue; }
            var equal = part.IndexOf('=');
            if (equal <= 0) return $"mount-refused: '{specification}'.";
            var key = part[..equal];
            if (key is not ("type" or "source" or "target") || !fields.TryAdd(key, part[(equal + 1)..]))
                return $"mount-refused: option '{key}' is not admitted.";
        }
        if (!fields.TryGetValue("type", out var type) || !fields.TryGetValue("source", out var source)
            || !fields.TryGetValue("target", out var target))
            return "mount-refused: type, source and target are required.";
        if (type == "volume")
        {
            if (source != container && !CacheName.IsMatch(source))
                return $"mount-refused: volume '{source}' is neither this attempt's volume nor a repository cache.";
        }
        else if (type != "bind" || !readOnly)
            return "mount-refused: only volumes and read-only bind mounts are admitted.";
        try
        {
            WorkContainerLaunchPlanner.ValidateMount(new WorkContainerMount(type, source, target, readOnly));
        }
        catch (ArgumentException ex)
        {
            return "mount-refused: " + ex.Message;
        }
        return null;
    }

    /// <summary>The one-shot ownership step: root without network, CHOWN and FOWNER only, the fixed script.</summary>
    private static string? Prepare(string[] rest)
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
        for (; index < rest.Length && rest[index].StartsWith('-'); index += 2)
        {
            if (index + 1 >= rest.Length) return "run-refused: dangling option.";
            var value = rest[index + 1];
            var refusal = rest[index] switch
            {
                "--label" => LabelPair.IsMatch(value) ? null : "label-refused.",
                "--mount" => Mount(value, container),
                _ => $"run-refused: option '{rest[index]}' is not admitted.",
            };
            if (refusal is not null) return refusal;
        }
        if (index >= rest.Length) return "image-missing.";
        if (Pinned(rest[index]) is { } unpinned) return unpinned;
        return rest[(index + 1)..] is ["/bin/sh", "-c", var script, "agent-prepare", var uid]
               && script == WorkContainerLaunchPlanner.PrepareScript
               && int.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
               && WorkContainerIdentity.IsAttemptUid(parsed)
            ? null
            : "run-refused: the ownership step runs only its fixed script for an attempt UID.";
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
        if (WorkContainerLauncherPolicy.Refusal(arguments, new WorkContainerLauncherContext(networks)) is { } refusal)
            return new(RefusedExitCode, "", $"agent-work-launcher refused: {refusal}");
        var result = await ProcessRunner.RunAsync(runtime, arguments, stdin: stdin, ct: ct);
        return new(result.ExitCode, result.StdOut, result.StdErr);
    }
}
