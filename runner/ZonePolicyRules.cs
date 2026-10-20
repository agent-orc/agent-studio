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
public sealed record ZoneChainJump(string Chain, string Target);

/// <summary>Everything one apply writes to the host, rendered without side effects.</summary>
public sealed record ZoneRuleSet(
    string PolicyVersion,
    string PolicySha256,
    string Iptables,
    string RulesSha256,
    int RuleCount,
    IReadOnlyList<ZoneNetworkSpec> Networks,
    IReadOnlyList<ZoneChainJump> Jumps);

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
/// Traffic a zone chain does not reject returns to the calling chain.
/// </summary>
public static class ZonePolicyRules
{
    public const string ForwardChain = "AGENT-ZONE-FWD";
    public const string InputChain = "AGENT-ZONE-IN";
    public const string DockerUserChain = "DOCKER-USER";
    public const string InputBuiltInChain = "INPUT";
    public const string DefaultProbeImage = "bash:5.2";
    public const string ZoneLabel = "org.agent-studio.zone";
    public const string TenantLabel = "org.agent-studio.tenant";
    private const string Reject = "-j REJECT --reject-with icmp-admin-prohibited";

    public static ZoneRuleSet Render(ResolvedZonePolicy policy)
    {
        var rules = new List<string>
        {
            $"-A {ForwardChain} -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN",
            $"-A {InputChain} -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN",
        };
        var allNetworks = policy.Zones.SelectMany(zone => zone.Networks).ToList();
        var networks = new List<ZoneNetworkSpec>();
        foreach (var zone in policy.Zones)
        {
            foreach (var network in zone.Networks)
            {
                var scope = $"{zone.Name}/{network.Name}";
                rules.Add($"# zone {zone.Name} ({zone.Kind}) network {network.Name} bridge {network.Bridge} subnet {network.Subnet}"
                          + (network.Tenant is { } tenant ? $" tenant {tenant}" : string.Empty));
                if (zone.Kind == ZoneKinds.Work)
                    RenderWork(rules, zone, network, scope);
                else
                    RenderRun(rules, zone, network, scope, allNetworks);
                networks.Add(new ZoneNetworkSpec(zone.Name, zone.Kind, network.Name, network.Bridge, network.Subnet,
                    network.Tenant, InterContainer: zone.Kind == ZoneKinds.Run));
            }
        }

        var text = new StringBuilder();
        text.Append("# Rendered by agent-host zone-policy from policy ").Append(policy.Version)
            .Append(" (sha256:").Append(policy.PolicySha256).Append(").\n");
        text.Append("# Load with iptables-restore --noflush; only the AGENT-ZONE-* chains are replaced.\n");
        text.Append("*filter\n");
        text.Append(':').Append(ForwardChain).Append(" - [0:0]\n");
        text.Append(':').Append(InputChain).Append(" - [0:0]\n");
        foreach (var rule in rules) text.Append(rule).Append('\n');
        text.Append("COMMIT\n");
        var iptables = text.ToString();
        return new ZoneRuleSet(
            policy.Version,
            policy.PolicySha256,
            iptables,
            ZonePolicyParser.Sha256(iptables),
            rules.Count(rule => rule.StartsWith("-A ", StringComparison.Ordinal)),
            networks,
            [new ZoneChainJump(DockerUserChain, ForwardChain), new ZoneChainJump(InputBuiltInChain, InputChain)]);
    }

    private static void RenderWork(List<string> rules, ResolvedZone zone, ZoneNetwork network, string scope)
    {
        // No inbound: a published port or another container cannot open a
        // connection into the work bridge. Replies to outbound connections
        // already returned at the conntrack rule above.
        rules.Add($"-A {ForwardChain} -o {network.Bridge} -m comment --comment \"{scope}: no inbound\" -j DROP");
        foreach (var chain in new[] { ForwardChain, InputChain })
        {
            foreach (var destination in zone.Allowed)
            {
                foreach (var address in destination.Addresses)
                {
                    foreach (var match in PortMatches(destination))
                        rules.Add($"-A {chain} -i {network.Bridge} -d {address}{match} "
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
        foreach (var chain in new[] { ForwardChain, InputChain })
        {
            foreach (var destination in zone.Denied)
            {
                foreach (var address in destination.Addresses)
                    rules.Add($"-A {chain} -i {network.Bridge} -d {address} "
                              + $"-m comment --comment \"{scope}: deny {destination.Name}\" {Reject}");
            }
        }
        // Each tenant network is its own island: no path into another tenant
        // or into a work bridge on the same host.
        foreach (var other in allNetworks.Where(other => other.Bridge != network.Bridge))
            rules.Add($"-A {ForwardChain} -i {network.Bridge} -o {other.Bridge} "
                      + $"-m comment --comment \"{scope}: isolate from {other.Name}\" -j DROP");
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

    /// <summary>The <c>docker network create</c> arguments for a zone network.</summary>
    public static IReadOnlyList<string> NetworkCreateArguments(ZoneNetworkSpec network)
    {
        var args = new List<string>
        {
            "network", "create", "--driver", "bridge",
            "--subnet", network.Subnet,
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
        plan.Append(step++).Append(". iptables-restore --noflush: replace ").Append(ForwardChain).Append(" and ")
            .Append(InputChain).Append(" with ").Append(rules.RuleCount).Append(" rules\n");
        foreach (var jump in rules.Jumps)
            plan.Append(step++).Append(". ensure ").Append(jump.Chain).Append(" jumps to ").Append(jump.Target)
                .Append(" (iptables -I ").Append(jump.Chain).Append(" 1 -j ").Append(jump.Target).Append(")\n");
        plan.Append(step).Append(". verify with probe containers, then record the applied state\n");
        return plan.ToString();
    }
}
