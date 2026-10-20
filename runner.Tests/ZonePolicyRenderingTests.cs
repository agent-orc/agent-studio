using System.Text.Json.Nodes;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: the checked-in zone policy parses, validates and renders to the
/// rules in the golden files. Set ZONE_POLICY_UPDATE_GOLDEN=1 to rewrite the
/// golden files after an intended policy or renderer change, then review the
/// diff like any other rule change.
/// </summary>
public sealed class ZonePolicyRenderingTests
{
    private static string GoldenPath(string name)
        => Path.Combine(ZonePolicyTestFiles.RepoRoot(), "runner.Tests", "Fixtures", "zone-policy", name);

    private static void AssertGolden(string name, string actual)
    {
        var path = GoldenPath(name);
        if (Environment.GetEnvironmentVariable("ZONE_POLICY_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }
        Assert.True(File.Exists(path), $"golden file {path} is missing; run with ZONE_POLICY_UPDATE_GOLDEN=1");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public async Task Checked_in_policy_renders_the_golden_rules_and_plan()
    {
        var policy = await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy);
        var rules = ZonePolicyRules.Render(policy);

        AssertGolden("zone-policy.rules.golden", rules.Iptables.Replace(policy.PolicySha256, "<policy-sha256>"));
        AssertGolden("zone-policy.rules6.golden", rules.Ip6tables.Replace(policy.PolicySha256, "<policy-sha256>"));
        AssertGolden("zone-policy.dnsmasq.golden", rules.ResolverConfig!.Replace(policy.PolicySha256, "<policy-sha256>"));
        AssertGolden("zone-policy.plan.golden", ZonePolicyRules.RenderPlan(rules)
            .Replace(policy.PolicySha256, "<policy-sha256>").Replace(rules.RulesSha256, "<rules-sha256>"));
    }

    [Fact]
    public async Task Ipv6_payload_drops_every_packet_on_each_zone_bridge()
    {
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        var lines = rules.Ip6tables.Split('\n').Where(line => line.StartsWith("-A ", StringComparison.Ordinal)).ToList();

        Assert.Equal(6, rules.Rule6Count);
        Assert.All(lines, line => Assert.EndsWith("no IPv6\" -j DROP", line));
        foreach (var bridge in new[] { "br-agent-work", "br-run-default" })
        {
            Assert.Contains(lines, line => line.StartsWith($"-A AGENT-ZONE-FWD -i {bridge} ", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith($"-A AGENT-ZONE-FWD -o {bridge} ", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith($"-A AGENT-ZONE-IN -i {bridge} ", StringComparison.Ordinal));
        }
        Assert.Contains(new ZoneChainJump("ipv6", "DOCKER-USER", "AGENT-ZONE-FWD", "FORWARD"), rules.Jumps);
        Assert.Contains(new ZoneChainJump("ipv6", "INPUT", "AGENT-ZONE-IN"), rules.Jumps);
        var create = ZonePolicyRules.NetworkCreateArguments(rules.Networks[0]);
        Assert.Contains("--ipv6=false", create);
        Assert.Equal("172.30.0.1", create[create.ToList().IndexOf("--gateway") + 1]);
    }

    [Fact]
    public async Task Work_zone_has_no_inbound_and_ends_in_a_default_deny_on_both_chains()
    {
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        var lines = rules.Iptables.Split('\n');

        Assert.Equal("-A AGENT-ZONE-FWD -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN",
            lines.First(line => line.StartsWith("-A ", StringComparison.Ordinal)));
        Assert.Contains("-A AGENT-ZONE-FWD -o br-agent-work -m comment --comment \"work/agent-work: no inbound\" -j DROP", lines);
        foreach (var chain in new[] { "AGENT-ZONE-FWD", "AGENT-ZONE-IN" })
        {
            var work = lines.Where(line => line.StartsWith($"-A {chain} -i br-agent-work", StringComparison.Ordinal)).ToList();
            Assert.EndsWith("-j REJECT --reject-with icmp-admin-prohibited", work[^1]);
            Assert.Contains("deny other egress", work[^1]);
            Assert.All(work[..^1], line => Assert.EndsWith("-j RETURN", line));
        }
        // Containers resolve only through the zone resolver on the bridge
        // gateway, never an upstream resolver directly; named destinations
        // match by their ipset, Git over SSH and HTTPS.
        Assert.Contains("-A AGENT-ZONE-IN -i br-agent-work -d 172.30.0.1/32 -p udp --dport 53 "
                        + "-m comment --comment \"work/agent-work: zone resolver\" -j RETURN", lines);
        Assert.Contains("-A AGENT-ZONE-IN -i br-agent-work -d 172.30.0.1/32 -p tcp --dport 53 "
                        + "-m comment --comment \"work/agent-work: zone resolver\" -j RETURN", lines);
        Assert.DoesNotContain(lines, line => line.Contains("192.0.2.53", StringComparison.Ordinal));
        Assert.Contains("-A AGENT-ZONE-FWD -i br-agent-work -m set --match-set agent-zone-git-hosts dst -p tcp -m multiport "
                        + "--dports 22,443 -m comment --comment \"work/agent-work: allow git-hosts\" -j RETURN", lines);
        Assert.DoesNotContain(lines, line => line.Contains("-i br-agent-work -d 198.", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("br-agent-work", StringComparison.Ordinal)
                                             && line.Contains("secret-store", StringComparison.Ordinal));
        Assert.Equal(["agent-zone-control-plane", "agent-zone-git-hosts", "agent-zone-package-registries", "agent-zone-model-providers"],
            rules.IpSets.Select(set => set.Name));
        Assert.All(rules.IpSets, set => Assert.Equal(86400, set.TimeoutSeconds));
    }

    [Fact]
    public async Task A_literal_address_stays_a_literal_rule_next_to_the_set_of_the_names()
    {
        var environment = new Dictionary<string, string>(ZonePolicyTestFiles.Environment)
        {
            ["AGENT_ZONE_CONTROL_PLANE_HOSTS"] = "task-server.example.internal, 10.40.0.0/24",
        };
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(
            ZonePolicyTestFiles.CheckedInPolicy, environment: environment));
        var allow = rules.Iptables.Split('\n')
            .Where(line => line.StartsWith("-A AGENT-ZONE-FWD -i br-agent-work", StringComparison.Ordinal)
                           && line.Contains("allow control-plane", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, allow.Count);
        Assert.StartsWith("-A AGENT-ZONE-FWD -i br-agent-work -d 10.40.0.0/24 -p tcp -m multiport --dports 443,5071 ", allow[0]);
        Assert.StartsWith("-A AGENT-ZONE-FWD -i br-agent-work -m set --match-set agent-zone-control-plane dst ", allow[1]);
        Assert.Contains("ipset=/task-server.example.internal/agent-zone-control-plane\n", rules.ResolverConfig);
        Assert.DoesNotContain("10.40.0.0", rules.ResolverConfig);
    }

    [Fact]
    public async Task Run_zone_denies_control_plane_and_secret_store_and_isolates_tenants()
    {
        var json = JsonNode.Parse(ZonePolicyTestFiles.CheckedInPolicy, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        json["zones"]![1]!["networks"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "agent-run-acme", "bridge": "br-run-acme", "subnet": "172.31.1.0/24", "tenant": "acme" }"""));
        var policy = await ZonePolicyTestFiles.ResolveAsync(json.ToJsonString());
        var lines = ZonePolicyRules.Render(policy).Iptables.Split('\n');

        foreach (var bridge in new[] { "br-run-default", "br-run-acme" })
        {
            // FORWARD matches the destination before Docker's port forwarding, INPUT the host address.
            foreach (var (chain, match) in new[] { ("AGENT-ZONE-FWD", "-m conntrack --ctorigdst"), ("AGENT-ZONE-IN", "-d") })
            {
                Assert.Contains(lines, line => line.StartsWith($"-A {chain} -i {bridge} {match} 192.0.2.80/32 ", StringComparison.Ordinal)
                                               && line.EndsWith("deny secret-store\" -j REJECT --reject-with icmp-admin-prohibited", StringComparison.Ordinal));
                Assert.Contains(lines, line => line.StartsWith($"-A {chain} -i {bridge} {match} 10.20.0.0/16 ", StringComparison.Ordinal));
                Assert.Contains(lines, line => line.StartsWith($"-A {chain} -i {bridge} {match} ", StringComparison.Ordinal)
                                               && line.Contains("deny control-plane", StringComparison.Ordinal));
            }
            Assert.DoesNotContain(lines, line => line.StartsWith($"-A AGENT-ZONE-FWD -i {bridge} -d ", StringComparison.Ordinal));
            var tenant = bridge == "br-run-acme" ? "acme" : "default";
            var isolation = lines.Where(line => line.StartsWith($"-A AGENT-ZONE-FWD -i {bridge} -o ", StringComparison.Ordinal)).ToList();
            Assert.Equal(
            [
                $"-A AGENT-ZONE-FWD -i {bridge} -o {bridge} -m comment --comment \"run/agent-run-{tenant}: own network\" -j RETURN",
                $"-A AGENT-ZONE-FWD -i {bridge} -o br-+ -m comment --comment \"run/agent-run-{tenant}: isolate from other bridges\" -j DROP",
                $"-A AGENT-ZONE-FWD -i {bridge} -o docker0 -m comment --comment \"run/agent-run-{tenant}: isolate from docker0\" -j DROP",
            ], isolation);
            Assert.Equal($"-A AGENT-ZONE-IN -i {bridge} -m comment --comment \"run/agent-run-{tenant}: no host services\" -j REJECT --reject-with icmp-admin-prohibited",
                lines.Last(line => line.StartsWith($"-A AGENT-ZONE-IN -i {bridge}", StringComparison.Ordinal)));
        }
        var firewall = new FakeZoneFirewall(ZonePolicyRules.Render(policy).Networks, string.Join('\n', lines));
        Assert.Equal(ZoneProbeOutcome.TimedOut, firewall.Connect("br-run-default", System.Net.IPAddress.Parse("172.31.1.5"), 80));
        Assert.Equal(ZoneProbeOutcome.TimedOut, firewall.Connect("br-run-acme", System.Net.IPAddress.Parse("172.31.0.5"), 80));
        Assert.Equal(ZoneProbeOutcome.TimedOut, firewall.Connect("br-run-acme", System.Net.IPAddress.Parse("172.30.0.5"), 80));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-run-acme", System.Net.IPAddress.Parse("172.31.1.7"), 80));
    }

    [Fact]
    public async Task Run_zone_denial_covers_every_port_of_the_control_plane_and_secret_store()
    {
        var resolver = new FakeZoneResolver();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero));
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver));
        var sets = new FakeIpSets(clock);
        foreach (var set in rules.IpSets) sets.Create(set.Name, set.TimeoutSeconds);
        var firewall = new FakeZoneFirewall(rules.Networks, rules.Iptables, sets);
        var controlPlane = new FakeZoneDnsmasq(rules.ResolverConfig!, resolver, sets).Resolve("task-server.example.internal")[0];

        // The control plane's ports describe the work zone's allowance only;
        // SSH or any other port on the same address stays closed to apps.
        foreach (var port in new[] { 22, 443, 5071, 8443 })
        {
            Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-run-default", controlPlane, port));
            Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-run-default", System.Net.IPAddress.Parse("10.20.33.4"), port));
        }
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", controlPlane, 443));
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-agent-work", controlPlane, 22));
        // The bridge gateway is the host itself: only DNS is open to the run zone.
        var gateway = System.Net.IPAddress.Parse("172.31.0.1");
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-run-default", gateway, 5071));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-run-default", gateway, 53));
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-agent-work", System.Net.IPAddress.Parse("172.30.0.1"), 5071));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", System.Net.IPAddress.Parse("172.30.0.1"), 53));
    }

    [Fact]
    public async Task Run_zone_denial_holds_after_docker_forwards_a_published_port()
    {
        var resolver = new FakeZoneResolver();
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver));
        var firewall = new FakeZoneFirewall(rules.Networks, rules.Iptables);
        var controlPlane = resolver.Resolve("task-server.example.internal")[0];
        // The control plane publishes 443 from a Compose container: Docker
        // rewrites the destination to the container before FORWARD runs.
        var container = System.Net.IPAddress.Parse("172.18.0.5");

        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-run-default", controlPlane, 443, translated: container));
        // Without the original destination the app would reach the container;
        // the Compose and default bridges stay closed to it as well.
        Assert.Equal(ZoneProbeOutcome.TimedOut, firewall.Connect("br-run-default", container, 443));
        Assert.Equal(ZoneProbeOutcome.TimedOut, firewall.Connect("br-run-default", System.Net.IPAddress.Parse("172.17.0.2"), 8080));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-run-default", System.Net.IPAddress.Parse("203.0.113.40"), 443));
    }

    [Fact]
    public async Task Rendering_is_deterministic_and_allowed_names_do_not_depend_on_apply_time_dns()
    {
        var first = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        var second = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        Assert.Equal(first.Iptables, second.Iptables);
        Assert.Equal(first.RulesSha256, second.RulesSha256);

        // A CDN that moves github.com changes nothing on the host: the set follows the containers' answers.
        var moved = new FakeZoneResolver();
        moved.Overrides["github.com"] = ["203.0.113.7", "203.0.113.5"];
        var rotated = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, moved));
        Assert.Equal(first.RulesSha256, rotated.RulesSha256);
        Assert.DoesNotContain("203.0.113.", rotated.Iptables);

        // A denied name is resolved at apply time, so its move is a rule change.
        moved.Overrides["task-server.example.internal"] = ["203.0.113.9"];
        var denied = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, moved));
        Assert.NotEqual(first.RulesSha256, denied.RulesSha256);
        Assert.Contains("-A AGENT-ZONE-FWD -i br-run-default -m conntrack --ctorigdst 203.0.113.9/32 ", denied.Iptables);
    }

    [Fact]
    public async Task Resolver_configuration_serves_the_work_bridges_and_maps_each_name_to_its_set()
    {
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        var lines = rules.ResolverConfig!.Split('\n');

        Assert.Contains("interface=br-agent-work", lines);
        Assert.DoesNotContain("interface=br-run-default", lines);
        Assert.Contains("except-interface=lo", lines);
        Assert.Contains("bind-dynamic", lines);
        Assert.Equal(["server=192.0.2.53", "server=192.0.2.54"], lines.Where(line => line.StartsWith("server=", StringComparison.Ordinal)));
        Assert.Contains("ipset=/github.com/agent-zone-git-hosts", lines);
        Assert.Contains("ipset=/registry.npmjs.org/agent-zone-package-registries", lines);
        Assert.Contains("ipset=/api.anthropic.com/agent-zone-model-providers", lines);

        var runOnly = await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, zones: ["run"]);
        Assert.Null(runOnly.Resolver);
        Assert.Null(ZonePolicyRules.Render(runOnly).ResolverConfig);
        Assert.Empty(ZonePolicyRules.Render(runOnly).IpSets);
    }

    [Fact]
    public async Task A_name_two_destinations_list_fills_both_sets()
    {
        var json = Policy();
        json["destinations"]!["package-registries"]!["hosts"]!.AsArray().Add("github.com");
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(json.ToJsonString()));

        Assert.Contains("ipset=/github.com/agent-zone-git-hosts,agent-zone-package-registries\n", rules.ResolverConfig);
    }

    [Fact]
    public async Task Zone_filter_resolves_only_the_destinations_the_selected_zone_uses()
    {
        var environment = ZonePolicyTestFiles.Environment
            .Where(item => item.Key != "AGENT_ZONE_SECRET_STORE_HOSTS")
            .ToDictionary(item => item.Key, item => item.Value);

        var work = await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy,
            environment: environment, zones: ["work"]);
        Assert.Equal(["work"], work.Zones.Select(zone => zone.Name));
        Assert.Equal(["agent-work"], ZonePolicyRules.Render(work).Networks.Select(network => network.Name));

        var all = await Assert.ThrowsAsync<ZonePolicyException>(() =>
            ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, environment: environment));
        Assert.Contains(all.Errors, error => error.Contains("${AGENT_ZONE_SECRET_STORE_HOSTS} is not set", StringComparison.Ordinal));
        var unknown = await Assert.ThrowsAsync<ZonePolicyException>(() =>
            ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, zones: ["build"]));
        Assert.Contains("zone 'build' is not in the policy", unknown.Errors);
    }

    [Fact]
    public async Task Resolution_rejects_ipv6_unresolvable_denied_hosts_and_non_canonical_ranges()
    {
        var resolver = new FakeZoneResolver();
        resolver.Missing.Add("task-server.example.internal");
        // An allowed-only name is never resolved on the host: the zone resolver handles it.
        resolver.Missing.Add("registry.npmjs.org");
        var environment = new Dictionary<string, string>(ZonePolicyTestFiles.Environment)
        {
            ["AGENT_ZONE_DNS_RESOLVERS"] = "2001:db8::53 192.0.2.0/24",
            ["AGENT_ZONE_SECRET_STORE_HOSTS"] = "10.20.0.1/16 2001:db8::80 vault\"evil",
        };

        var error = await Assert.ThrowsAsync<ZonePolicyException>(() =>
            ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver, environment));

        Assert.Contains(error.Errors, item => item.Contains("resolver: upstream '2001:db8::53' is not an IPv4 address", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("resolver: upstream '192.0.2.0/24' is not an IPv4 address", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("'2001:db8::80' is not an IPv4 address", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("'task-server.example.internal' does not resolve", StringComparison.Ordinal));
        Assert.DoesNotContain(error.Errors, item => item.Contains("registry.npmjs.org", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("'10.20.0.1/16' is not a canonical IPv4 CIDR", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("'vault\\\"evil' is neither an IPv4 address nor a DNS name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("10.0.0.0/08", "10.0.0.0/8")]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("192.0.2.7", "192.0.2.7/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("10.0.0.1/8", null)]
    [InlineData("010.0.0.0/8", null)]
    [InlineData("10.0.0/24", null)]
    [InlineData("1", null)]
    [InlineData("10.0.0.0/ 8", null)]
    [InlineData("10.0.0.0/33", null)]
    public void Cidrs_normalize_to_the_canonical_form(string value, string? expected)
    {
        var parsed = ZonePolicyParser.TryNormalizeCidr(value, out var cidr);
        Assert.Equal(expected is not null, parsed);
        if (expected is not null) Assert.Equal(expected, cidr);
    }

    public static TheoryData<string, string, string> InvalidPolicies => new()
    {
        { "$.schemaVersion", "2", "schemaVersion 2 is not supported" },
        { "$.version", "\"\"", "version is required" },
        { "$.zones[0].egress.allow", "[\"git-hosts\", \"secret-store\"]", "a work zone must not reach the secret store 'secret-store'" },
        { "$.zones[0].egress.default", "\"allow\"", "a work zone denies egress by default" },
        { "$.zones[0].egress.allow", "[\"git-hosts\", \"artifactory\"]", "unknown destination 'artifactory'" },
        { "$.zones[1].egress.deny", "[\"control-plane\"]", "a run zone must deny secret-store destination 'secret-store'" },
        { "$.zones[1].networks[0].tenant", "null", "run network agent-run-default must name its tenant" },
        { "$.zones[1].networks[0].subnet", "\"172.30.0.128/25\"", "overlaps agent-work" },
        { "$.zones[1].networks[0].subnet", "\"172.31.0.1/24\"", "is not an IPv4 CIDR" },
        { "$.zones[1].networks[0].bridge", "\"br-agent-run-default\"", "at most 15 characters" },
        { "$.zones[1].networks[0].bridge", "\"br-agent-work\"", "must be a unique interface name" },
        { "$.zones[0].verify.blocked", "[]", "at least one reachable and one blocked probe" },
        { "$.zones[1].verify.blocked", "[{ \"destination\": \"secret-store\" }]", "needs a port because the destination has none" },
        { "$.zones[0].kind", "\"build\"", "kind must be 'work' or 'run'" },
        { "$.destinations.git-hosts.protocols", "[\"icmp\"]", "protocols must be tcp or udp" },
        { "$.destinations.git-hosts.ports", "[0]", "ports must be between 1 and 65535" },
        { "$.destinations.git-hosts.class", "\"internal\"", "class must be egress, control-plane or secret-store" },
        { "$.zones[0].proxy", "\"squid\"", "not a valid zone policy document" },
        { "$.resolver", "null", "resolver is required" },
        { "$.resolver.upstreams", "[]", "resolver.upstreams needs at least one resolver" },
        { "$.resolver.setTimeoutSeconds", "30", "resolver.setTimeoutSeconds must be between 60 and 2147483" },
        { "$.verification.probeImage", "\"bash:5.2\"", "verification.probeImage must be pinned by digest" },
        { "$.verification.controlNetwork", "\"agent-work\"", "verification.controlNetwork agent-work is a zone network" },
        { "$.zones[0].name", "\"Work\"", "names match ^[a-z0-9][a-z0-9-]{0,47}$" },
        { "$.zones[0].name", "\"wo\\\"rk\"", "zone wo\\\"rk: names match" },
        { "$.zones[0].name", "\"wo\\nrk\"", "zone wo\\nrk: names match" },
        { "$.zones[0].networks[0].name", "\"agent-work\\\" -j ACCEPT\"", "network name agent-work\\\" -j ACCEPT: names match" },
        { "$.zones[0].networks[0].name", "\"agent-work\\n-A INPUT -j ACCEPT\"", "network name agent-work\\n-A INPUT -j ACCEPT: names match" },
        { "$.zones[1].networks[0].tenant", "\"acme\\\"\"", "tenant acme\\\": names match" },
        { "$.zones[1].networks[0].tenant", "\"acme\\ncorp\"", "tenant acme\\ncorp: names match" },
    };

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public void Validation_names_the_broken_rule(string path, string value, string expected)
    {
        var json = JsonNode.Parse(ZonePolicyTestFiles.CheckedInPolicy, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        Set(json, path, value is "null" ? null : JsonNode.Parse(value));

        var error = Assert.Throws<ZonePolicyException>(() => ZonePolicyParser.Parse(json.ToJsonString()));

        Assert.Contains(error.Errors, item => item.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("git\"hosts", "git\\\"hosts")]
    [InlineData("git\nhosts", "git\\nhosts")]
    [InlineData("Git-Hosts", "Git-Hosts")]
    [InlineData("-git-hosts", "-git-hosts")]
    public void A_destination_name_with_a_quote_a_newline_or_capitals_is_rejected(string name, string shown)
    {
        var json = Policy();
        var destinations = json["destinations"]!.AsObject();
        destinations[name] = destinations["git-hosts"]!.DeepClone();
        json["zones"]![0]!["egress"]!["allow"]!.AsArray().Add(name);

        var error = Assert.Throws<ZonePolicyException>(() => ZonePolicyParser.Parse(json.ToJsonString()));

        Assert.Contains(error.Errors, item => item == $"destination {shown}: {"names match ^[a-z0-9][a-z0-9-]{0,47}$ (they go verbatim into the rule comments)"}");
        Assert.DoesNotContain(error.Errors, item => item.Contains('\n'));
    }

    [Fact]
    public void An_allowed_destination_name_must_fit_the_ipset_name_limit()
    {
        var json = Policy();
        var destinations = json["destinations"]!.AsObject();
        destinations["internal-artifact-mirror"] = destinations["git-hosts"]!.DeepClone();
        json["zones"]![0]!["egress"]!["allow"]!.AsArray().Add("internal-artifact-mirror");

        var error = Assert.Throws<ZonePolicyException>(() => ZonePolicyParser.Parse(json.ToJsonString()));

        Assert.Contains(error.Errors, item => item.Contains(
            "allowed destination 'internal-artifact-mirror' is too long for its ipset agent-zone-internal-artifact-mirror", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blocked_probe_without_a_port_covers_every_port_of_the_destination()
    {
        var policy = await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, new FakeZoneResolver());
        var run = policy.Zones.Single(zone => zone.Name == "run");

        Assert.Equal([443, 5071], run.Probes
            .Where(probe => probe is { Expectation: ZoneProbeExpectations.Blocked, Host: "control-plane" })
            .Select(probe => probe.Port));
        Assert.Equal([443, 443], run.Probes
            .Where(probe => probe is { Expectation: ZoneProbeExpectations.Blocked, Host: "secret-store" })
            .Select(probe => probe.Port));
    }

    [Fact]
    public void Checked_in_policy_is_valid_and_every_zone_proves_both_directions()
    {
        var document = ZonePolicyParser.Parse(ZonePolicyTestFiles.CheckedInPolicy);

        Assert.Empty(ZonePolicyParser.Validate(document));
        Assert.Equal(["work", "run"], document.Zones.Select(zone => zone.Kind));
        var work = document.Zones[0];
        Assert.Equal(["control-plane", "git-hosts", "package-registries", "model-providers"], work.Egress.Allow);
        Assert.Equal(["control-plane", "secret-store"], document.Zones[1].Egress.Deny);
        Assert.Equal(ZonePolicyRules.DefaultProbeImage, document.Verification!.ProbeImage);
        Assert.Equal("bridge", document.Verification.ControlNetwork);
    }

    private static JsonNode Policy()
        => JsonNode.Parse(ZonePolicyTestFiles.CheckedInPolicy, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;

    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var segments = path.TrimStart('$', '.').Replace("[", ".[").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var node = root;
        for (var index = 0; index < segments.Length - 1; index++)
            node = Step(node, segments[index])!;
        var last = segments[^1];
        if (last.StartsWith('[')) node.AsArray()[int.Parse(last.Trim('[', ']'))] = value;
        else node.AsObject()[last] = value;
    }

    private static JsonNode? Step(JsonNode node, string segment)
        => segment.StartsWith('[') ? node.AsArray()[int.Parse(segment.Trim('[', ']'))] : node[segment];
}
