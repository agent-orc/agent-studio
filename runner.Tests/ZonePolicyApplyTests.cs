using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: the host-side apply is a dry run by default, idempotent with
/// --execute, refuses to recreate a conflicting network, and records the
/// verified state the daemons report as their zone-policy capability.
/// </summary>
public sealed class ZonePolicyApplyTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zone-policy-{Guid.NewGuid():N}");

    public ZonePolicyApplyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<ZoneRuleSet> RulesAsync()
        => ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy));

    [Fact]
    public async Task Dry_run_reads_and_validates_but_changes_nothing()
    {
        var host = new FakeZoneHost();

        var report = await new ZonePolicyApplier(host).ApplyAsync(await RulesAsync(), execute: false, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Empty(host.Mutations);
        Assert.Contains("iptables-restore --test --noflush", host.Commands);
        Assert.Equal(["unchanged", "would-create", "would-create", "would-apply", "would-insert", "would-insert"],
            report.Steps.Select(step => step.Status));
        Assert.Empty(host.Networks);
        Assert.Null(host.LoadedRules);
    }

    [Fact]
    public async Task Dry_run_without_root_reports_the_rule_checks_as_unchecked()
    {
        var host = new FakeZoneHost { Root = false };

        var report = await new ZonePolicyApplier(host).ApplyAsync(await RulesAsync(), execute: false, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal("unchecked", report.Steps.Single(step => step.Name.StartsWith("iptables-restore", StringComparison.Ordinal)).Status);
        Assert.Empty(host.Mutations);
    }

    [Fact]
    public async Task Execute_applies_once_and_a_second_run_changes_nothing()
    {
        var host = new FakeZoneHost();
        var rules = await RulesAsync();
        var applier = new ZonePolicyApplier(host);

        var first = await applier.ApplyAsync(rules, execute: true, CancellationToken.None);
        var firstMutations = host.Mutations.ToList();
        host.Commands.Clear();
        var second = await applier.ApplyAsync(rules, execute: true, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(["unchanged", "created", "created", "applied", "inserted", "inserted"], first.Steps.Select(step => step.Status));
        Assert.Contains("docker network create --driver bridge --subnet 172.30.0.0/24 --opt com.docker.network.bridge.name=br-agent-work "
                        + "--opt com.docker.network.bridge.enable_icc=false --label org.agent-studio.zone=work agent-work", firstMutations);
        Assert.Contains("iptables -I DOCKER-USER 1 -j AGENT-ZONE-FWD", firstMutations);
        Assert.Contains("iptables -I INPUT 1 -j AGENT-ZONE-IN", firstMutations);
        Assert.Equal(rules.Iptables, host.LoadedRules);

        Assert.True(second.Succeeded);
        Assert.Equal(["unchanged", "unchanged", "unchanged", "applied", "unchanged", "unchanged"], second.Steps.Select(step => step.Status));
        // Reloading the two owned chains is the only write; it replaces them atomically.
        Assert.Equal(["iptables-restore --noflush"], host.Mutations);
    }

    [Fact]
    public async Task A_conflicting_network_is_never_recreated_and_no_rule_is_loaded()
    {
        var host = new FakeZoneHost();
        host.Networks["agent-work"] = "bridge|172.29.0.0/24,|br-agent-work|false";

        var report = await new ZonePolicyApplier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        Assert.False(report.Succeeded);
        var conflict = Assert.Single(report.Steps, step => step.Status == "conflict");
        Assert.Contains("subnet is 172.29.0.0/24, not 172.30.0.0/24", conflict.Detail);
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("iptables", StringComparison.Ordinal));
        Assert.Null(host.LoadedRules);
    }

    [Theory]
    [InlineData("bridge|172.30.0.0/24,|br-agent-work|false", null)]
    [InlineData("overlay|172.30.0.0/24,|br-agent-work|false", "driver is overlay, not bridge")]
    [InlineData("bridge|172.30.0.0/24,|<no value>|false", "bridge is unset, not br-agent-work")]
    [InlineData("bridge|172.30.0.0/24,|br-agent-work|<no value>", "inter-container traffic is true, not false")]
    [InlineData("bridge|172.30.0.0/24,172.30.1.0/24,|br-agent-work|false", "subnet is 172.30.0.0/24 172.30.1.0/24, not 172.30.0.0/24")]
    public void Network_comparison_names_the_first_difference(string inspected, string? expected)
    {
        var network = new ZoneNetworkSpec("work", "work", "agent-work", "br-agent-work", "172.30.0.0/24", null, InterContainer: false);
        Assert.Equal(expected, ZonePolicyApplier.NetworkConflict(network, inspected));
    }

    [Fact]
    public async Task Docker_down_fails_before_touching_networks_or_rules()
    {
        var host = new FakeZoneHost { DockerUp = false };

        var report = await new ZonePolicyApplier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(["docker version --format {{.Server.Version}}"], host.Commands);
    }

    [Fact]
    public async Task Command_apply_execute_records_the_verified_state_the_daemon_reports()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, output, error, _clock);

        Assert.True(exit == 0, output + "\n" + error);
        Assert.Contains("verification passed", output.ToString());
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.Equal("2026-10-20.1", state.PolicyVersion);
        Assert.Equal(_clock.GetUtcNow(), state.AppliedAt);
        Assert.Equal(ZonePolicyParser.Sha256(ZonePolicyTestFiles.CheckedInPolicy), state.PolicySha256);
        Assert.Equal(ZonePolicyParser.Sha256(host.LoadedRules!), state.RulesSha256);
        Assert.Equal(["work", "run"], state.Zones.Select(zone => zone.Name));

        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:5031", RunnerId = "review-a", RunnerName = "review-a", Hostname = "host-a",
            BackendName = "test", Role = "review", WorkDir = Path.GetTempPath(), BaseBranch = "main",
            ZonePolicyStatePath = statePath,
        };
        var capability = Assert.Single(RunnerCapabilityProbe.Advertise(options, gitPushReady: false),
            item => item.Key == CapabilityProtocol.ZonePolicy);
        Assert.Equal("ready", capability.Status);
        Assert.Equal("2026-10-20.1", capability.Version);
        Assert.Equal("work", capability.Identity);
        Assert.StartsWith("verifiedAt=2026-10-20T09:30:00Z; networks=agent-work; rules=sha256:", capability.Detail);
    }

    [Fact]
    public async Task Command_dry_run_is_the_default_and_writes_no_state()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        var output = new StringWriter();

        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath], host, resolver, output, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Contains("dry run: nothing changed", output.ToString());
        Assert.Contains("*filter", output.ToString());
        Assert.Empty(host.Mutations);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public async Task Verify_record_turns_the_capability_unavailable_when_the_rules_are_gone()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        // Someone flushes the zone chains on the host; the record still names
        // the same rules, so --record is allowed and reports what it sees.
        host.LoadedRules = null;
        _clock.Advance(TimeSpan.FromHours(1));
        var output = new StringWriter();

        var exit = await Run(["verify", "--policy", policyPath, "--state", statePath, "--record"],
            host, resolver, output, new StringWriter(), _clock);

        Assert.Equal(1, exit);
        Assert.Contains("verification FAILED", output.ToString());
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.False(state.Verification.Passed);
        Assert.Equal(_clock.GetUtcNow(), state.Verification.CheckedAt);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("2026-10-20.1", capability.Version);
        Assert.Contains("verification failed at 2026-10-20T10:30:00Z", capability.Detail);
        Assert.Contains("blocked", capability.Detail);
    }

    [Fact]
    public async Task Verify_record_refuses_when_the_rendered_rules_differ_from_the_applied_ones()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        resolver.Overrides["github.com"] = ["203.0.113.9"];
        var error = new StringWriter();

        var exit = await Run(["verify", "--policy", policyPath, "--state", statePath, "--record"], host, resolver, new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Contains("rendered rules differ from the applied ones", error.ToString());
    }

    [Fact]
    public async Task A_failed_apply_is_recorded_as_unverified()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        host.Networks["agent-work"] = "bridge|172.30.0.0/24,|docker1|false";
        var error = new StringWriter();

        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Contains("apply failed at docker network agent-work", error.ToString());
        Assert.Equal("unavailable", ZonePolicyCapability.Describe(statePath, ZoneKinds.Work)!.Status);
    }

    [Theory]
    [InlineData(new[] { "apply", "--force" }, "apply does not accept --force")]
    [InlineData(new[] { "verify", "--execute" }, "verify does not accept --execute")]
    [InlineData(new[] { "apply", "--state" }, "--state needs a value")]
    [InlineData(new[] { "remove" }, "usage: agent-host zone-policy")]
    public async Task Command_rejects_unknown_arguments(string[] args, string expected)
    {
        var error = new StringWriter();
        var exit = await Run(args, new FakeZoneHost(), new FakeZoneResolver(), new StringWriter(), error);
        Assert.Equal(2, exit);
        Assert.Contains(expected, error.ToString());
    }

    [Fact]
    public async Task Env_file_supplies_the_installation_addresses()
    {
        var (host, resolver, policyPath, _) = Setup();
        var envFile = Path.Combine(_root, "zone-policy.env");
        await File.WriteAllLinesAsync(envFile,
        [
            "# zone policy addresses",
            "AGENT_ZONE_DNS_RESOLVERS=192.0.2.53",
            "AGENT_ZONE_CONTROL_PLANE_HOSTS=\"task-server.example.internal\"",
            "AGENT_ZONE_SECRET_STORE_HOSTS=192.0.2.80",
        ]);
        var output = new StringWriter();

        var exit = await ZonePolicyCommand.RunAsync(["render", "--policy", policyPath, "--env-file", envFile],
            output, new StringWriter(), host, resolver, _clock, new Dictionary<string, string>(), _ => throw new InvalidOperationException(),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("-A AGENT-ZONE-FWD -i br-agent-work -d 192.0.2.53/32 -p udp --dport 53", output.ToString());
        Assert.DoesNotContain("192.0.2.54", output.ToString());
    }

    private (FakeZoneHost Host, FakeZoneResolver Resolver, string PolicyPath, string StatePath) Setup()
    {
        var policyPath = Path.Combine(_root, "zone-policy.json");
        File.Copy(ZonePolicyTestFiles.CheckedInPolicyPath, policyPath);
        return (new FakeZoneHost(), new FakeZoneResolver(), policyPath, Path.Combine(_root, "state", "applied.json"));
    }

    private static async Task<int> Run(
        string[] args, FakeZoneHost host, FakeZoneResolver resolver, TextWriter output, TextWriter error,
        FakeTimeProvider? clock = null)
    {
        var networks = (await RulesAsync()).Networks;
        return await ZonePolicyCommand.RunAsync(args, output, error, host, resolver,
            clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero)),
            ZonePolicyTestFiles.Environment,
            _ => new FakeZoneNetwork(networks, () => host.LoadedRules, resolver),
            CancellationToken.None);
    }
}
