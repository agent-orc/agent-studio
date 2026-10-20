using System.Text;

namespace AgentRunner;

/// <summary>A Docker bridge network the apply step ensures for a zone.</summary>
public sealed record ZoneNetworkSpec(
    string Zone,
    string Kind,
    string Name,
    string Bridge,
    string Subnet,
    string? Tenant,
    bool InterContainer);

/// <summary>A jump from a built-in or Docker-owned chain into a zone chain.</summary>
/// <param name="Family"><c>ipv4</c> (iptables) or <c>ipv6</c> (ip6tables).</param>
/// <param name="FallbackChain">Where the jump goes when <paramref name="Chain"/>
/// does not exist: Docker creates the IPv6 DOCKER-USER chain only with its
/// ip6tables support on.</param>
public sealed record ZoneChainJump(string Family, string Chain, string Target, string? FallbackChain = null)
{
    public string Executable => ZonePolicyRules.Iptables(Family);
}

/// <summary>An ipset of one allowed destination; the zone resolver fills it.</summary>
public sealed record ZoneIpSetSpec(string Name, string Destination, int TimeoutSeconds)
{
    /// <summary>The <c>ipset create</c> arguments.</summary>
    public IReadOnlyList<string> CreateArguments
        => ["create", Name, ZonePolicyRules.IpSetType, "family", "inet", "timeout",
            TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
}

/// <summary>Everything one apply writes to the host, rendered without side effects.</summary>
/// <param name="Ip6tables">The IPv6 payload: it drops all zone-bridge traffic.</param>
/// <param name="ResolverConfig">The zone resolver's dnsmasq configuration, or null
/// without a work zone.</param>
/// <param name="RulesSha256">Covers both payloads and the resolver configuration.</param>
public sealed record ZoneRuleSet(
    string PolicyVersion,
    string PolicySha256,
    string Iptables,
    string RulesSha256,
    int RuleCount,
    IReadOnlyList<ZoneNetworkSpec> Networks,
    IReadOnlyList<ZoneChainJump> Jumps,
    string Ip6tables,
    int Rule6Count,
    IReadOnlyList<ZoneIpSetSpec> IpSets,
    string? ResolverConfig);

/// <summary>
/// Renders a resolved zone policy into an <c>iptables-restore --noflush</c>
/// payload. The payload declares only the two <c>AGENT-ZONE-*</c> chains, so
/// loading it replaces exactly those chains atomically and leaves Docker's and
/// the operator's chains alone; reapplying the same policy is a no-op.
/// <list type="bullet">
///   <item><c>AGENT-ZONE-FWD</c> hangs off <c>DOCKER-USER</c>, which Docker
///   evaluates before its own forwarding rules: container traffic that leaves
///   or enters a zone bridge.</item>
///   <item><c>AGENT-ZONE-IN</c> hangs off <c>INPUT</c>: container traffic to
///   the host itself, including the bridge gateway address, which would
///   otherwise bypass destination rules for services on the same host.</item>
/// </list>
/// Traffic a zone chain does not reject returns to the calling chain. The
/// IPv6 payload declares the same two chains and drops every packet on a zone
/// bridge, because the zone networks carry IPv4 only. A work zone allows a
/// named destination by its ipset (<c>agent-zone-&lt;destination&gt;</c>),
/// which the zone resolver fills from the answers the containers get.
/// </summary>
public static class ZonePolicyRules
{
    public const string ForwardChain = "AGENT-ZONE-FWD";
    public const string InputChain = "AGENT-ZONE-IN";
    public const string DockerUserChain = "DOCKER-USER";
    public const string InputBuiltInChain = "INPUT";
    public const string ForwardBuiltInChain = "FORWARD";
    public const string Ipv4 = "ipv4";
    public const string Ipv6 = "ipv6";

    /// <summary>bash 5.2 (multi-arch index), pinned so every host probes with the same image.</summary>
    public const string DefaultProbeImage = "bash:5.2@sha256:a54fb4422b18f05dd3107c36f39d67b26334fda7ec89f4126052b45e228e2f15";

