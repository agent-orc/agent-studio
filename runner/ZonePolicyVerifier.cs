using System.Globalization;

namespace AgentRunner;

/// <param name="Kind"><c>no-inbound</c>, <c>reachable</c>, <c>blocked</c>, <c>control</c> or <c>setup</c>.</param>
public sealed record ZoneCheck(
    string Zone,
    string Network,
    string Kind,
    string Target,
    string Expected,
    string Observed,
    bool Passed,
    string? Detail = null);

public sealed record ZoneVerificationReport(bool Passed, DateTimeOffset CheckedAt, IReadOnlyList<ZoneCheck> Checks)
{
    public int FailedCount => Checks.Count(check => !check.Passed);
}

/// <summary>
/// Proves the zone rules from inside each zone network: every reachable probe
/// must connect, every blocked probe must meet the zone's own reject or a
/// drop, and no container on a work network may publish a port. A positive
/// control from a network outside the zones shows that each blocked target
/// answers when nothing blocks it, so a dead destination cannot pass for a
/// block. A probe that could not run proves nothing and fails the verification
/// instead of passing it.
/// </summary>
public sealed class ZonePolicyVerifier(IZoneNetworkProbe probe, TimeProvider clock)
{
    public async Task<ZoneVerificationReport> VerifyAsync(ResolvedZonePolicy policy, CancellationToken ct)
    {
        var checks = new List<ZoneCheck>();
        var controls = new Dictionary<(string Address, int Port), ZoneProbeObservation>();
        try
        {
            await probe.PrepareAsync(ct);
        }
        catch (InvalidOperationException exception)
        {
            checks.Add(new ZoneCheck("*", "*", "setup", policy.Verification.ProbeImage, "ready", "unavailable",
                Passed: false, exception.Message));
            return new ZoneVerificationReport(false, clock.GetUtcNow(), checks);
        }
        foreach (var zone in policy.Zones)
        {
            foreach (var network in zone.Networks)
            {
                // A work container resolves through the zone resolver, whose
                // answers fill the allowed sets; the probe does the same.
                var dns = zone.Kind == ZoneKinds.Work ? ZonePolicyParser.GatewayOf(network.Subnet) : null;
                if (zone.Kind == ZoneKinds.Work)
                    checks.Add(await NoInboundAsync(zone, network, ct));
                foreach (var target in zone.Probes)
                {
                    var observation = await probe.ConnectAsync(network.Name, dns, target.Address, target.Port, ct);
                    checks.Add(new ZoneCheck(zone.Name, network.Name, target.Expectation, Endpoint(target),
                        target.Expectation, Observed(observation.Outcome),
                        Satisfies(target.Expectation, observation.Outcome), observation.Detail));
                }
            }
            foreach (var target in zone.Probes.Where(target => target.Expectation == ZoneProbeExpectations.Blocked)
                         .DistinctBy(target => (target.Address, target.Port)))
            {
                if (!controls.TryGetValue((target.Address, target.Port), out var control))
                {
                    control = await probe.ConnectAsync(policy.Verification.ControlNetwork, null, target.Address, target.Port, ct);
                    controls[(target.Address, target.Port)] = control;
                }
                checks.Add(new ZoneCheck(zone.Name, policy.Verification.ControlNetwork, "control", Endpoint(target),
                    "answered", Observed(control.Outcome), ControlAnswered(control.Outcome), control.Detail));
            }
        }
        return new ZoneVerificationReport(checks.Count > 0 && checks.All(check => check.Passed), clock.GetUtcNow(), checks);
    }

    private static string Endpoint(ResolvedZoneProbe target)
        => target.Host == target.Address
            ? $"{target.Address}:{target.Port.ToString(CultureInfo.InvariantCulture)}"
            : $"{target.Host} ({target.Address}:{target.Port.ToString(CultureInfo.InvariantCulture)})";

    private async Task<ZoneCheck> NoInboundAsync(ResolvedZone zone, ZoneNetwork network, CancellationToken ct)
    {
        try
        {
            var published = await probe.PublishedPortsAsync(network.Name, ct);
            return published.Count == 0
                ? new ZoneCheck(zone.Name, network.Name, "no-inbound", "published ports", "none", "none", Passed: true)
                : new ZoneCheck(zone.Name, network.Name, "no-inbound", "published ports", "none",
                    $"{published.Count} published", Passed: false, string.Join("; ", published));
        }
        catch (InvalidOperationException exception)
        {
            return new ZoneCheck(zone.Name, network.Name, "no-inbound", "published ports", "none", "unknown",
                Passed: false, exception.Message);
        }
    }

