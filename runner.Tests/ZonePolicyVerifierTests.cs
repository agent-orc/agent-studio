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

    [Fact]
    public async Task Applied_rules_pass_every_reachable_blocked_and_inbound_check()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = new FakeZoneNetwork(rules.Networks, () => rules.Iptables, resolver);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.True(report.Passed, string.Join("\n", report.Checks.Where(check => !check.Passed)));
        Assert.Equal(_clock.GetUtcNow(), report.CheckedAt);
        var work = report.Checks.Where(check => check.Zone == "work").ToList();
        Assert.Contains(work, check => check.Kind == "no-inbound" && check.Observed == "none");
        Assert.Contains(work, check => check is { Kind: "reachable", Observed: "connected" }
                                       && check.Target.StartsWith("task-server.example.internal", StringComparison.Ordinal));
        Assert.Contains(work, check => check is { Kind: "reachable", Target: "github.com:443", Observed: "connected" });
        Assert.Contains(work, check => check is { Kind: "blocked", Target: "1.1.1.1:443", Observed: "refused" });
        var run = report.Checks.Where(check => check.Zone == "run").ToList();
        Assert.Contains(run, check => check is { Kind: "reachable", Target: "example.com:443", Observed: "connected" });
        // Each control-plane and secret-store address is probed by address.
        Assert.Equal(3, run.Count(check => check.Kind == "blocked"));
        Assert.Contains(run, check => check is { Kind: "blocked", Observed: "refused" }
                                      && check.Target == "secret-store (10.20.0.1:443)");
        Assert.Contains(run, check => check is { Kind: "blocked", Observed: "refused" }
                                      && check.Target == "secret-store (192.0.2.80:443)");
    }

    [Fact]
    public async Task Missing_rules_fail_every_blocked_probe()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = new FakeZoneNetwork(rules.Networks, () => null, resolver);

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
        var network = new FakeZoneNetwork(rules.Networks, () => rules.Iptables, resolver);

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(narrowed, CancellationToken.None);

        var github = Assert.Single(report.Checks, check => check.Target == "github.com:443");
        Assert.False(github.Passed);
        Assert.Equal("refused", github.Observed);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task Blocked_probes_connect_to_resolved_addresses_so_dns_cannot_fake_a_block()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = new FakeZoneNetwork(rules.Networks, () => rules.Iptables, resolver);

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
        var network = new FakeZoneNetwork(rules.Networks, () => rules.Iptables, resolver);
        network.Forced[("1.1.1.1", 443)] = new ZoneProbeObservation(ZoneProbeOutcome.ProbeError, "probe container failed (exit 125)");
        network.Published["agent-work"] = ["review-1: 0.0.0.0:8080->8080/tcp"];

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(2, report.FailedCount);
        var probe = Assert.Single(report.Checks, check => check.Target == "1.1.1.1:443");
        Assert.Equal("probe-error", probe.Observed);
        var inbound = Assert.Single(report.Checks, check => check.Kind == "no-inbound");
        Assert.Equal("1 published", inbound.Observed);
        Assert.Equal("review-1: 0.0.0.0:8080->8080/tcp", inbound.Detail);
    }

    [Fact]
    public async Task A_probe_image_that_cannot_be_prepared_fails_before_any_probe()
    {
        var (policy, rules, resolver) = await CheckedInAsync();
        var network = new FakeZoneNetwork(rules.Networks, () => rules.Iptables, resolver)
        {
            PrepareFailure = "probe image bash:5.2 could not be pulled: denied",
        };

        var report = await new ZonePolicyVerifier(network, _clock).VerifyAsync(policy, CancellationToken.None);

        var setup = Assert.Single(report.Checks);
        Assert.Equal("setup", setup.Kind);
        Assert.False(report.Passed);
        Assert.Empty(network.Connections);
    }

    [Theory]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Connected, true)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Refused, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.TimedOut, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.Unresolved, false)]
    [InlineData(ZoneProbeExpectations.Reachable, ZoneProbeOutcome.ProbeError, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Connected, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Refused, true)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.TimedOut, true)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.Unresolved, false)]
    [InlineData(ZoneProbeExpectations.Blocked, ZoneProbeOutcome.ProbeError, false)]
    public void Expectation_matrix(string expectation, ZoneProbeOutcome outcome, bool passes)
        => Assert.Equal(passes, ZonePolicyVerifier.Satisfies(expectation, outcome));

    [Theory]
    [InlineData(0, "", ZoneProbeOutcome.Connected)]
    [InlineData(124, "", ZoneProbeOutcome.TimedOut)]
    [InlineData(143, "", ZoneProbeOutcome.TimedOut)]
    [InlineData(1, "bash: connect: No route to host", ZoneProbeOutcome.Refused)]
    [InlineData(1, "bash: connect: Connection refused", ZoneProbeOutcome.Refused)]
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
        var probe = new DockerZoneNetworkProbe(host, "bash:5.2", 4);

        await probe.ConnectAsync("agent-work", "1.1.1.1", 443, CancellationToken.None);
        var published = await probe.PublishedPortsAsync("agent-work", CancellationToken.None);

        Assert.Equal(
            "docker run --rm --network agent-work --label org.agent-studio.zone-probe=true bash:5.2 timeout 4 bash -c "
            + "exec 3<>\"/dev/tcp/$0/$1\" 1.1.1.1 443",
            host.Commands[0]);
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
    }
}
