using System.Globalization;

namespace AgentRunner;

/// <param name="Kind"><c>no-inbound</c>, <c>reachable</c>, <c>blocked</c> or <c>setup</c>.</param>
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
/// must connect, every blocked probe must be refused or dropped, and no
/// container on a work network may publish a port. A probe that could not run
/// proves nothing and fails the verification instead of passing it.
/// </summary>
public sealed class ZonePolicyVerifier(IZoneNetworkProbe probe, TimeProvider clock)
{
    public async Task<ZoneVerificationReport> VerifyAsync(ResolvedZonePolicy policy, CancellationToken ct)
    {
        var checks = new List<ZoneCheck>();
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
                if (zone.Kind == ZoneKinds.Work)
                    checks.Add(await NoInboundAsync(zone, network, ct));
                foreach (var target in zone.Probes)
                {
                    var observation = await probe.ConnectAsync(network.Name, target.Address, target.Port, ct);
                    var endpoint = target.Host == target.Address
                        ? $"{target.Address}:{target.Port.ToString(CultureInfo.InvariantCulture)}"
                        : $"{target.Host} ({target.Address}:{target.Port.ToString(CultureInfo.InvariantCulture)})";
                    checks.Add(new ZoneCheck(zone.Name, network.Name, target.Expectation, endpoint,
                        target.Expectation, Observed(observation.Outcome),
                        Satisfies(target.Expectation, observation.Outcome), observation.Detail));
                }
            }
        }
        return new ZoneVerificationReport(checks.Count > 0 && checks.All(check => check.Passed), clock.GetUtcNow(), checks);
    }

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
    /// probe passes on a refusal or a drop; an unresolved name or a failed
    /// probe container is inconclusive and fails either expectation.
    /// </summary>
    public static bool Satisfies(string expectation, ZoneProbeOutcome outcome)
        => expectation == ZoneProbeExpectations.Reachable
            ? outcome == ZoneProbeOutcome.Connected
            : outcome is ZoneProbeOutcome.Refused or ZoneProbeOutcome.TimedOut;

    private static string Observed(ZoneProbeOutcome outcome) => outcome switch
    {
        ZoneProbeOutcome.Connected => "connected",
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
public sealed record ZonePolicyState(
    int SchemaVersion,
    string PolicyVersion,
    string PolicySha256,
    string RulesSha256,
    DateTimeOffset AppliedAt,
    IReadOnlyList<ZonePolicyStateZone> Zones,
    ZoneVerificationReport Verification);

public static class ZonePolicyStateStore
{
    public const int SchemaVersion = 1;
    public const string DefaultPath = "/var/lib/agent-studio/zone-policy/applied.json";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static ZonePolicyState Create(
        ZoneRuleSet rules, ResolvedZonePolicy policy, DateTimeOffset appliedAt, ZoneVerificationReport verification)
        => new(SchemaVersion, rules.PolicyVersion, rules.PolicySha256, rules.RulesSha256, appliedAt,
            policy.Zones.Select(zone => new ZonePolicyStateZone(zone.Name, zone.Kind,
                zone.Networks.Select(network => network.Name).ToList())).ToList(),
            verification);

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
/// applied, would-insert, inserted, unchecked, conflict, failed.</param>
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
public sealed class ZonePolicyApplier(IZoneHost host)
{
    private const string NetworkFormat =
        "{{.Driver}}|{{range .IPAM.Config}}{{.Subnet}},{{end}}|"
        + "{{index .Options \"com.docker.network.bridge.name\"}}|"
        + "{{index .Options \"com.docker.network.bridge.enable_icc\"}}";

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

        var restore = await host.RunAsync("iptables-restore",
            execute ? ["--noflush"] : ["--test", "--noflush"], rules.Iptables, ct);
        var restoreName = $"iptables-restore {ZonePolicyRules.ForwardChain} {ZonePolicyRules.InputChain}";
        if (execute)
        {
            steps.Add(restore.Succeeded
                ? new ZoneApplyStep(restoreName, "applied", $"{rules.RuleCount} rules")
                : new ZoneApplyStep(restoreName, "failed", FirstLine(restore.Error, $"exit {restore.ExitCode}")));
            if (!restore.Succeeded) return new ZoneApplyReport(execute, steps);
        }
        else
        {
            steps.Add(restore.Succeeded
                ? new ZoneApplyStep(restoreName, "would-apply", $"{rules.RuleCount} rules pass iptables-restore --test")
                : Unchecked(restoreName, restore));
        }

        foreach (var jump in rules.Jumps)
        {
            var name = $"{jump.Chain} -j {jump.Target}";
            var check = await host.RunAsync("iptables", ["-C", jump.Chain, "-j", jump.Target], null, ct);
            if (check.Succeeded)
            {
                steps.Add(new ZoneApplyStep(name, "unchanged"));
                continue;
            }
            if (!execute)
            {
                steps.Add(new ZoneApplyStep(name, "would-insert", FirstLine(check.Error, "jump not present")));
                continue;
            }
            var insert = await host.RunAsync("iptables", ["-I", jump.Chain, "1", "-j", jump.Target], null, ct);
            steps.Add(insert.Succeeded
                ? new ZoneApplyStep(name, "inserted")
                : new ZoneApplyStep(name, "failed", FirstLine(insert.Error, $"exit {insert.ExitCode}")));
        }
        return new ZoneApplyReport(execute, steps);
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
        if (parts.Length != 4) return $"unexpected inspect output '{inspected}'";
        if (parts[0] != "bridge") return $"driver is {parts[0]}, not bridge";
        var subnets = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (subnets is not [var subnet] || subnet != network.Subnet)
            return $"subnet is {(subnets.Length == 0 ? "unset" : string.Join(' ', subnets))}, not {network.Subnet}";
        if (parts[2] != network.Bridge) return $"bridge is {Shown(parts[2])}, not {network.Bridge}";
        var icc = network.InterContainer ? "true" : "false";
        // Docker leaves the option unset for its default (enabled).
        var actualIcc = parts[3] is "" or "<no value>" ? "true" : parts[3];
        if (actualIcc != icc) return $"inter-container traffic is {actualIcc}, not {icc}";
        return null;
    }

    private static bool IsMissing(string error)
        => error.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || error.Contains("No such network", StringComparison.OrdinalIgnoreCase);

    private static ZoneApplyStep Unchecked(string name, ZoneCommandResult result)
        => new(name, result.ExitCode == ProcessZoneHost.NotStarted || IsPermission(result.Error) ? "unchecked" : "failed",
            FirstLine(result.Error, $"exit {result.ExitCode}"));

    private static bool IsPermission(string error)
        => error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || error.Contains("must be root", StringComparison.OrdinalIgnoreCase);

    private static string Shown(string value) => value is "" or "<no value>" ? "unset" : value;

    private static string FirstLine(string text, string fallback)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? fallback;
}