    /// <summary>
    /// A reachable probe passes only on a completed connection. A blocked
    /// probe passes only on the zone's own reject ("No route to host", from
    /// <c>icmp-admin-prohibited</c>) or a drop (a timeout). "Connection
    /// refused" is a reset from the real destination, so nothing blocked the
    /// connection; it fails, like an unresolved name or a failed probe.
    /// </summary>
    public static bool Satisfies(string expectation, ZoneProbeOutcome outcome)
        => expectation == ZoneProbeExpectations.Reachable
            ? outcome == ZoneProbeOutcome.Connected
            : outcome is ZoneProbeOutcome.Rejected or ZoneProbeOutcome.TimedOut;

    /// <summary>
    /// The positive control of a blocked target passes when the destination
    /// answered from outside the zones: it accepted the connection or reset
    /// it (a port nobody listens on). Either way the path is open, so the
    /// zone probe's reject or drop came from the zone rules. A timeout or
    /// reject here means the target is unreachable anyway and the block
    /// proves nothing.
    /// </summary>
    public static bool ControlAnswered(ZoneProbeOutcome outcome)
        => outcome is ZoneProbeOutcome.Connected or ZoneProbeOutcome.Refused;

    private static string Observed(ZoneProbeOutcome outcome) => outcome switch
    {
        ZoneProbeOutcome.Connected => "connected",
        ZoneProbeOutcome.Rejected => "rejected",
        ZoneProbeOutcome.Refused => "refused",
        ZoneProbeOutcome.TimedOut => "timed-out",
        ZoneProbeOutcome.Unresolved => "unresolved",
        _ => "probe-error",
    };
}

public sealed record ZonePolicyStateZone(string Name, string Kind, IReadOnlyList<string> Networks);

/// <summary>
/// The host's record of the zone policy it enforces, written by
/// <c>zone-policy apply --execute</c>. Daemons on the host read it to report
/// the <c>zone-policy</c> capability.
/// </summary>
/// <param name="BootId">The kernel boot id the record was made under. The rules
/// and ipsets do not survive a reboot, so a record from an earlier boot proves
/// nothing.</param>
/// <param name="RemovedAt">Set by <c>zone-policy remove --execute</c>.</param>
public sealed record ZonePolicyState(
    int SchemaVersion,
    string PolicyVersion,
    string PolicySha256,
    string RulesSha256,
    DateTimeOffset AppliedAt,
    IReadOnlyList<ZonePolicyStateZone> Zones,
    ZoneVerificationReport Verification,
    string? BootId = null,
    DateTimeOffset? RemovedAt = null);

public static class ZonePolicyStateStore
{
    public const int SchemaVersion = 2;
    public const string DefaultPath = "/var/lib/agent-studio/zone-policy/applied.json";
    public const string BootIdPath = "/proc/sys/kernel/random/boot_id";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static ZonePolicyState Create(
        ZoneRuleSet rules, ResolvedZonePolicy policy, DateTimeOffset appliedAt, ZoneVerificationReport verification,
        string? bootId)
        => new(SchemaVersion, rules.PolicyVersion, rules.PolicySha256, rules.RulesSha256, appliedAt,
            policy.Zones.Select(zone => new ZonePolicyStateZone(zone.Name, zone.Kind,
                zone.Networks.Select(network => network.Name).ToList())).ToList(),
            verification, bootId);

    /// <summary>
    /// The kernel boot id. A container reads the host's, because the boot id
    /// is not namespaced. Null where it cannot be read (not Linux).
    /// </summary>
    public static string? CurrentBootId(string path = BootIdPath)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var id = File.ReadAllText(path).Trim();
            return id.Length > 0 ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Serialize(ZonePolicyState state)
        => System.Text.Json.JsonSerializer.Serialize(state, Json) + "\n";

    /// <summary>Write through a sibling temporary file so a reader never sees half a record.</summary>
    public static void Write(string path, ZonePolicyState state)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, Serialize(state));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, path, overwrite: true);
    }

    /// <returns>null when no state exists; otherwise the state or a read error.</returns>
    public static (ZonePolicyState? State, string? Error)? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<ZonePolicyState>(File.ReadAllText(path), Json);
            if (state is null || state.SchemaVersion != SchemaVersion || state.Verification is null
                || string.IsNullOrWhiteSpace(state.PolicyVersion))
                return (null, $"unsupported zone policy state in {path}");
            return (state, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Text.Json.JsonException)
        {
            return (null, $"zone policy state {path} is unreadable: {exception.GetType().Name}");
        }
    }
}