    /// <summary>Docker's default bridge: outside every zone, so it hosts the positive controls.</summary>
    public const string DefaultControlNetwork = "bridge";
    public const string ZoneLabel = "org.agent-studio.zone";
    public const string TenantLabel = "org.agent-studio.tenant";
    public const string IpSetPrefix = "agent-zone-";
    public const string IpSetType = "hash:ip";

    /// <summary>The kernel's ipset name limit (32 bytes with the terminator).</summary>
    public const int MaxIpSetNameLength = 31;

    /// <summary>The zone resolver's own pid file, apart from a distribution dnsmasq's.</summary>
    public const string ResolverPidFile = "/run/agent-zone-dns.pid";
    private const string Reject = "-j REJECT --reject-with icmp-admin-prohibited";

    public static string IpSetName(string destination) => IpSetPrefix + destination;

    public static string Iptables(string family) => family == Ipv6 ? "ip6tables" : "iptables";

    public static ZoneRuleSet Render(ResolvedZonePolicy policy)
    {
        var rules = new List<string>
        {
            $"-A {ForwardChain} -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN",
            $"-A {InputChain} -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN",
        };
        var rules6 = new List<string>();
        var allNetworks = policy.Zones.SelectMany(zone => zone.Networks).ToList();
        var networks = new List<ZoneNetworkSpec>();
        foreach (var zone in policy.Zones)
        {
            foreach (var network in zone.Networks)
            {
                var scope = $"{zone.Name}/{network.Name}";
                var heading = $"# zone {zone.Name} ({zone.Kind}) network {network.Name} bridge {network.Bridge} subnet {network.Subnet}"
                              + (network.Tenant is { } tenant ? $" tenant {tenant}" : string.Empty);
                rules.Add(heading);
                if (zone.Kind == ZoneKinds.Work)
                    RenderWork(rules, zone, network, scope);
                else
                    RenderRun(rules, zone, network, scope, allNetworks);
                rules6.Add(heading);
                rules6.Add($"-A {ForwardChain} -i {network.Bridge} -m comment --comment \"{scope}: no IPv6\" -j DROP");
                rules6.Add($"-A {ForwardChain} -o {network.Bridge} -m comment --comment \"{scope}: no IPv6\" -j DROP");
                rules6.Add($"-A {InputChain} -i {network.Bridge} -m comment --comment \"{scope}: no IPv6\" -j DROP");
                networks.Add(new ZoneNetworkSpec(zone.Name, zone.Kind, network.Name, network.Bridge, network.Subnet,
                    network.Tenant, InterContainer: zone.Kind == ZoneKinds.Run));
            }
        }

        var iptables = Payload(policy, "iptables-restore", rules);
        var ip6tables = Payload(policy, "ip6tables-restore", rules6);
        var timeout = policy.Resolver?.SetTimeoutSeconds ?? 0;
        var ipSets = policy.Zones.Where(zone => zone.Kind == ZoneKinds.Work)
            .SelectMany(zone => zone.Allowed)
            .Where(destination => destination.IpSet is not null)
            .DistinctBy(destination => destination.Name)
            .Select(destination => new ZoneIpSetSpec(destination.IpSet!, destination.Name, timeout))
            .ToList();
        var resolverConfig = RenderResolverConfig(policy, networks);
        return new ZoneRuleSet(
            policy.Version,
            policy.PolicySha256,
            iptables,
            ZonePolicyParser.Sha256(iptables + ip6tables + (resolverConfig ?? string.Empty)),
            rules.Count(rule => rule.StartsWith("-A ", StringComparison.Ordinal)),
            networks,
            [
                new ZoneChainJump(Ipv4, DockerUserChain, ForwardChain),
                new ZoneChainJump(Ipv4, InputBuiltInChain, InputChain),
                new ZoneChainJump(Ipv6, DockerUserChain, ForwardChain, FallbackChain: ForwardBuiltInChain),
                new ZoneChainJump(Ipv6, InputBuiltInChain, InputChain),
            ],
            ip6tables,
            rules6.Count(rule => rule.StartsWith("-A ", StringComparison.Ordinal)),
            ipSets,
            resolverConfig);
    }

