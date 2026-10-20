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
        AssertGolden("zone-policy.plan.golden", ZonePolicyRules.RenderPlan(rules)
            .Replace(policy.PolicySha256, "<policy-sha256>").Replace(rules.RulesSha256, "<rules-sha256>"));
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
        // DNS resolvers are allowed over both protocols, Git over SSH and HTTPS.
        Assert.Contains(lines, line => line.Contains("-d 192.0.2.53/32 -p udp --dport 53", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("-d 192.0.2.53/32 -p tcp --dport 53", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("-m multiport --dports 22,443", StringComparison.Ordinal)
                                       && line.Contains("allow git-hosts", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("br-agent-work", StringComparison.Ordinal)
                                             && line.Contains("secret-store", StringComparison.Ordinal));
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
            foreach (var chain in new[] { "AGENT-ZONE-FWD", "AGENT-ZONE-IN" })
            {
                Assert.Contains(lines, line => line.StartsWith($"-A {chain} -i {bridge} -d 192.0.2.80/32 ", StringComparison.Ordinal)
                                               && line.EndsWith("deny secret-store\" -j REJECT --reject-with icmp-admin-prohibited", StringComparison.Ordinal));
                Assert.Contains(lines, line => line.StartsWith($"-A {chain} -i {bridge} -d 10.20.0.0/16 ", StringComparison.Ordinal));
                Assert.Contains(lines, line => line.StartsWith($"-A {chain} -i {bridge} -d ", StringComparison.Ordinal)
                                               && line.Contains("deny control-plane", StringComparison.Ordinal));
            }
            Assert.Contains(lines, line => line.StartsWith($"-A AGENT-ZONE-FWD -i {bridge} -o br-agent-work ", StringComparison.Ordinal)
                                           && line.EndsWith("-j DROP", StringComparison.Ordinal));
            Assert.Equal($"-A AGENT-ZONE-IN -i {bridge} -m comment --comment \"run/agent-run-{(bridge == "br-run-acme" ? "acme" : "default")}: no host services\" -j REJECT --reject-with icmp-admin-prohibited",
                lines.Last(line => line.StartsWith($"-A AGENT-ZONE-IN -i {bridge}", StringComparison.Ordinal)));
        }
        Assert.Contains(lines, line => line.StartsWith("-A AGENT-ZONE-FWD -i br-run-default -o br-run-acme ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("-A AGENT-ZONE-FWD -i br-run-acme -o br-run-default ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_zone_denial_covers_every_port_of_the_control_plane_and_secret_store()
    {
        var resolver = new FakeZoneResolver();
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver));
        var firewall = new FakeZoneFirewall(rules.Networks, rules.Iptables);
        var controlPlane = resolver.Resolve("task-server.example.internal")[0];

        // The control plane's ports describe the work zone's allowance only;
        // SSH or any other port on the same address stays closed to apps.
        foreach (var port in new[] { 22, 443, 5071, 8443 })
        {
            Assert.Equal(ZoneProbeOutcome.Refused, firewall.Connect("br-run-default", controlPlane, port));
            Assert.Equal(ZoneProbeOutcome.Refused, firewall.Connect("br-run-default", System.Net.IPAddress.Parse("10.20.33.4"), port));
        }
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", controlPlane, 443));
        Assert.Equal(ZoneProbeOutcome.Refused, firewall.Connect("br-agent-work", controlPlane, 22));
        // The bridge gateway is the host itself: only DNS is open to the run zone.
        var gateway = System.Net.IPAddress.Parse("172.31.0.1");
        Assert.Equal(ZoneProbeOutcome.Refused, firewall.Connect("br-run-default", gateway, 5071));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-run-default", gateway, 53));
        Assert.Equal(ZoneProbeOutcome.Refused, firewall.Connect("br-agent-work", System.Net.IPAddress.Parse("172.30.0.1"), 5071));
    }

    [Fact]
    public async Task Rendering_is_deterministic_and_follows_name_resolution()
    {
        var first = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        var second = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));
        Assert.Equal(first.Iptables, second.Iptables);
        Assert.Equal(first.RulesSha256, second.RulesSha256);

        var moved = new FakeZoneResolver();
        moved.Overrides["github.com"] = ["203.0.113.7", "203.0.113.5"];
        var changed = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, moved));
        Assert.NotEqual(first.RulesSha256, changed.RulesSha256);
        var github = changed.Iptables.Split('\n')
            .Where(line => line.StartsWith("-A AGENT-ZONE-FWD -i br-agent-work -d 203.0.113.", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, github.Count);
        Assert.Contains("203.0.113.5/32", github[0]);
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
    public async Task Resolution_rejects_ipv6_unresolvable_hosts_and_non_canonical_ranges()
    {
        var resolver = new FakeZoneResolver();
        resolver.Missing.Add("registry.npmjs.org");
        var environment = new Dictionary<string, string>(ZonePolicyTestFiles.Environment)
        {
            ["AGENT_ZONE_DNS_RESOLVERS"] = "2001:db8::53",
            ["AGENT_ZONE_SECRET_STORE_HOSTS"] = "10.20.0.1/16",
        };

        var error = await Assert.ThrowsAsync<ZonePolicyException>(() =>
            ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver, environment));

        Assert.Contains(error.Errors, item => item.Contains("'2001:db8::53' is not an IPv4 address", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("'registry.npmjs.org' does not resolve", StringComparison.Ordinal));
        Assert.Contains(error.Errors, item => item.Contains("'10.20.0.1/16' is not a canonical IPv4 CIDR", StringComparison.Ordinal));
    }

    public static TheoryData<string, string, string> InvalidPolicies => new()
    {
        { "$.schemaVersion", "2", "schemaVersion 2 is not supported" },
        { "$.version", "\"\"", "version is required" },
        { "$.zones[0].egress.allow", "[\"dns\", \"secret-store\"]", "a work zone must not reach the secret store 'secret-store'" },
        { "$.zones[0].egress.default", "\"allow\"", "a work zone denies egress by default" },
        { "$.zones[0].egress.allow", "[\"dns\", \"artifactory\"]", "unknown destination 'artifactory'" },
        { "$.zones[1].egress.deny", "[\"control-plane\"]", "a run zone must deny secret-store destination 'secret-store'" },
        { "$.zones[1].networks[0].tenant", "null", "run network agent-run-default must name its tenant" },
        { "$.zones[1].networks[0].subnet", "\"172.30.0.128/25\"", "overlaps agent-work" },
        { "$.zones[1].networks[0].subnet", "\"172.31.0.1/24\"", "is not an IPv4 CIDR" },
        { "$.zones[1].networks[0].bridge", "\"br-agent-run-default\"", "at most 15 characters" },
        { "$.zones[1].networks[0].bridge", "\"br-agent-work\"", "must be a unique interface name" },
        { "$.zones[0].verify.blocked", "[]", "at least one reachable and one blocked probe" },
        { "$.zones[1].verify.blocked", "[{ \"destination\": \"secret-store\" }]", "needs a port because the destination has none" },
        { "$.zones[0].kind", "\"build\"", "kind must be 'work' or 'run'" },
        { "$.destinations.dns.protocols", "[\"icmp\"]", "protocols must be tcp or udp" },
        { "$.destinations.dns.ports", "[0]", "ports must be between 1 and 65535" },
        { "$.destinations.dns.class", "\"internal\"", "class must be egress, control-plane or secret-store" },
        { "$.zones[0].proxy", "\"squid\"", "not a valid zone policy document" },
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

    [Fact]
    public void Checked_in_policy_is_valid_and_every_zone_proves_both_directions()
    {
        var document = ZonePolicyParser.Parse(ZonePolicyTestFiles.CheckedInPolicy);

        Assert.Empty(ZonePolicyParser.Validate(document));
        Assert.Equal(["work", "run"], document.Zones.Select(zone => zone.Kind));
        var work = document.Zones[0];
        Assert.Equal(["dns", "control-plane", "git-hosts", "package-registries", "model-providers"], work.Egress.Allow);
        Assert.Equal(["control-plane", "secret-store"], document.Zones[1].Egress.Deny);
    }

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