/// <param name="Status">One of unchanged, would-create, created, valid, would-apply,
/// applied, would-insert, inserted, would-write, written, would-start, started,
/// would-restart, restarted, would-delete, deleted, unchecked, conflict, failed.</param>
public sealed record ZoneApplyStep(string Name, string Status, string? Detail = null)
{
    public bool Failed => Status is "conflict" or "failed";
}

public sealed record ZoneApplyReport(bool Executed, IReadOnlyList<ZoneApplyStep> Steps)
{
    public bool Succeeded => Steps.All(step => !step.Failed);
}

/// <summary>
/// Brings a host to a rendered rule set. Every step first reads the current
/// state, so a second run with the same rules changes nothing. Without
/// <c>execute</c> it only reads and validates (<c>iptables-restore --test</c>).
/// A zone network that exists with different settings is a conflict: it is
/// never recreated under running containers, and no rule is loaded.
/// </summary>
public sealed class ZonePolicyApplier(IZoneHost host, string resolverConfigPath = ZonePolicyApplier.DefaultResolverConfigPath)
{
    public const string ResolverUnit = "agent-zone-dns.service";
    public const string DefaultResolverConfigPath = "/var/lib/agent-studio/zone-policy/dnsmasq.conf";

    private const string NetworkFormat =
        "{{.Driver}}|{{range .IPAM.Config}}{{.Subnet}},{{end}}|"
        + "{{index .Options \"com.docker.network.bridge.name\"}}|"
        + "{{index .Options \"com.docker.network.bridge.enable_icc\"}}|{{.EnableIPv6}}";

    public async Task<ZoneApplyReport> ApplyAsync(ZoneRuleSet rules, bool execute, CancellationToken ct)
    {
        var steps = new List<ZoneApplyStep>();
        var docker = await host.RunAsync("docker", ["version", "--format", "{{.Server.Version}}"], null, ct);
        if (!docker.Succeeded)
        {
            steps.Add(new ZoneApplyStep("docker", "failed", FirstLine(docker.Error, "the Docker daemon is not reachable")));
            return new ZoneApplyReport(execute, steps);
        }
        steps.Add(new ZoneApplyStep("docker", "unchanged", $"server {docker.Output.Trim()}"));

        foreach (var network in rules.Networks)
            steps.Add(await EnsureNetworkAsync(network, execute, ct));
        if (steps.Any(step => step.Failed)) return new ZoneApplyReport(execute, steps);

        // The rules reference the sets, so they exist before the rules load.
        foreach (var set in rules.IpSets)
            steps.Add(await EnsureIpSetAsync(set, execute, ct));
        if (steps.Any(step => step.Failed)) return new ZoneApplyReport(execute, steps);
        var setsPending = steps.Any(step => step.Name.StartsWith("ipset ", StringComparison.Ordinal)
                                            && step.Status is "would-create" or "unchecked");

        if (rules.ResolverConfig is { } config)
        {
            steps.AddRange(await EnsureResolverAsync(config, execute, ct));
            if (steps.Any(step => step.Failed)) return new ZoneApplyReport(execute, steps);
        }

        foreach (var (family, payload, count) in new[] { (ZonePolicyRules.Ipv4, rules.Iptables, rules.RuleCount),
                     (ZonePolicyRules.Ipv6, rules.Ip6tables, rules.Rule6Count) })
        {
            var loader = $"{ZonePolicyRules.Iptables(family)}-restore";
            var restore = await host.RunAsync(loader, execute ? ["--noflush"] : ["--test", "--noflush"], payload, ct);
            var restoreName = $"{loader} {ZonePolicyRules.ForwardChain} {ZonePolicyRules.InputChain}";
            if (execute)
            {
                steps.Add(restore.Succeeded
                    ? new ZoneApplyStep(restoreName, "applied", $"{count} rules")
                    : new ZoneApplyStep(restoreName, "failed", FirstLine(restore.Error, $"exit {restore.ExitCode}")));
                if (!restore.Succeeded) return new ZoneApplyReport(execute, steps);
            }
            else if (restore.Succeeded)
                steps.Add(new ZoneApplyStep(restoreName, "would-apply", $"{count} rules pass {loader} --test"));
            else if (setsPending && family == ZonePolicyRules.Ipv4 && !IsPermissionOrNoTool(restore))
                // The kernel checks a set match against existing sets only.
                steps.Add(new ZoneApplyStep(restoreName, "unchecked",
                    $"the ipsets do not exist yet: {FirstLine(restore.Error, $"exit {restore.ExitCode}")}"));
            else
                steps.Add(Unchecked(restoreName, restore));
        }

        foreach (var jump in rules.Jumps)
            steps.Add(await EnsureJumpAsync(jump, execute, ct));
        return new ZoneApplyReport(execute, steps);
    }