    private static string Payload(ResolvedZonePolicy policy, string loader, IReadOnlyList<string> rules)
    {
        var text = new StringBuilder();
        text.Append("# Rendered by agent-host zone-policy from policy ").Append(policy.Version)
            .Append(" (sha256:").Append(policy.PolicySha256).Append(").\n");
        text.Append("# Load with ").Append(loader).Append(" --noflush; only the AGENT-ZONE-* chains are replaced.\n");
        text.Append("*filter\n");
        text.Append(':').Append(ForwardChain).Append(" - [0:0]\n");
        text.Append(':').Append(InputChain).Append(" - [0:0]\n");
        foreach (var rule in rules) text.Append(rule).Append('\n');
        text.Append("COMMIT\n");
        return text.ToString();
    }

    private static void RenderWork(List<string> rules, ResolvedZone zone, ZoneNetwork network, string scope)
    {
        // No inbound: a published port or another container cannot open a
        // connection into the work bridge. Replies to outbound connections
        // already returned at the conntrack rule above.
        rules.Add($"-A {ForwardChain} -o {network.Bridge} -m comment --comment \"{scope}: no inbound\" -j DROP");
        // Containers resolve through the zone resolver on the bridge gateway;
        // its answers are what fills the allowed sets.
        var gateway = ZonePolicyParser.GatewayOf(network.Subnet);
        foreach (var protocol in new[] { "udp", "tcp" })
            rules.Add($"-A {InputChain} -i {network.Bridge} -d {gateway}/32 -p {protocol} --dport 53 "
                      + $"-m comment --comment \"{scope}: zone resolver\" -j RETURN");
        foreach (var chain in new[] { ForwardChain, InputChain })
        {
            foreach (var destination in zone.Allowed)
            {
                var matches = destination.Literals.Select(address => $" -d {address}").ToList();
                if (destination.IpSet is { } set) matches.Add($" -m set --match-set {set} dst");
                foreach (var target in matches)
                {
                    foreach (var match in PortMatches(destination))
                        rules.Add($"-A {chain} -i {network.Bridge}{target}{match} "
                                  + $"-m comment --comment \"{scope}: allow {destination.Name}\" -j RETURN");
                }
            }
            rules.Add($"-A {chain} -i {network.Bridge} -m comment --comment \"{scope}: deny other egress\" {Reject}");
        }
    }

    private static void RenderRun(
        List<string> rules, ResolvedZone zone, ZoneNetwork network, string scope, IReadOnlyList<ZoneNetwork> allNetworks)
    {
        // A denial covers the whole address: a destination's ports describe
        // what a work zone may use, never what a run zone may still reach.
        // FORWARD sees a connection after Docker's port forwarding rewrote its
        // destination, so the denial matches the original destination: an app
        // that dials a published port of the control plane is still denied.
        foreach (var destination in zone.Denied)
        {
            foreach (var address in destination.Addresses)
                rules.Add($"-A {ForwardChain} -i {network.Bridge} -m conntrack --ctorigdst {address} "
                          + $"-m comment --comment \"{scope}: deny {destination.Name}\" {Reject}");
        }
        foreach (var destination in zone.Denied)
        {
            foreach (var address in destination.Addresses)
                rules.Add($"-A {InputChain} -i {network.Bridge} -d {address} "
                          + $"-m comment --comment \"{scope}: deny {destination.Name}\" {Reject}");
        }
        // Each tenant network is its own island: its own bridge, and no path
        // into another tenant, a work bridge, a Compose network (br-*) or
        // Docker's default bridge on the same host.
        rules.Add($"-A {ForwardChain} -i {network.Bridge} -o {network.Bridge} "
                  + $"-m comment --comment \"{scope}: own network\" -j RETURN");
        foreach (var other in allNetworks.Where(other => other.Bridge != network.Bridge
                                                         && !other.Bridge.StartsWith("br-", StringComparison.Ordinal)))
            rules.Add($"-A {ForwardChain} -i {network.Bridge} -o {other.Bridge} "
                      + $"-m comment --comment \"{scope}: isolate from {other.Name}\" -j DROP");
        rules.Add($"-A {ForwardChain} -i {network.Bridge} -o br-+ "
                  + $"-m comment --comment \"{scope}: isolate from other bridges\" -j DROP");
        rules.Add($"-A {ForwardChain} -i {network.Bridge} -o docker0 "
                  + $"-m comment --comment \"{scope}: isolate from docker0\" -j DROP");
        // An app reaches the host only for name resolution; everything else on
        // the host, including services behind the bridge gateway address, is
        // closed to the run zone.
        rules.Add($"-A {InputChain} -i {network.Bridge} -p udp --dport 53 -m comment --comment \"{scope}: host dns\" -j RETURN");
        rules.Add($"-A {InputChain} -i {network.Bridge} -p tcp --dport 53 -m comment --comment \"{scope}: host dns\" -j RETURN");
        rules.Add($"-A {InputChain} -i {network.Bridge} -m comment --comment \"{scope}: no host services\" {Reject}");
    }

