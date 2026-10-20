using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// <c>agent-host zone-policy render|apply|verify</c> (AGT-3231). Host-side
/// tooling for the zone network policy; deploy/zones/apply-zone-policy.sh is
/// the operator entry point. <c>apply</c> is a dry run unless
/// <c>--execute</c> is given; with it the command ensures the networks, loads
/// the rules, verifies them with probe containers and records the result for
/// the daemons' <c>zone-policy</c> capability.
/// </summary>
public static class ZonePolicyCommand
{
    public const string DefaultPolicyPath = "/etc/agent-studio/zone-policy/zone-policy.json";
    public const string DefaultEnvironmentPath = "/etc/agent-studio/zone-policy/zone-policy.env";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);

    private static readonly Dictionary<string, string[]> VerbOptions = new(StringComparer.Ordinal)
    {
        ["render"] = ["--policy", "--env-file", "--zones", "--out-dir"],
        ["apply"] = ["--policy", "--env-file", "--zones", "--state"],
        ["verify"] = ["--policy", "--env-file", "--zones", "--state"],
    };

    private static readonly Dictionary<string, string[]> VerbFlags = new(StringComparer.Ordinal)
    {
        ["render"] = [],
        ["apply"] = ["--execute"],
        ["verify"] = ["--record"],
    };

    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var host = new ProcessZoneHost(CommandTimeout);
        return RunAsync(args, output, error, host, new DnsZoneAddressResolver(), TimeProvider.System,
            Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.Ordinal),
            settings => new DockerZoneNetworkProbe(host, settings.ProbeImage, settings.TimeoutSeconds),
            ct);
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        IZoneHost host,
        IZoneAddressResolver resolver,
        TimeProvider clock,
        IReadOnlyDictionary<string, string> processEnvironment,
        Func<ZoneVerificationSettings, IZoneNetworkProbe> probeFactory,
        CancellationToken ct)
    {
        var verb = args.Count > 0 ? args[0] : string.Empty;
        if (!VerbOptions.TryGetValue(verb, out var allowed))
        {
            error.WriteLine("usage: agent-host zone-policy render|apply|verify [--policy FILE] [--env-file FILE] "
                            + "[--zones a,b] [--state FILE] [--out-dir DIR] [--execute] [--record]");
            return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index++)
        {
            var name = args[index];
            if (VerbFlags[verb].Contains(name, StringComparer.Ordinal))
            {
                flags.Add(name);
                continue;
            }
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                error.WriteLine($"error: {verb} does not accept {name}; use {string.Join(", ", allowed.Concat(VerbFlags[verb]))}.");
                return 2;
            }
            if (index + 1 >= args.Count)
            {
                error.WriteLine($"error: {name} needs a value.");
                return 2;
            }
            if (!options.TryAdd(name, args[++index]))
            {
                error.WriteLine($"error: {name} is given more than once.");
                return 2;
            }
        }

        var policyPath = options.GetValueOrDefault("--policy", DefaultPolicyPath);
        var statePath = options.GetValueOrDefault("--state", ZonePolicyStateStore.DefaultPath);
        var zones = options.TryGetValue("--zones", out var zoneList)
            ? zoneList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        ResolvedZonePolicy policy;
        try
        {
            if (!File.Exists(policyPath))
            {
                error.WriteLine($"error: policy file '{policyPath}' does not exist.");
                return 2;
            }
            var json = await File.ReadAllTextAsync(policyPath, ct);
            var document = ZonePolicyParser.Parse(json);
            var environment = LoadEnvironment(options.GetValueOrDefault("--env-file"), processEnvironment, error);
            if (environment is null) return 2;
            policy = await ZonePolicyParser.ResolveAsync(
                document, ZonePolicyParser.Sha256(json), environment, resolver, ct, zones);
        }
        catch (ZonePolicyException exception)
        {
            foreach (var item in exception.Errors) error.WriteLine($"error: {item}");
            return 2;
        }
        var rules = ZonePolicyRules.Render(policy);

        switch (verb)
        {
            case "render":
                if (options.TryGetValue("--out-dir", out var outDir))
                {
                    Directory.CreateDirectory(outDir);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "zone-rules.v4"), rules.Iptables, ct);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "zone-plan.txt"), ZonePolicyRules.RenderPlan(rules), ct);
                    output.WriteLine($"rendered {rules.RuleCount} rules to {outDir}");
                }
                else
                    output.Write(rules.Iptables);
                return 0;

            case "apply":
            {
                var execute = flags.Contains("--execute");
                output.Write(ZonePolicyRules.RenderPlan(rules));
                if (!execute) output.Write(rules.Iptables);
                var report = await new ZonePolicyApplier(host).ApplyAsync(rules, execute, ct);
                foreach (var step in report.Steps)
                    output.WriteLine($"  [{step.Status}] {step.Name}{(step.Detail is null ? string.Empty : $": {step.Detail}")}");
                if (!execute)
                {
                    output.WriteLine(report.Succeeded
                        ? "dry run: nothing changed; rerun with --execute to apply and verify."
                        : "dry run: the plan cannot be applied as it stands.");
                    return report.Succeeded ? 0 : 2;
                }
                if (!report.Succeeded)
                {
                    // Record the failure so the daemons stop reporting an older,
                    // verified state as current.
                    var failed = report.Steps.First(step => step.Failed);
                    var verification = new ZoneVerificationReport(false, clock.GetUtcNow(),
                        [new ZoneCheck("*", "*", "setup", failed.Name, "applied", failed.Status, Passed: false, failed.Detail)]);
                    ZonePolicyStateStore.Write(statePath, ZonePolicyStateStore.Create(rules, policy, clock.GetUtcNow(), verification));
                    error.WriteLine($"error: apply failed at {failed.Name}; recorded as unverified in {statePath}.");
                    return 2;
                }
                var appliedAt = clock.GetUtcNow();
                var verified = await new ZonePolicyVerifier(probeFactory(policy.Verification), clock).VerifyAsync(policy, ct);
                WriteChecks(output, verified);
                ZonePolicyStateStore.Write(statePath, ZonePolicyStateStore.Create(rules, policy, appliedAt, verified));
                output.WriteLine($"recorded {statePath}");
                return verified.Passed ? 0 : 1;
            }

            default:
            {
                ZonePolicyState? previous = null;
                if (flags.Contains("--record"))
                {
                    var read = ZonePolicyStateStore.TryRead(statePath);
                    previous = read?.State;
                    if (previous is null || previous.RulesSha256 != rules.RulesSha256)
                    {
                        error.WriteLine(previous is null
                            ? $"error: no applied state in {statePath}; run apply --execute first."
                            : "error: the rendered rules differ from the applied ones (policy or DNS changed); run apply --execute.");
                        return 2;
                    }
                }
                var verified = await new ZonePolicyVerifier(probeFactory(policy.Verification), clock).VerifyAsync(policy, ct);
                WriteChecks(output, verified);
                if (previous is not null)
                {
                    ZonePolicyStateStore.Write(statePath, previous with { Verification = verified });
                    output.WriteLine($"recorded {statePath}");
                }
                return verified.Passed ? 0 : 1;
            }
        }
    }

    private static void WriteChecks(TextWriter output, ZoneVerificationReport report)
    {
        foreach (var check in report.Checks)
            output.WriteLine($"  [{(check.Passed ? "pass" : "FAIL")}] {check.Zone}/{check.Network} {check.Kind} {check.Target}: "
                             + $"expected {check.Expected}, observed {check.Observed}"
                             + (check.Passed || check.Detail is null ? string.Empty : $" ({check.Detail})"));
        output.WriteLine(report.Passed
            ? $"verification passed: {report.Checks.Count} checks"
            : $"verification FAILED: {report.FailedCount} of {report.Checks.Count} checks");
    }

    /// <summary>
    /// <c>KEY=VALUE</c> lines from the env file, then the process environment
    /// on top. A missing default file is fine; a missing named file is not.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? LoadEnvironment(
        string? path, IReadOnlyDictionary<string, string> processEnvironment, TextWriter error)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = path ?? DefaultEnvironmentPath;
        if (File.Exists(file))
        {
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    error.WriteLine($"error: {file}: '{line}' is not KEY=VALUE.");
                    return null;
                }
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
            }
        }
        else if (path is not null)
        {
            error.WriteLine($"error: env file '{path}' does not exist.");
            return null;
        }
        foreach (var (key, value) in processEnvironment)
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
        return values;
    }
}