    private async Task<ZoneApplyStep> EnsureJumpAsync(ZoneChainJump jump, bool execute, CancellationToken ct)
    {
        var chain = jump.Chain;
        if (jump.FallbackChain is { } fallback)
        {
            var exists = await host.RunAsync(jump.Executable, ["-S", chain], null, ct);
            if (!exists.Succeeded && IsPermissionOrNoTool(exists))
                return Unchecked($"{jump.Executable} {chain} -j {jump.Target}", exists, execute);
            if (!exists.Succeeded) chain = fallback;
        }
        var name = $"{jump.Executable} {chain} -j {jump.Target}";
        var check = await host.RunAsync(jump.Executable, ["-C", chain, "-j", jump.Target], null, ct);
        if (check.Succeeded) return new ZoneApplyStep(name, "unchanged");
        if (IsPermissionOrNoTool(check)) return Unchecked(name, check, execute);
        if (!execute) return new ZoneApplyStep(name, "would-insert", FirstLine(check.Error, "jump not present"));
        var insert = await host.RunAsync(jump.Executable, ["-I", chain, "1", "-j", jump.Target], null, ct);
        return insert.Succeeded
            ? new ZoneApplyStep(name, "inserted", chain == jump.Chain ? null : $"{jump.Chain} does not exist")
            : new ZoneApplyStep(name, "failed", FirstLine(insert.Error, $"exit {insert.ExitCode}"));
    }

    private async Task<ZoneApplyStep> EnsureIpSetAsync(ZoneIpSetSpec set, bool execute, CancellationToken ct)
    {
        var name = $"ipset {set.Name}";
        var list = await host.RunAsync("ipset", ["-t", "list", set.Name], null, ct);
        if (list.Succeeded)
        {
            var conflict = IpSetConflict(set, list.Output);
            return conflict is null
                ? new ZoneApplyStep(name, "unchanged")
                : new ZoneApplyStep(name, "conflict",
                    $"{conflict}; run zone-policy remove --execute and ipset destroy {set.Name}, or change the policy");
        }
        if (IsPermissionOrNoTool(list)) return Unchecked(name, list, execute);
        if (!execute) return new ZoneApplyStep(name, "would-create");
        var create = await host.RunAsync("ipset", set.CreateArguments, null, ct);
        return create.Succeeded
            ? new ZoneApplyStep(name, "created")
            : new ZoneApplyStep(name, "failed", FirstLine(create.Error, $"exit {create.ExitCode}"));
    }

