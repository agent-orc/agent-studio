using AgentRunner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: the verification probe logic against a fake network whose
/// firewall evaluates the rendered rules, so the checks prove what the rules
/// do and not only what they say.
/// </summary>
public sealed class ZonePolicyVerifierTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero));

    private static async Task<(ResolvedZonePolicy Policy, ZoneRuleSet Rules, FakeZoneResolver Resolver)> CheckedInAsync()
    {
        var resolver = new FakeZoneResolver();
        var policy = await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver);
        return (policy, ZonePolicyRules.Render(policy), resolver);
    }

    /// <summary>The host after apply: rules loaded, sets created, the zone resolver running.</summary>
    private FakeZoneNetwork Network(ZoneRuleSet rules, FakeZoneResolver resolver, Func<string?>? loaded = null)
    {
        var sets = new FakeIpSets(_clock);
        foreach (var set in rules.IpSets) sets.Create(set.Name, set.TimeoutSeconds);
        return new FakeZoneNetwork(rules.Networks, loaded ?? (() => rules.Iptables), resolver, sets, () => rules.ResolverConfig);
    }

    [Fact]
    public async Task Applied_rules_pass_every_reachable_blocked_control_and_inbound_check()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.True(report.Passed, string.Join("\n", report.Checks.Where(check => !check.Passed)));
        Assert.Equal(_clock.GetUtcNow(), report.CheckedAt);
        var work = report.Checks.Where(check => check.Zone == "work").ToList();
        Assert.Contains(work, check => check.Kind == "no-inbound" && check.Observed == "none");
        Assert.Contains(work, check => check is { Kind: "reachable", Observed: "connected" }
                                       && check.Target.StartsWith("task-server.example.internal", StringComparison.Ordinal));
        Assert.Contains(work, check => check is { Kind: "reachable", Target: "github.com:443", Observed: "connected" });
        Assert.Contains(work, check => check is { Kind: "blocked", Target: "1.1.1.1:443", Observed: "rejected" });
        Assert.Contains(work, check => check is { Kind: "control", Network: "bridge", Target: "1.1.1.1:443", Observed: "connected" });
        var run = report.Checks.Where(check => check.Zone == "run").ToList();
        Assert.Contains(run, check => check is { Kind: "reachable", Target: "example.com:443", Observed: "connected" });
        // Each control-plane address on every port, each secret-store address on its probe port.
        Assert.Equal(4, run.Count(check => check.Kind == "blocked"));
        var controlPlane = resolver.Resolve("task-server.example.internal")[0];
        foreach (var port in new[] { 443, 5071 })
            Assert.Contains(run, check => check is { Kind: "blocked", Observed: "rejected" }
                                          && check.Target == $"control-plane ({controlPlane}:{port})");
        Assert.Contains(run, check => check is { Kind: "blocked", Observed: "rejected" }
                                      && check.Target == "secret-store (10.20.0.1:443)");
        Assert.Contains(run, check => check is { Kind: "blocked", Observed: "rejected" }
                                      && check.Target == "secret-store (192.0.2.80:443)");
        // One positive control per blocked target, from outside the zones.
        Assert.Equal(4, run.Count(check => check is { Kind: "control", Network: "bridge", Passed: true }));
        Assert.All(network.DnsUsed.Where(item => item.Network == "agent-work"), item => Assert.Equal("172.30.0.1", item.Dns));
        Assert.All(network.DnsUsed.Where(item => item.Network != "agent-work"), item => Assert.Null(item.Dns));
    }

    [Fact]
    public async Task Missing_rules_fail_every_blocked_probe()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver, () => null);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.False(report.Passed);
        var blocked = report.Checks.Where(check => check.Kind == "blocked").ToList();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, check => Assert.Equal("connected", check.Observed));
        Assert.All(blocked, check => Assert.False(check.Passed));
        Assert.All(report.Checks.Where(check => check.Kind == "reachable"), check => Assert.True(check.Passed));
    }

    [Fact]
    public async Task An_allowlist_without_the_destination_fails_the_reachable_probe()
    {
        var (policy, _, resolver) = await CheckedInAsync();
        var narrowed = policy with
        {
            Zones = policy.Zones.Select(zone => zone with
            {
                Allowed = zone.Allowed.Where(destination => destination.Name != "git-hosts").ToList(),
            }).ToList(),
        };
        var rules = ZonePolicyRules.Render(narrowed);
        var network = Network(rules, resolver);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(narrowed, CancellationToken.None);

        var github = Assert.Single(report.Checks, check => check.Target == "github.com:443");
        Assert.False(github.Passed);
        // The zone resolver no longer forwards the name, so it does not even resolve.
        Assert.Equal("unresolved", github.Observed);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task The_allowed_set_follows_rotating_answers_and_keeps_earlier_ones_until_the_set_timeout()
    {
        var resolver = new FakeZoneResolver();
        resolver.Rotating["github.com"] = new Queue<string[]>([["203.0.113.10"], ["203.0.113.20"]]);
        var rules = ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver));
        var sets = new FakeIpSets(_clock);
        foreach (var set in rules.IpSets) sets.Create(set.Name, set.TimeoutSeconds);
        var dnsmasq = new FakeZoneDnsmasq(rules.ResolverConfig!, resolver, sets);
        var firewall = new FakeZoneFirewall(rules.Networks, rules.Iptables, sets);
        var first = System.Net.IPAddress.Parse("203.0.113.10");
        var second = System.Net.IPAddress.Parse("203.0.113.20");

        Assert.Equal(first, Assert.Single(dnsmasq.Resolve("github.com")));
        _clock.Advance(TimeSpan.FromMinutes(5));
        // The CDN rotates: the second answer differs, and a container that
        // still holds the first answer keeps connecting.
        Assert.Equal(second, Assert.Single(dnsmasq.Resolve("github.com")));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", first, 443));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", second, 443));
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-agent-work", first, 80));
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-agent-work", System.Net.IPAddress.Parse("203.0.113.30"), 443));
        // An answer for a subdomain lands in the same set; a lookalike is refused.
        resolver.Overrides["eu.api.github.com"] = ["203.0.113.40"];
        resolver.Overrides["notgithub.com"] = ["203.0.113.50"];
        dnsmasq.Resolve("eu.api.github.com");
        Assert.Throws<System.Net.Sockets.SocketException>(() => dnsmasq.Resolve("notgithub.com"));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", System.Net.IPAddress.Parse("203.0.113.40"), 443));
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-agent-work", System.Net.IPAddress.Parse("203.0.113.50"), 443));

        // An address nobody resolved for a whole set timeout expires; the fresher one stays.
        _clock.Advance(TimeSpan.FromSeconds(86400) - TimeSpan.FromMinutes(5));
        Assert.Equal(ZoneProbeOutcome.Rejected, firewall.Connect("br-agent-work", first, 443));
        Assert.Equal(ZoneProbeOutcome.Connected, firewall.Connect("br-agent-work", second, 443));
    }

    [Fact]
    public async Task Blocked_probes_connect_to_resolved_addresses_so_dns_cannot_fake_a_block()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);

        await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        var example = resolver.Resolve("example.com")[0].ToString();
        Assert.Contains(network.Connections, connection => connection == ("agent-work", example, 80));
        Assert.DoesNotContain(network.Connections, connection => connection.Address == "example.com" && connection.Port == 80);
        // Reachable probes use the name so the resolver allowance is exercised.
        Assert.Contains(network.Connections, connection => connection == ("agent-work", "github.com", 443));
    }

    [Fact]
    public async Task Inconclusive_probes_and_published_ports_fail_the_verification()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);
        network.ForcedFrom[("agent-work", "1.1.1.1", 443)] = new ZoneProbeObservation(ZoneProbeOutcome.ProbeError, "probe container failed (exit 125)");
        network.Published["agent-work"] = ["review-1: 0.0.0.0:8080->8080/tcp"];

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(2, report.FailedCount);
        var probe = Assert.Single(report.Checks, check => check is { Kind: "blocked", Target: "1.1.1.1:443" });
        Assert.Equal("probe-error", probe.Observed);
        var inbound = Assert.Single(report.Checks, check => check.Kind == "no-inbound");
        Assert.Equal("1 published", inbound.Observed);
        Assert.Equal("review-1: 0.0.0.0:8080->8080/tcp", inbound.Detail);
    }

    [Fact]
    public async Task A_probe_image_that_cannot_be_prepared_fails_before_any_probe()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);
        network.PrepareFailure = "probe image bash:5.2 could not be pulled: denied";

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        var setup = Assert.Single(report.Checks);
        Assert.Equal("setup", setup.Kind);
        Assert.False(report.Passed);
        Assert.Empty(network.Connections);
    }

    [Fact]
    public async Task Unresolved_names_fail_their_checks_without_a_probe()
    {
        var resolver = new FakeZoneResolver();
        resolver.Missing.Add("example.com");
        resolver.Missing.Add("task-server.example.internal");
        var policy = await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver);
        var network = Network(ZonePolicyRules.Render(policy), resolver);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.False(report.Passed);
        // The denial without a rule comes first, so the capability names it.
        var setup = report.Checks[0];
        Assert.Equal(("setup", "deny control-plane", "resolved", "unresolved", false),
            (setup.Kind, setup.Target, setup.Expected, setup.Observed, setup.Passed));
        var blocked = Assert.Single(report.Checks, check => check is { Kind: "blocked", Target: "example.com:80" });
        Assert.Equal("unresolved", blocked.Observed);
        Assert.False(blocked.Passed);
        Assert.DoesNotContain(network.Connections, connection => connection.Port == 80);
        Assert.DoesNotContain(report.Checks, check => check is { Kind: "control", Target: "example.com:80" });
    }

    [Fact]
    public async Task A_name_the_zone_resolver_refuses_is_unresolved_in_the_work_zone()
    {
        var (_, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);

        var observation = await network.ConnectAsync("agent-work", "172.30.0.1", "example.com", 443, CancellationToken.None);

        Assert.Equal(ZoneProbeOutcome.Unresolved, observation.Outcome);
        Assert.False(ZonePolicyVerifier.Satisfies(ZoneProbeExpectations.Reachable, observation.Outcome));
    }

    [Fact]
    public async Task A_reset_from_the_destination_does_not_pass_as_blocked()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);
        network.ForcedFrom[("agent-work", "1.1.1.1", 443)] =
            new ZoneProbeObservation(ZoneProbeOutcome.Refused, "bash: connect: Connection refused");

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.False(report.Passed);
        var blocked = Assert.Single(report.Checks, check => !check.Passed);
        Assert.Equal(("blocked", "1.1.1.1:443", "refused"), (blocked.Kind, blocked.Target, blocked.Observed));
    }

    [Theory]
    [InlineData(ZoneProbeOutcome.Connected, true)]
    [InlineData(ZoneProbeOutcome.Refused, true)]
    [InlineData(ZoneProbeOutcome.TimedOut, false)]
    [InlineData(ZoneProbeOutcome.Rejected, false)]
    [InlineData(ZoneProbeOutcome.ProbeError, false)]
    public async Task A_blocked_target_counts_only_when_its_positive_control_answers(ZoneProbeOutcome control, bool passes)
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = Network(rules, resolver);
        // From outside the zones the target is dead or blocked elsewhere, so
        // a timeout inside the zone would prove nothing.
        network.ForcedFrom[("bridge", "1.1.1.1", 443)] = new ZoneProbeObservation(control, control.ToString());

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.Equal(passes, report.Passed);
        var check = Assert.Single(report.Checks, check => check is { Kind: "control", Target: "1.1.1.1:443" });
        Assert.Equal(passes, check.Passed);
        Assert.Equal("bridge", check.Network);
        Assert.Equal("answered", check.Expected);
        Assert.True(Assert.Single(report.Checks, check => check is { Kind: "blocked", Target: "1.1.1.1:443" }).Passed);
    }

    [Fact]
    public async Task Each_blocked_target_gets_one_control_probe_however_many_networks_probe_it()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ZonePolicyTestFiles.CheckedInPolicy,
            documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        json["zones"]![1]!["networks"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
            """{ "name": "agent-run-acme", "bridge": "br-run-acme", "subnet": "172.31.1.0/24", "tenant": "acme" }"""));
        var resolver = new FakeZoneResolver();
        var policy = await ZonePolicyTestFiles.ResolveAsync(json.ToJsonString(), resolver);
        var network = Network(ZonePolicyRules.Render(policy), resolver);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.True(report.Passed, string.Join("\n", report.Checks.Where(check => !check.Passed)));
        Assert.Equal(8, report.Checks.Count(check => check is { Zone: "run", Kind: "blocked" }));
        Assert.Equal(4, report.Checks.Count(check => check is { Zone: "run", Kind: "control" }));
        Assert.Equal(6, network.Connections.Count(connection => connection.Network == "bridge"));
    }

    [Theory]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Connected, true)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Rejected, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Refused, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.TimedOut, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Unresolved, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.ProbeError, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Connected, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Rejected, true)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Refused, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.TimedOut, true)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Unresolved, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.ProbeError, false)]
    public void Expectation_matrix(string expectation, ZoneProbeOutcome outcome, bool passes)
        => Assert.Equal(passes, ZonePolicyVerifier.Satisfies(expectation, outcome));

    [Theory]
    [InlineData(0, "", ZoneProbeOutcome.Connected)]
    [InlineData(124, "", ZoneProbeOutcome.TimedOut)]
    [InlineData(143, "", ZoneProbeOutcome.TimedOut)]
    [InlineData(1, "bash: connect: No route to host", ZoneProbeOutcome.Rejected)]
    [InlineData(1, "bash: connect: Connection refused", ZoneProbeOutcome.Refused)]
    [InlineData(1, "bash: connect: Network is unreachable", ZoneProbeOutcome.ProbeError)]
    [InlineData(1, "", ZoneProbeOutcome.ProbeError)]
    [InlineData(1, "bash: example.invalid: Name or service not known", ZoneProbeOutcome.Unresolved)]
    [InlineData(1, "bash: example.invalid: Name does not resolve", ZoneProbeOutcome.Unresolved)]
    [InlineData(125, "docker: Error response from daemon: network agent-work not found.", ZoneProbeOutcome.ProbeError)]
    [InlineData(127, "docker could not start", ZoneProbeOutcome.ProbeError)]
    [InlineData(ProcessZoneHost.Overran, "docker did not finish within 180s", ZoneProbeOutcome.ProbeError)]
    public void Docker_probe_exit_codes_are_classified(int exitCode, string error, ZoneProbeOutcome expected)
        => Assert.Equal(expected, DockerZoneNetworkProbe.Classify(new ZoneCommandResult(exitCode, "", error)).Outcome);

    [Fact]
    public async Task Docker_probe_runs_a_throw_away_container_on_the_zone_network()
    {
        var host = new RecordingZoneHost();
        var probe = new DockerZoneNetworkProbe(host, ZonePolicyRules.DefaultProbeImage, 4);

        await probe.ConnectAsync("agent-work", "172.30.0.1", "1.1.1.1", 443, CancellationToken.None);
        await probe.ConnectAsync("bridge", null, "1.1.1.1", 443, CancellationToken.None);
        var published = await probe.PublishedPortsAsync("agent-work", CancellationToken.None);

        Assert.Equal(
            "docker run --rm --network agent-work --dns 172.30.0.1 --label org.agent-studio.zone-probe=true "
            + "bash:5.2@sha256:a54fb4422b18f05dd3107c36f39d67b26334fda7ec89f4126052b45e228e2f15 timeout 4 bash -c "
            + "exec 3<>\"/dev/tcp/$0/$1\" 1.1.1.1 443",
            host.Commands[0]);
        Assert.StartsWith("docker run --rm --network bridge --label ", host.Commands[1]);
        Assert.Equal(["gate-1: 0.0.0.0:9000->9000/tcp"], published);
    }

    private sealed class RecordingZoneHost : IZoneHost
    {
        public List<string> Commands { get; } = [];

        public Task<ZoneCommandResult> RunAsync(
            string executable, IReadOnlyList<string> args, string? standardInput, CancellationToken ct)
        {
            Commands.Add($"{executable} {string.Join(' ', args)}");
            return Task.FromResult(args[0] == "ps"
                ? new ZoneCommandResult(0, "review-1\t\ngate-1\t0.0.0.0:9000->9000/tcp\n", "")
                : new ZoneCommandResult(0, "", ""));
        }

        public bool PathExists(string path) => true;
    }
}