/// <summary>
/// The <c>zone-policy</c> capability every coding, review and gate daemon
/// reports (AGT-3231): the policy version and zone the host enforces, from the
/// state <c>zone-policy apply --execute</c> recorded. A host without a record
/// reports nothing, so a workstation is not marked degraded; a record whose
/// verification failed reports <c>unavailable</c>.
/// </summary>
public static class ZonePolicyCapability
{
    public const string Category = "zone";

    public static AdvertisedCapabilityDto? Describe(string? statePath, string zoneKind)
    {
        if (string.IsNullOrWhiteSpace(statePath)) return null;
        return ZonePolicyStateStore.TryRead(statePath) is { } read
            ? Describe(read.State, read.Error, zoneKind)
            : null;
    }

    public static AdvertisedCapabilityDto Describe(ZonePolicyState? state, string? readError, string zoneKind)
    {
        if (state is null)
            return new AdvertisedCapabilityDto(Key: CapabilityProtocol.ZonePolicy, Category: Category,
                Status: "unavailable", Detail: readError ?? "zone policy state is unreadable");
        var zone = state.Zones.FirstOrDefault(item => item.Kind == zoneKind);
        if (zone is null)
            return new AdvertisedCapabilityDto(Key: CapabilityProtocol.ZonePolicy, Category: Category,
                Status: "unavailable", Version: state.PolicyVersion,
                Detail: $"the applied policy has no {zoneKind} zone");
        var verifiedAt = state.Verification.CheckedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var scope = $"networks={string.Join(',', zone.Networks)}; rules=sha256:{state.RulesSha256[..Math.Min(12, state.RulesSha256.Length)]}";
        if (!state.Verification.Passed)
        {
            var first = state.Verification.Checks.FirstOrDefault(check => !check.Passed);
            var reason = first is null
                ? "no checks ran"
                : $"{first.Kind} {first.Target} expected {first.Expected}, observed {first.Observed}";
            return new AdvertisedCapabilityDto(Key: CapabilityProtocol.ZonePolicy, Category: Category,
                Status: "unavailable", Version: state.PolicyVersion, Identity: zone.Name,
                Detail: $"verification failed at {verifiedAt}: {state.Verification.FailedCount} of "
                        + $"{state.Verification.Checks.Count} checks; {reason}; {scope}");
        }
        return new AdvertisedCapabilityDto(Key: CapabilityProtocol.ZonePolicy, Category: Category,
            Status: "ready", Version: state.PolicyVersion, Identity: zone.Name,
            Detail: $"verifiedAt={verifiedAt}; {scope}");
    }
}