    /// <summary>The first difference between <c>ipset -t list</c> output and the spec, or null.</summary>
    internal static string? IpSetConflict(ZoneIpSetSpec set, string listed)
    {
        var lines = listed.Split('\n', StringSplitOptions.TrimEntries);
        var type = lines.FirstOrDefault(line => line.StartsWith("Type:", StringComparison.Ordinal))?[5..].Trim();
        if (type != ZonePolicyRules.IpSetType) return $"type is {type ?? "unknown"}, not {ZonePolicyRules.IpSetType}";
        var header = (lines.FirstOrDefault(line => line.StartsWith("Header:", StringComparison.Ordinal))?[7..] ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? Value(string key) => header.SkipWhile(token => token != key).Skip(1).FirstOrDefault();
        if (Value("family") != "inet") return $"family is {Value("family") ?? "unknown"}, not inet";
        var timeout = set.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        if (Value("timeout") != timeout) return $"timeout is {Value("timeout") ?? "unset"}, not {timeout}";
        return null;
    }

    private async Task<IReadOnlyList<ZoneApplyStep>> EnsureResolverAsync(string config, bool execute, CancellationToken ct)
    {
        var steps = new List<ZoneApplyStep>();
        var configName = $"zone resolver configuration {resolverConfigPath}";
        bool changed;
        try
        {
            changed = !File.Exists(resolverConfigPath) || await File.ReadAllTextAsync(resolverConfigPath, ct) != config;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            steps.Add(new ZoneApplyStep(configName, execute ? "failed" : "unchecked", exception.Message));
            return steps;
        }
        if (!changed) steps.Add(new ZoneApplyStep(configName, "unchanged"));
        else if (!execute) steps.Add(new ZoneApplyStep(configName, "would-write"));
        else
        {
            WriteAtomically(resolverConfigPath, config);
            steps.Add(new ZoneApplyStep(configName, "written"));
        }

        var unitName = $"systemctl {ResolverUnit}";
        var active = await host.RunAsync("systemctl", ["is-active", "--quiet", ResolverUnit], null, ct);
        if (active.ExitCode == ProcessZoneHost.NotStarted || active.ExitCode < 0)
        {
            steps.Add(new ZoneApplyStep(unitName, execute ? "failed" : "unchecked", FirstLine(active.Error, "systemctl is not available")));
            return steps;
        }
        // dnsmasq reads its ipset lines only at start, so a changed
        // configuration needs a restart; an unchanged one only needs to run.
        if (active.Succeeded && !changed)
        {
            steps.Add(new ZoneApplyStep(unitName, "unchanged", "active"));
            return steps;
        }
        var verb = active.Succeeded ? "restart" : "start";
        if (!execute)
        {
            steps.Add(new ZoneApplyStep(unitName, $"would-{verb}", active.Succeeded ? "configuration changed" : "not active"));
            return steps;
        }
        var run = await host.RunAsync("systemctl", [verb, ResolverUnit], null, ct);
        steps.Add(run.Succeeded
            ? new ZoneApplyStep(unitName, verb == "restart" ? "restarted" : "started")
            : new ZoneApplyStep(unitName, "failed", FirstLine(run.Error, $"exit {run.ExitCode}")));
        return steps;
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<ZoneApplyStep> EnsureNetworkAsync(ZoneNetworkSpec network, bool execute, CancellationToken ct)
    {
        var name = $"docker network {network.Name}";
        var inspect = await host.RunAsync("docker", ["network", "inspect", "--format", NetworkFormat, network.Name], null, ct);
        if (inspect.Succeeded)
        {
            var conflict = NetworkConflict(network, inspect.Output.Trim());
            return conflict is null
                ? new ZoneApplyStep(name, "unchanged")
                : new ZoneApplyStep(name, "conflict",
                    $"{conflict}; detach its containers and remove the network, or change the policy");
        }
        if (!IsMissing(inspect.Error))
            return new ZoneApplyStep(name, "failed", FirstLine(inspect.Error, $"exit {inspect.ExitCode}"));
        if (!execute) return new ZoneApplyStep(name, "would-create");
        var create = await host.RunAsync("docker", ZonePolicyRules.NetworkCreateArguments(network), null, ct);
        return create.Succeeded
            ? new ZoneApplyStep(name, "created")
            : new ZoneApplyStep(name, "failed", FirstLine(create.Error, $"exit {create.ExitCode}"));
    }

    /// <summary>The first difference between an existing network and its spec, or null.</summary>
    internal static string? NetworkConflict(ZoneNetworkSpec network, string inspected)
    {
        var parts = inspected.Split('|');
        if (parts.Length != 5) return $"unexpected inspect output '{inspected}'";
        if (parts[0] != "bridge") return $"driver is {parts[0]}, not bridge";
        var subnets = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (subnets is not [var subnet] || subnet != network.Subnet)
            return $"subnet is {(subnets.Length == 0 ? "unset" : string.Join(' ', subnets))}, not {network.Subnet}";
        if (parts[2] != network.Bridge) return $"bridge is {Shown(parts[2])}, not {network.Bridge}";
        var icc = network.InterContainer ? "true" : "false";
        // Docker leaves the option unset for its default (enabled).
        var actualIcc = parts[3] is "" or "<no value>" ? "true" : parts[3];
        if (actualIcc != icc) return $"inter-container traffic is {actualIcc}, not {icc}";
        // The zone rules and the resolver cover IPv4 only.
        if (parts[4] != "false") return $"IPv6 is {(parts[4] == "true" ? "enabled" : Shown(parts[4]))}, not disabled";
        return null;
    }

    private static bool IsMissing(string error)
        => error.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || error.Contains("No such network", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A permission error or a missing tool proves nothing about the host:
    /// the dry run reports the step as unchecked, an executed run fails it.
    /// </summary>
    internal static ZoneApplyStep Unchecked(string name, ZoneCommandResult result, bool execute = false)
        => new(name, !execute && IsPermissionOrNoTool(result) ? "unchecked" : "failed",
            FirstLine(result.Error, $"exit {result.ExitCode}"));

    internal static bool IsPermissionOrNoTool(ZoneCommandResult result)
        => result.ExitCode == ProcessZoneHost.NotStarted
           || result.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || result.Error.Contains("must be root", StringComparison.OrdinalIgnoreCase)
           || result.Error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase);

    private static string Shown(string value) => value is "" or "<no value>" ? "unset" : value;

    internal static string FirstLine(string text, string fallback)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? fallback;
}