    private static IEnumerable<string> PortMatches(ResolvedZoneDestination destination)
    {
        if (destination.Ports.Count == 0)
        {
            yield return string.Empty;
            yield break;
        }
        var ports = destination.Ports.Order().ToList();
        foreach (var protocol in destination.Protocols)
            yield return ports.Count == 1
                ? $" -p {protocol} --dport {ports[0]}"
                : $" -p {protocol} -m multiport --dports {string.Join(',', ports)}";
    }

    /// <summary>
    /// The zone resolver's dnsmasq configuration: it listens on the work
    /// bridges only and forwards only the allowed names (and their
    /// subdomains), one <c>server=/name/upstream</c> line per name and
    /// upstream. There is no general <c>server=</c> line, so any other name
    /// gets REFUSED and the resolver is no DNS channel out of the work zone.
    /// Every IPv4 answer for an allowed name lands in that destination's ipset.
    /// The set's own timeout keeps an address allowed after its TTL ran out, so
    /// a container that reuses an older answer still connects. dnsmasq adds to
    /// the set only on an upstream reply, not on a cache hit, so its cache
    /// keeps an answer for at most half the set timeout: an address served
    /// from the cache was added less than half a timeout ago.
    /// </summary>
    private static string? RenderResolverConfig(ResolvedZonePolicy policy, IReadOnlyList<ZoneNetworkSpec> networks)
    {
        if (policy.Resolver is not { } resolver) return null;
        var work = networks.Where(network => network.Kind == ZoneKinds.Work).ToList();
        if (work.Count == 0) return null;
        var setsByName = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var destination in policy.Zones.Where(zone => zone.Kind == ZoneKinds.Work).SelectMany(zone => zone.Allowed))
        {
            foreach (var name in destination.Names)
            {
                if (!setsByName.TryGetValue(name, out var sets)) setsByName[name] = sets = new SortedSet<string>(StringComparer.Ordinal);
                sets.Add(destination.IpSet!);
            }
        }
        var text = new StringBuilder();
        text.Append("# Rendered by agent-host zone-policy from policy ").Append(policy.Version)
            .Append(" (sha256:").Append(policy.PolicySha256).Append(").\n");
        text.Append("# Zone resolver (dnsmasq): work containers use it with --dns <bridge gateway>.\n");
        text.Append("port=53\nbind-dynamic\nexcept-interface=lo\n");
        foreach (var network in work) text.Append("interface=").Append(network.Bridge).Append('\n');
        text.Append("no-resolv\nno-hosts\n");
        text.Append("pid-file=").Append(ResolverPidFile).Append('\n');
        text.Append("max-cache-ttl=").Append(MaxCacheTtl(resolver.SetTimeoutSeconds)).Append('\n');
        foreach (var (name, sets) in setsByName)
        {
            foreach (var upstream in resolver.Upstreams)
                text.Append("server=/").Append(name).Append('/').Append(upstream).Append('\n');
            text.Append("ipset=/").Append(name).Append('/').Append(string.Join(',', sets)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The longest time dnsmasq may answer from its cache: half the set timeout.</summary>
    public static int MaxCacheTtl(int setTimeoutSeconds) => setTimeoutSeconds / 2;

    /// <summary>The <c>docker network create</c> arguments for a zone network.</summary>
    public static IReadOnlyList<string> NetworkCreateArguments(ZoneNetworkSpec network)
    {
        var args = new List<string>
        {
            "network", "create", "--driver", "bridge", "--ipv6=false",
            "--subnet", network.Subnet,
            "--gateway", ZonePolicyParser.GatewayOf(network.Subnet),
            "--opt", $"com.docker.network.bridge.name={network.Bridge}",
            "--opt", $"com.docker.network.bridge.enable_icc={(network.InterContainer ? "true" : "false")}",
            "--label", $"{ZoneLabel}={network.Zone}",
        };
        if (network.Tenant is { } tenant) args.AddRange(["--label", $"{TenantLabel}={tenant}"]);
        args.Add(network.Name);
        return args;
    }

    /// <summary>The human-readable apply plan; the dry run prints it unchanged.</summary>
    public static string RenderPlan(ZoneRuleSet rules)
    {
        var plan = new StringBuilder();
        plan.Append("zone policy ").Append(rules.PolicyVersion)
            .Append(" (policy sha256:").Append(rules.PolicySha256)
            .Append(", rules sha256:").Append(rules.RulesSha256).Append(")\n");
        var step = 1;
        foreach (var network in rules.Networks)
        {
            plan.Append(step++).Append(". ensure docker network ").Append(network.Name)
                .Append(" (zone ").Append(network.Zone).Append(", bridge ").Append(network.Bridge)
                .Append(", subnet ").Append(network.Subnet)
                .Append(", inter-container ").Append(network.InterContainer ? "on" : "off").Append(")\n");
            plan.Append("   when missing: docker ").Append(string.Join(' ', NetworkCreateArguments(network))).Append('\n');
        }
        foreach (var set in rules.IpSets)
            plan.Append(step++).Append(". ensure ipset ").Append(set.Name).Append(" (allowed ").Append(set.Destination)
                .Append(", entries expire ").Append(set.TimeoutSeconds).Append("s after the last answer)\n")
                .Append("   when missing: ipset ").Append(string.Join(' ', set.CreateArguments)).Append('\n');
        // The rules load before the resolver starts: until it answers, the
        // sets stay empty and every named destination is denied.
        foreach (var (family, count) in new[] { (Ipv4, rules.RuleCount), (Ipv6, rules.Rule6Count) })
        {
            plan.Append(step++).Append(". ").Append(Iptables(family)).Append("-restore --noflush: replace ")
                .Append(ForwardChain).Append(" and ").Append(InputChain).Append(" with ").Append(count)
                .Append(family == Ipv4 ? " rules\n" : " rules that drop zone-bridge traffic (skipped on a kernel without IPv6)\n");
            foreach (var jump in rules.Jumps.Where(jump => jump.Family == family))
                plan.Append(step++).Append(". ensure ").Append(jump.Chain).Append(" jumps to ").Append(jump.Target)
                    .Append(" (").Append(jump.Executable).Append(" -I ").Append(jump.Chain).Append(" 1 -j ").Append(jump.Target)
                    .Append(jump.FallbackChain is { } fallback ? $"; {fallback} when {jump.Chain} does not exist" : string.Empty)
                    .Append(")\n");
        }
        if (rules.ResolverConfig is not null)
            plan.Append(step++).Append(". write the zone resolver configuration and (re)start ")
                .Append(ZonePolicyApplier.ResolverUnit).Append(" when it changed or is not running\n");
        plan.Append(step).Append(". verify with probe containers, then record the applied state\n");
        return plan.ToString();
    }
}
