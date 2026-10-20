using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// <c>agent-host zone-policy render|apply|verify|remove</c> (AGT-3231).
/// Host-side tooling for the zone network policy;
/// deploy/zones/apply-zone-policy.sh is the operator entry point. <c>apply</c>
/// and <c>remove</c> are dry runs unless <c>--execute</c> is given; with it
/// apply ensures the networks, ipsets and zone resolver, loads the rules,
/// verifies them with probe containers and records the result for the
/// daemons' <c>zone-policy</c> capability.
/// </summary>
public static class ZonePolicyCommand
{
    public const string DefaultPolicyPath = "/etc/agent-studio/zone-policy/zone-policy.json";
    public const string DefaultEnvironmentPath = "/etc/agent-studio/zone-policy/zone-policy.env";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);

    private static readonly Dictionary<string, string[]> VerbOptions = new(StringComparer.Ordinal)
    {
        ["render"] = ["--policy", "--env-file", "--zones", "--out-dir"],
        ["apply"] = ["--policy", "--env-file", "--zones", "--state", "--resolver-config"],
        ["verify"] = ["--policy", "--env-file", "--zones", "--state"],
        ["remove"] = ["--state"],
    };

    private static readonly Dictionary<string, string[]> VerbFlags = new(StringComparer.Ordinal)
    {
        ["render"] = [],
        ["apply"] = ["--execute"],
        ["verify"] = ["--record"],
        ["remove"] = ["--execute"],
    };

    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var host = new ProcessZoneHost(CommandTimeout);
        return RunAsync(args, output, error, host, new DnsZoneAddressResolver(), TimeProvider.System,
            Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.Ordinal),
            settings => new DockerZoneNetworkProbe(host, settings.ProbeImage, settings.TimeoutSeconds),
            ZonePolicyStateStore.CurrentBootId(),
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
        string? bootId,
        CancellationToken ct)
    {
        var verb = args.Count > 0 ? args[0] : string.Empty;
        if (!VerbOptions.TryGetValue(verb, out var allowed))
        {
            error.WriteLine("usage: agent-host zone-policy render|apply|verify|remove [--policy FILE] [--env-file FILE] "
                            + "[--zones a,b] [--state FILE] [--resolver-config FILE] [--out-dir DIR] [--execute] [--record]");
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
        if (verb == "remove")
            return await RemoveAsync(host, flags.Contains("--execute"), statePath, clock, output, error, ct);
        var zones = options.TryGetValue("--zones", out var zoneList)
            ? zoneList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var executing = verb == "apply" && flags.Contains("--execute");
        ResolvedZonePolicy? policy = null;
        ZonePolicyDocument? document = null;
        var problems = new List<string>();
        try
        {
            if (!File.Exists(policyPath))
                problems.Add($"policy file '{policyPath}' does not exist.");
            else
            {
                var json = await File.ReadAllTextAsync(policyPath, ct);
                document = ZonePolicyParser.Parse(json);
                var environment = LoadEnvironment(options.GetValueOrDefault("--env-file"), processEnvironment, problems);
                if (environment is not null)
                    policy = await ZonePolicyParser.ResolveAsync(
                        document, ZonePolicyParser.Sha256(json), environment, resolver, ct, zones);
            }
        }
        catch (ZonePolicyException exception)
        {
            problems.AddRange(exception.Errors);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreadable policy or env file, or a resolver error other than "not found".
            problems.Add($"the policy could not be loaded: {exception.GetType().Name}: {exception.Message}");
        }
        if (problems.Count > 0)
        {
            foreach (var item in problems) error.WriteLine($"error: {item}");
            if (executing)
            {
                // The rules on the host no longer follow the policy file, so
                // the daemons must stop reporting the older, verified state.
                RecordFailure(statePath, document, "policy", "invalid", problems[0], clock, bootId);
                error.WriteLine($"error: recorded as unverified in {statePath}.");
            }
            return 2;
        }
        var resolved = policy!;
        var rules = ZonePolicyRules.Render(resolved);
        // A denied name that does not resolve leaves its denial out; every
        // other rule still loads, and the verification records the problem.
        var unresolved = resolved.Problems ?? [];
        foreach (var problem in unresolved)
            error.WriteLine($"error: {problem.Detail}; its denial is missing from the rules");

        switch (verb)
        {
            case "render":
                if (options.TryGetValue("--out-dir", out var outDir))
                {
                    Directory.CreateDirectory(outDir);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "zone-rules.v4"), rules.Iptables, ct);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "zone-rules.v6"), rules.Ip6tables, ct);
                    if (rules.ResolverConfig is { } config)
                        await File.WriteAllTextAsync(Path.Combine(outDir, "zone-dnsmasq.conf"), config, ct);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "zone-plan.txt"), ZonePolicyRules.RenderPlan(rules), ct);
                    output.WriteLine($"rendered {rules.RuleCount} IPv4 and {rules.Rule6Count} IPv6 rules to {outDir}");
                }
                else
                    WritePayloads(output, rules);
                return 0;

            case "apply":
            {
                var execute = flags.Contains("--execute");
                output.Write(ZonePolicyRules.RenderPlan(rules));
                if (!execute) WritePayloads(output, rules);
                var applier = new ZonePolicyApplier(host,
                    options.GetValueOrDefault("--resolver-config", ZonePolicyApplier.DefaultResolverConfigPath));
                if (!execute)
                {
                    var plan = await applier.ApplyAsync(rules, execute: false, ct);
                    WriteSteps(output, plan);
                    output.WriteLine(!plan.Succeeded
                        ? "dry run: the plan cannot be applied as it stands."
                        : unresolved.Count > 0
                            ? "dry run: nothing changed; --execute would load the other rules and record the state as failed."
                            : "dry run: nothing changed; rerun with --execute to apply and verify.");
                    return plan.Succeeded && unresolved.Count == 0 ? 0 : 2;
                }
                try
                {
                    var report = await applier.ApplyAsync(rules, execute: true, ct);
                    WriteSteps(output, report);
                    if (!report.Succeeded)
                    {
                        // Record the failure so the daemons stop reporting an older,
                        // verified state as current.
                        var failed = report.Steps.First(step => step.Failed);
                        var verification = new ZoneVerificationReport(false, clock.GetUtcNow(),
                            [new ZoneCheck("*", "*", "setup", failed.Name, "applied", failed.Status, Passed: false, failed.Detail)]);
                        ZonePolicyStateStore.Write(statePath,
                            ZonePolicyStateStore.Create(rules, resolved, clock.GetUtcNow(), verification, bootId));
                        error.WriteLine($"error: apply failed at {failed.Name}; recorded as unverified in {statePath}.");
                        return 2;
                    }
                    var appliedAt = clock.GetUtcNow();
                    var verified = await new ZonePolicyVerifier(probeFactory(resolved.Verification), clock).VerifyAsync(resolved, ct);
                    WriteChecks(output, verified);
                    ZonePolicyStateStore.Write(statePath, ZonePolicyStateStore.Create(rules, resolved, appliedAt, verified, bootId));
                    output.WriteLine($"recorded {statePath}");
                    return unresolved.Count > 0 ? 2 : verified.Passed ? 0 : 1;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    error.WriteLine($"error: apply failed: {exception.GetType().Name}: {exception.Message}");
                    RecordFailure(statePath, document, "apply", "error", $"{exception.GetType().Name}: {exception.Message}",
                        clock, bootId);
                    error.WriteLine($"error: recorded as unverified in {statePath}.");
                    return 2;
                }
            }

            default:
            {
                ZonePolicyState? previous = null;
                if (flags.Contains("--record"))
                {
                    var read = ZonePolicyStateStore.TryRead(statePath);
                    previous = read?.State;
                    if (previous is null || previous.RemovedAt is not null || previous.RulesSha256 != rules.RulesSha256)
                    {
                        error.WriteLine(previous is null || previous.RemovedAt is not null
                            ? $"error: no applied state in {statePath}; run apply --execute first."
                            : "error: the rendered rules differ from the applied ones (policy or DNS changed); run apply --execute.");
                        return 2;
                    }
                }
                var verified = await new ZonePolicyVerifier(probeFactory(resolved.Verification), clock).VerifyAsync(resolved, ct);
                WriteChecks(output, verified);
                if (previous is not null)
                {
                    // The verification ran under this boot, so it proves the rules of this boot.
                    ZonePolicyStateStore.Write(statePath, previous with { Verification = verified, BootId = bootId });
                    output.WriteLine($"recorded {statePath}");
                }
                return verified.Passed ? 0 : 1;
            }
        }
    }

    private static async Task<int> RemoveAsync(
        IZoneHost host, bool execute, string statePath, TimeProvider clock, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        var report = await new ZonePolicyRemover(host).RemoveAsync(execute, ct);
        WriteSteps(output, report);
        if (!execute)
        {
            output.WriteLine(report.Succeeded
                ? "dry run: nothing changed; rerun with --execute to remove the zone rules."
                : "dry run: the zone rules cannot be removed as they stand.");
            return report.Succeeded ? 0 : 2;
        }
        var state = ZonePolicyStateStore.TryRead(statePath)?.State;
        if (!report.Succeeded)
        {
            var failed = report.Steps.First(step => step.Failed);
            error.WriteLine($"error: remove failed at {failed.Name}.");
            // Some jumps or chains may be gone already, so the recorded
            // verification no longer describes the host.
            if (state is not null)
            {
                ZonePolicyStateStore.Write(statePath, state with
                {
                    RemovedAt = clock.GetUtcNow(),
                    RemovalProblem = $"failed at {failed.Name}" + (failed.Detail is null ? string.Empty : $": {failed.Detail}"),
                });
                error.WriteLine($"error: recorded the incomplete removal in {statePath}.");
            }
            return 2;
        }
        // A daemon must not keep reporting a policy that no longer exists.
        // A complete removal keeps its time, so a second remove writes nothing.
        if (state is not null && (state.RemovedAt is null || state.RemovalProblem is not null))
        {
            ZonePolicyStateStore.Write(statePath, state with { RemovedAt = clock.GetUtcNow(), RemovalProblem = null });
            output.WriteLine($"recorded the removal in {statePath}");
        }
        output.WriteLine("zone rules removed; Docker networks, ipsets and the zone resolver stay (see the rollback runbook).");
        return 0;
    }

    /// <summary>
    /// A policy, environment or unexpected error under <c>apply --execute</c>
    /// becomes a failed verification in the state, keeping what is known of
    /// the policy.
    /// </summary>
    private static void RecordFailure(
        string statePath, ZonePolicyDocument? document, string target, string observed, string detail,
        TimeProvider clock, string? bootId)
    {
        var previous = ZonePolicyStateStore.TryRead(statePath)?.State;
        var now = clock.GetUtcNow();
        var zones = document?.Zones.Select(zone => new ZonePolicyStateZone(zone.Name, zone.Kind,
                        (zone.Networks ?? []).Select(network => network.Name).ToList())).ToList()
                    ?? previous?.Zones.ToList() ?? [];
        var version = document?.Version is { Length: > 0 } documented ? documented : previous?.PolicyVersion ?? "unknown";
        var verification = new ZoneVerificationReport(false, now,
            [new ZoneCheck("*", "*", "setup", target, target == "policy" ? "valid" : "applied", observed, Passed: false, detail)]);
        ZonePolicyStateStore.Write(statePath, new ZonePolicyState(ZonePolicyStateStore.SchemaVersion, version,
            previous?.PolicySha256 ?? string.Empty, previous?.RulesSha256 ?? string.Empty, now, zones, verification, bootId));
    }

    private static void WriteSteps(TextWriter output, ZoneApplyReport report)
    {
        foreach (var step in report.Steps)
            output.WriteLine($"  [{step.Status}] {step.Name}{(step.Detail is null ? string.Empty : $": {step.Detail}")}");
    }

    private static void WritePayloads(TextWriter output, ZoneRuleSet rules)
    {
        output.Write(rules.Iptables);
        output.Write(rules.Ip6tables);
        if (rules.ResolverConfig is { } config) output.Write(config);
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
        string? path, IReadOnlyDictionary<string, string> processEnvironment, List<string> errors)
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
                    errors.Add($"{file}: '{line}' is not KEY=VALUE.");
                    return null;
                }
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
            }
        }
        else if (path is not null)
        {
            errors.Add($"env file '{path}' does not exist.");
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
/// state <c>zone-policy apply --execute</c> recorded. A daemon reports it only
/// when <c>RUNNER_ZONE_POLICY_STATE</c> names the state file, and a missing
/// file reports nothing. A record reads as <c>unavailable</c> when its
/// verification failed, when it comes from an earlier boot (the rules and
/// ipsets are gone), when the last verification is older than twice the
/// timer interval (the timer stopped), or after a removal.
/// </summary>
public static class ZonePolicyCapability
{
    public const string Category = "zone";

    /// <summary>The <c>OnUnitActiveSec</c> of deploy/systemd/agent-zone-policy.timer.</summary>
    public static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(15);

    public static TimeSpan MaxVerificationAge => VerificationInterval * 2;

    public static AdvertisedCapabilityDto? Describe(string? statePath, string zoneKind, TimeProvider clock, string? hostBootId)
    {
        if (string.IsNullOrWhiteSpace(statePath)) return null;
        return ZonePolicyStateStore.TryRead(statePath) is { } read
            ? Describe(read.State, read.Error, zoneKind, clock, hostBootId)
            : null;
    }

    public static AdvertisedCapabilityDto Describe(
        ZonePolicyState? state, string? readError, string zoneKind, TimeProvider clock, string? hostBootId)
    {
        if (state is null)
            return Unavailable(null, null, readError ?? "zone policy state is unreadable");
        var zone = state.Zones.FirstOrDefault(item => item.Kind == zoneKind);
        var verifiedAt = Timestamp(state.Verification.CheckedAt);
        if (state is { RemovedAt: { } partly, RemovalProblem: { } problem })
            return Unavailable(state, zone, $"a removal of the zone rules stopped partway at {Timestamp(partly)} ({problem}); "
                                            + "rerun zone-policy remove --execute or apply --execute");
        if (state.RemovedAt is { } removedAt)
            return Unavailable(state, zone, $"the zone rules were removed at {Timestamp(removedAt)}");
        if (hostBootId is null)
            return Unavailable(state, zone, "the host boot id is unreadable, so the record cannot be tied to this boot");
        if (state.BootId != hostBootId)
            return Unavailable(state, zone, $"recorded before the last reboot (verified at {verifiedAt}); "
                                            + "the rules and ipsets are gone until agent-zone-policy.service reapplies them");
        var age = clock.GetUtcNow() - state.Verification.CheckedAt;
        if (age > MaxVerificationAge)
            return Unavailable(state, zone, $"last verification at {verifiedAt} is older than "
                                            + $"{MaxVerificationAge.TotalMinutes:0} minutes; check agent-zone-policy.timer");
        var scope = zone is null ? null : Scope(state, zone);
        if (!state.Verification.Passed)
        {
            var first = state.Verification.Checks.FirstOrDefault(check => !check.Passed);
            var reason = first is null
                ? "no checks ran"
                : $"{first.Kind} {first.Target} expected {first.Expected}, observed {first.Observed}"
                  + (first.Kind == "setup" && first.Detail is { } detail ? $" ({detail})" : string.Empty);
            return Unavailable(state, zone, $"verification failed at {verifiedAt}: {state.Verification.FailedCount} of "
                                            + $"{state.Verification.Checks.Count} checks; {reason}"
                                            + (scope is null ? string.Empty : $"; {scope}"));
        }
        if (zone is null)
            return Unavailable(state, null, $"the applied policy has no {zoneKind} zone");
        return new AdvertisedCapabilityDto(Key: CapabilityProtocol.ZonePolicy, Category: Category,
            Status: "ready", Version: state.PolicyVersion, Identity: zone.Name,
            Detail: $"verifiedAt={verifiedAt}; {scope}");
    }

    private static AdvertisedCapabilityDto Unavailable(ZonePolicyState? state, ZonePolicyStateZone? zone, string detail)
        => new(Key: CapabilityProtocol.ZonePolicy, Category: Category, Status: "unavailable",
            Version: state?.PolicyVersion, Identity: zone?.Name, Detail: detail);

    private static string Scope(ZonePolicyState state, ZonePolicyStateZone zone)
        => $"networks={string.Join(',', zone.Networks)}; rules=sha256:{state.RulesSha256[..Math.Min(12, state.RulesSha256.Length)]}";

    private static string Timestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
