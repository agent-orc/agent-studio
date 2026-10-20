using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: the host-side apply is a dry run by default, idempotent with
/// --execute, refuses to recreate a conflicting network or ipset, records the
/// verified state the daemons report as their zone-policy capability, and
/// remove rolls it back.
/// </summary>
public sealed class ZonePolicyApplyTests : IDisposable
{
    private const string BootId = "0f6e1d3c-boot-a";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zone-policy-{Guid.NewGuid():N}");

    public ZonePolicyApplyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ResolverConfigPath => Path.Combine(_root, "dnsmasq.conf");

    private static async Task<ZoneRuleSet> RulesAsync(FakeZoneResolver? resolver = null)
        => ZonePolicyRules.Render(await ZonePolicyTestFiles.ResolveAsync(ZonePolicyTestFiles.CheckedInPolicy, resolver));

    private ZonePolicyApplier Applier(FakeZoneHost host) => new(host, ResolverConfigPath);

    private static readonly string[] Sets = ["control-plane", "git-hosts", "package-registries", "model-providers"];

    [Fact]
    public async Task Dry_run_reads_and_validates_but_changes_nothing()
    {
        var host = new FakeZoneHost();

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: false, CancellationToken.None);

        Assert.True(report.Succeeded, string.Join("\n", report.Steps));
        Assert.Empty(host.Mutations);
        Assert.Contains("iptables-restore --test --noflush", host.Commands);
        Assert.Contains("ip6tables-restore --test --noflush", host.Commands);
        Assert.Equal(
        [
            "docker:unchanged", "docker network agent-work:would-create", "docker network agent-run-default:would-create",
            .. Sets.Select(set => $"ipset agent-zone-{set}:would-create"),
            // The kernel checks a set match against existing sets only.
            "iptables-restore AGENT-ZONE-FWD AGENT-ZONE-IN:unchecked",
            "iptables DOCKER-USER -j AGENT-ZONE-FWD:would-insert", "iptables INPUT -j AGENT-ZONE-IN:would-insert",
            "ip6tables-restore AGENT-ZONE-FWD AGENT-ZONE-IN:would-apply",
            "ip6tables DOCKER-USER -j AGENT-ZONE-FWD:would-insert", "ip6tables INPUT -j AGENT-ZONE-IN:would-insert",
            // The resolver comes last: the rules deny until it fills the sets.
            $"zone resolver configuration {ResolverConfigPath}:would-write", "systemctl agent-zone-dns.service:would-start",
        ], report.Steps.Select(step => $"{step.Name}:{step.Status}"));
        Assert.Contains("the ipsets do not exist yet", report.Steps.Single(step => step.Name.StartsWith("iptables-restore", StringComparison.Ordinal)).Detail);
        Assert.Empty(host.Networks);
        Assert.Null(host.LoadedRules);
        Assert.False(File.Exists(ResolverConfigPath));
    }

    [Fact]
    public async Task Dry_run_without_root_reports_every_firewall_check_as_unchecked()
    {
        var host = new FakeZoneHost { Root = false };

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: false, CancellationToken.None);

        Assert.True(report.Succeeded, string.Join("\n", report.Steps));
        var firewall = report.Steps.Where(step => step.Name.StartsWith("ip", StringComparison.Ordinal)).ToList();
        Assert.Equal(4 + 2 + 4, firewall.Count);
        Assert.All(firewall, step => Assert.Equal("unchecked", step.Status));
        Assert.Contains("Permission denied", firewall.Last().Detail);
        Assert.Empty(host.Mutations);
    }

    [Fact]
    public async Task Execute_applies_once_and_a_second_run_changes_nothing()
    {
        var host = new FakeZoneHost();
        var rules = await RulesAsync();
        var applier = Applier(host);

        var first = await applier.ApplyAsync(rules, execute: true, CancellationToken.None);
        var firstMutations = host.Mutations.ToList();
        host.Commands.Clear();
        var second = await applier.ApplyAsync(rules, execute: true, CancellationToken.None);

        Assert.True(first.Succeeded, string.Join("\n", first.Steps));
        Assert.Equal(["unchanged", "created", "created", "created", "created", "created", "created",
            "applied", "inserted", "inserted", "applied", "inserted", "inserted", "written", "started"], first.Steps.Select(step => step.Status));
        Assert.Contains("docker network create --driver bridge --ipv6=false --subnet 172.30.0.0/24 --gateway 172.30.0.1 "
                        + "--opt com.docker.network.bridge.name=br-agent-work --opt com.docker.network.bridge.enable_icc=false "
                        + "--label org.agent-studio.zone=work agent-work", firstMutations);
        Assert.Contains("ipset create agent-zone-git-hosts hash:ip family inet timeout 86400", firstMutations);
        Assert.Contains("iptables -I DOCKER-USER 1 -j AGENT-ZONE-FWD", firstMutations);
        Assert.Contains("iptables -I INPUT 1 -j AGENT-ZONE-IN", firstMutations);
        Assert.Contains("ip6tables -I DOCKER-USER 1 -j AGENT-ZONE-FWD", firstMutations);
        Assert.Contains("ip6tables -I INPUT 1 -j AGENT-ZONE-IN", firstMutations);
        Assert.Equal(rules.Iptables, host.LoadedRules);
        Assert.Equal(rules.Ip6tables, host.Loaded["ip6tables"]);
        Assert.Equal(rules.ResolverConfig, File.ReadAllText(ResolverConfigPath));
        Assert.Equal(1, host.ResolverStarts);

        Assert.True(second.Succeeded);
        Assert.All(second.Steps.Where(step => !step.Name.Contains("-restore", StringComparison.Ordinal)),
            step => Assert.Equal("unchanged", step.Status));
        // Reloading the owned chains is the only write; it replaces them atomically.
        Assert.Equal(["iptables-restore --noflush", "ip6tables-restore --noflush"], host.Mutations);
    }

    [Fact]
    public async Task A_changed_resolver_configuration_restarts_the_resolver()
    {
        var host = new FakeZoneHost();
        var rules = await RulesAsync();
        await Applier(host).ApplyAsync(rules, execute: true, CancellationToken.None);
        File.WriteAllText(ResolverConfigPath, "# an older policy\n");

        var report = await Applier(host).ApplyAsync(rules, execute: true, CancellationToken.None);

        Assert.Equal("written", report.Steps.Single(step => step.Name.StartsWith("zone resolver", StringComparison.Ordinal)).Status);
        Assert.Equal("restarted", report.Steps.Single(step => step.Name.StartsWith("systemctl", StringComparison.Ordinal)).Status);
        Assert.Contains("systemctl restart agent-zone-dns.service", host.Commands);
        Assert.Equal(rules.ResolverConfig, File.ReadAllText(ResolverConfigPath));
    }

    [Fact]
    public async Task Without_docker_ip6tables_the_ipv6_jump_goes_into_forward()
    {
        var host = new FakeZoneHost { Ip6DockerUser = false };

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        Assert.True(report.Succeeded, string.Join("\n", report.Steps));
        var jump = report.Steps.Single(step => step.Name == "ip6tables FORWARD -j AGENT-ZONE-FWD");
        Assert.Equal("inserted", jump.Status);
        Assert.Equal("DOCKER-USER does not exist", jump.Detail);
        Assert.Contains("ip6tables:FORWARD>AGENT-ZONE-FWD", host.Jumps);
    }

    [Fact]
    public async Task A_conflicting_network_is_never_recreated_and_no_rule_is_loaded()
    {
        var host = new FakeZoneHost();
        host.Networks["agent-work"] = "bridge|172.29.0.0/24,|172.29.0.1,|br-agent-work|false|false";

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        Assert.False(report.Succeeded);
        var conflict = Assert.Single(report.Steps, step => step.Status == "conflict");
        Assert.Contains("subnet is 172.29.0.0/24, not 172.30.0.0/24", conflict.Detail);
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("ip", StringComparison.Ordinal));
        Assert.Null(host.LoadedRules);
    }

    [Fact]
    public async Task An_ipset_with_another_timeout_is_a_conflict_and_no_rule_is_loaded()
    {
        var host = new FakeZoneHost();
        host.Sets.Create("agent-zone-git-hosts", 3600);

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        var conflict = Assert.Single(report.Steps, step => step.Status == "conflict");
        Assert.Equal("ipset agent-zone-git-hosts", conflict.Name);
        Assert.StartsWith("timeout is 3600, not 86400", conflict.Detail);
        Assert.Null(host.LoadedRules);
        Assert.False(File.Exists(ResolverConfigPath));
    }

    [Theory]
    [InlineData("bridge|172.30.0.0/24,|172.30.0.1,|br-agent-work|false|false", null)]
    [InlineData("overlay|172.30.0.0/24,|172.30.0.1,|br-agent-work|false|false", "driver is overlay, not bridge")]
    [InlineData("bridge|172.30.0.0/24,|172.30.0.1,|<no value>|false|false", "bridge is unset, not br-agent-work")]
    [InlineData("bridge|172.30.0.0/24,|172.30.0.1,|br-agent-work|<no value>|false", "inter-container traffic is true, not false")]
    [InlineData("bridge|172.30.0.0/24,172.30.1.0/24,|172.30.0.1,|br-agent-work|false|false", "subnet is 172.30.0.0/24 172.30.1.0/24, not 172.30.0.0/24")]
    [InlineData("bridge|172.30.0.0/24,|172.30.0.254,|br-agent-work|false|false", "gateway is 172.30.0.254, not 172.30.0.1")]
    [InlineData("bridge|172.30.0.0/24,||br-agent-work|false|false", "gateway is unset, not 172.30.0.1")]
    [InlineData("bridge|172.30.0.0/24,|172.30.0.1,|br-agent-work|false|true", "IPv6 is enabled, not disabled")]
    [InlineData("bridge|172.30.0.0/24,|br-agent-work|false|false", "unexpected inspect output 'bridge|172.30.0.0/24,|br-agent-work|false|false'")]
    public void Network_comparison_names_the_first_difference(string inspected, string? expected)
    {
        var network = new ZoneNetworkSpec("work", "work", "agent-work", "br-agent-work", "172.30.0.0/24", null, InterContainer: false);
        Assert.Equal(expected, ZonePolicyApplier.NetworkConflict(network, inspected));
    }

    [Theory]
    [InlineData("Type: hash:ip\nHeader: family inet hashsize 1024 maxelem 65536 timeout 86400 bucketsize 12", null)]
    [InlineData("Type: hash:net\nHeader: family inet hashsize 1024 maxelem 65536 timeout 86400", "type is hash:net, not hash:ip")]
    [InlineData("Type: hash:ip\nHeader: family inet6 hashsize 1024 maxelem 65536 timeout 86400", "family is inet6, not inet")]
    [InlineData("Type: hash:ip\nHeader: family inet hashsize 1024 maxelem 65536", "timeout is unset, not 86400")]
    public void Ipset_comparison_names_the_first_difference(string listed, string? expected)
        => Assert.Equal(expected, ZonePolicyApplier.IpSetConflict(new ZoneIpSetSpec("agent-zone-git-hosts", "git-hosts", 86400), listed));

    [Fact]
    public async Task Without_ipv6_in_the_kernel_the_ipv6_rules_are_skipped_after_the_ipv4_jumps()
    {
        var host = new FakeZoneHost { Ipv6Kernel = false };

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        Assert.True(report.Succeeded, string.Join("\n", report.Steps));
        var firewall = report.Steps.Select(step => $"{step.Name}:{step.Status}")
            .Where(step => step.StartsWith("ip", StringComparison.Ordinal) && !step.StartsWith("ipset", StringComparison.Ordinal));
        Assert.Equal(
        [
            "iptables-restore AGENT-ZONE-FWD AGENT-ZONE-IN:applied",
            "iptables DOCKER-USER -j AGENT-ZONE-FWD:inserted", "iptables INPUT -j AGENT-ZONE-IN:inserted",
            "ip6tables-restore AGENT-ZONE-FWD AGENT-ZONE-IN:skipped",
            "ip6tables DOCKER-USER -j AGENT-ZONE-FWD:skipped", "ip6tables INPUT -j AGENT-ZONE-IN:skipped",
        ], firewall);
        Assert.Contains("/proc/net/if_inet6 is missing", report.Steps.Single(step => step.Name.StartsWith("ip6tables-restore", StringComparison.Ordinal)).Detail);
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("ip6tables", StringComparison.Ordinal));
        Assert.Equal(["iptables:DOCKER-USER>AGENT-ZONE-FWD", "iptables:INPUT>AGENT-ZONE-IN"], host.Jumps.Order(StringComparer.Ordinal));
        Assert.Equal("started", report.Steps[^1].Status);

        // A remove on the same host skips the IPv6 family as well, without asking ip6tables.
        var removed = await new ZonePolicyRemover(host).RemoveAsync(execute: true, CancellationToken.None);
        Assert.True(removed.Succeeded, string.Join("\n", removed.Steps));
        var ipv6Steps = removed.Steps.Where(step => step.Name.StartsWith("ip6tables", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, ipv6Steps.Count);
        Assert.All(ipv6Steps, step => Assert.Equal("skipped", step.Status));
        Assert.DoesNotContain(host.Commands, command => command.StartsWith("ip6tables", StringComparison.Ordinal));
        Assert.Empty(host.Jumps);
    }

    [Fact]
    public async Task A_missing_ip6tables_filter_table_on_a_kernel_with_ipv6_fails_the_apply()
    {
        var host = new FakeZoneHost { Ip6FilterTableMissing = true };

        var dryRun = await Applier(host).ApplyAsync(await RulesAsync(), execute: false, CancellationToken.None);
        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        var planned = dryRun.Steps.Single(step => step.Name.StartsWith("ip6tables-restore", StringComparison.Ordinal));
        Assert.Equal("failed", planned.Status);
        Assert.False(dryRun.Succeeded);
        Assert.False(report.Succeeded);
        var restore = report.Steps.Single(step => step.Name.StartsWith("ip6tables-restore", StringComparison.Ordinal));
        Assert.Equal("failed", restore.Status);
        Assert.Contains("Table does not exist", restore.Detail);
        Assert.DoesNotContain(report.Steps, step => step.Status == "skipped");
        // The IPv4 rules and jumps still went in first.
        Assert.Contains("iptables:INPUT>AGENT-ZONE-IN", host.Jumps);
        Assert.Contains("iptables:DOCKER-USER>AGENT-ZONE-FWD", host.Jumps);
    }

    [Fact]
    public async Task An_ip6tables_without_its_filter_table_is_skipped_too()
    {
        var host = new FakeZoneHost { Ip6TablesUnsupported = true };

        var dryRun = await Applier(host).ApplyAsync(await RulesAsync(), execute: false, CancellationToken.None);
        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

        Assert.True(dryRun.Succeeded, string.Join("\n", dryRun.Steps));
        Assert.True(report.Succeeded, string.Join("\n", report.Steps));
        var restore = report.Steps.Single(step => step.Name.StartsWith("ip6tables-restore", StringComparison.Ordinal));
        Assert.Equal("skipped", restore.Status);
        Assert.Contains("Address family not supported by protocol", restore.Detail);
        Assert.All(report.Steps.Where(step => step.Name.StartsWith("ip6tables ", StringComparison.Ordinal)),
            step => Assert.Equal("skipped", step.Status));
        Assert.Contains("iptables:INPUT>AGENT-ZONE-IN", host.Jumps);
    }

    [Fact]
    public async Task After_a_reboot_a_failing_resolver_and_an_unresolvable_probe_host_still_load_both_chains_and_every_jump()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        // The reboot: Docker keeps its networks; the chains, jumps, ipsets and
        // the resolver are gone, and the resolver does not come back.
        var rebooted = new FakeZoneHost { ResolverStartFails = true };
        foreach (var (name, inspected) in host.Networks) rebooted.Networks[name] = inspected;
        resolver.Missing.Add("example.com");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], rebooted, resolver, output, error,
            _clock, bootId: "7d2a9b41-boot-b");

        Assert.Equal(2, exit);
        Assert.Equal((await RulesAsync(resolver)).Iptables, rebooted.LoadedRules);
        Assert.True(rebooted.Loaded.ContainsKey("ip6tables"));
        Assert.Equal(
        [
            "ip6tables:DOCKER-USER>AGENT-ZONE-FWD", "ip6tables:INPUT>AGENT-ZONE-IN",
            "iptables:DOCKER-USER>AGENT-ZONE-FWD", "iptables:INPUT>AGENT-ZONE-IN",
        ], rebooted.Jumps.Order(StringComparer.Ordinal));
        // The sets exist and are empty, so every named destination is denied.
        Assert.All(Sets, set => Assert.True(rebooted.Sets.Exists($"agent-zone-{set}")));
        Assert.Contains("apply failed at systemctl agent-zone-dns.service", error.ToString());
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.False(state.Verification.Passed);
        Assert.Equal("7d2a9b41-boot-b", state.BootId);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, "7d2a9b41-boot-b")!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Contains("setup systemctl agent-zone-dns.service expected applied, observed failed", capability.Detail);
    }

    [Fact]
    public async Task A_blocked_probe_host_that_does_not_resolve_fails_its_check_instead_of_the_apply()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        resolver.Missing.Add("example.com");
        var output = new StringWriter();

        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, output, new StringWriter());

        Assert.Equal(1, exit);
        Assert.NotNull(host.LoadedRules);
        Assert.Equal(4, host.Jumps.Count);
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        var blocked = Assert.Single(state.Verification.Checks, check => !check.Passed && check.Kind == "blocked");
        Assert.Equal(("work", "example.com:80", "unresolved"), (blocked.Zone, blocked.Target, blocked.Observed));
        Assert.Contains("'example.com' does not resolve (HostNotFound)", blocked.Detail);
        // The run zone's reachable probe of the same name fails as well; nothing else does.
        Assert.Equal(2, state.Verification.FailedCount);
        Assert.Equal("unavailable", ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!.Status);
    }

    [Fact]
    public async Task Docker_down_fails_before_touching_networks_or_rules()
    {
        var host = new FakeZoneHost { DockerUp = false };

        var report = await Applier(host).ApplyAsync(await RulesAsync(), execute: true, CancellationToken.None);

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
        Assert.Equal("2026-10-20.3", state.PolicyVersion);
        Assert.Equal(_clock.GetUtcNow(), state.AppliedAt);
        Assert.Equal(ZonePolicyParser.Sha256(ZonePolicyTestFiles.CheckedInPolicy), state.PolicySha256);
        Assert.Equal((await RulesAsync()).RulesSha256, state.RulesSha256);
        Assert.Equal(ZonePolicyParser.Sha256(host.LoadedRules! + host.Loaded["ip6tables"] + File.ReadAllText(ResolverConfigPath)),
            state.RulesSha256);
        Assert.Equal(["work", "run"], state.Zones.Select(zone => zone.Name));
        Assert.Equal(BootId, state.BootId);
        Assert.Null(state.RemovedAt);
        Assert.Contains(state.Verification.Checks, check => check is { Kind: "control", Network: "bridge", Passed: true });

        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:5031", RunnerId = "review-a", RunnerName = "review-a", Hostname = "host-a",
            BackendName = "test", Role = "review", WorkDir = Path.GetTempPath(), BaseBranch = "main",
            ZonePolicyStatePath = statePath,
        };
        var capability = Assert.Single(RunnerCapabilityProbe.Advertise(options, gitPushReady: false, clock: _clock, hostBootId: BootId),
            item => item.Key == CapabilityProtocol.ZonePolicy);
        Assert.Equal("ready", capability.Status);
        Assert.Equal("2026-10-20.3", capability.Version);
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
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("2026-10-20.3", capability.Version);
        Assert.Contains("verification failed at 2026-10-20T10:30:00Z", capability.Detail);
        Assert.Contains("blocked", capability.Detail);
    }

    [Fact]
    public async Task Verify_record_refuses_when_the_rendered_rules_differ_from_the_applied_ones()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        resolver.Overrides["task-server.example.internal"] = ["203.0.113.9"];
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
        Assert.Equal("unavailable", ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!.Status);
    }

    [Fact]
    public async Task A_policy_error_under_execute_is_recorded_as_unverified()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        await File.WriteAllTextAsync(policyPath, "{ \"schemaVersion\": 1, \"version\": ");
        var error = new StringWriter();

        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Contains("not a valid zone policy document", error.ToString());
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.False(state.Verification.Passed);
        // The version of the record that the broken file replaced stays visible.
        Assert.Equal("2026-10-20.3", state.PolicyVersion);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Contains("setup policy expected valid, observed invalid (not a valid zone policy document", capability.Detail);
    }

    [Fact]
    public async Task A_denied_name_that_does_not_resolve_still_loads_the_other_rules_and_is_recorded_failed()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        resolver.Missing.Add("task-server.example.internal");

        var dryOutput = new StringWriter();
        var dryRun = await Run(["apply", "--policy", policyPath, "--state", statePath], host, resolver, dryOutput, new StringWriter());
        Assert.Equal(2, dryRun);
        Assert.Contains("--execute would load the other rules and record the state as failed", dryOutput.ToString());
        Assert.False(File.Exists(statePath));
        Assert.Empty(host.Mutations);

        var error = new StringWriter();
        var exit = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), error);

        Assert.Equal(2, exit);
        Assert.Contains("'task-server.example.internal' does not resolve (HostNotFound); its denial is missing from the rules",
            error.ToString());
        // Everything else is in place: the secret-store denial, the work allowlist, all jumps.
        Assert.Contains("--ctorigdst 192.0.2.80/32 ", host.LoadedRules);
        Assert.DoesNotContain("deny control-plane", host.LoadedRules);
        Assert.Contains("allow control-plane", host.LoadedRules);
        Assert.Equal(4, host.Jumps.Count);
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.Equal("2026-10-20.3", state.PolicyVersion);
        Assert.Equal(["work", "run"], state.Zones.Select(zone => zone.Name));
        Assert.Equal(BootId, state.BootId);
        Assert.False(state.Verification.Passed);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("work", capability.Identity);
        Assert.Contains("setup deny control-plane expected resolved, observed unresolved (destination control-plane: "
                        + "'task-server.example.internal' does not resolve (HostNotFound))", capability.Detail);
    }

    [Fact]
    public async Task An_io_error_or_another_exception_under_execute_is_recorded_as_unverified()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        // The resolver configuration path turns into a directory: writing it fails.
        File.Delete(ResolverConfigPath);
        Directory.CreateDirectory(ResolverConfigPath);
        var error = new StringWriter();

        var io = await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), error);

        Assert.Equal(2, io);
        Assert.Contains($"apply failed at zone resolver configuration {ResolverConfigPath}", error.ToString());
        Assert.Equal("unavailable", ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!.Status);
        Directory.Delete(ResolverConfigPath);

        // The probe cannot even be created: the exception is recorded, not lost.
        var thrown = new StringWriter();
        var exit = await ZonePolicyCommand.RunAsync(
            ["apply", "--resolver-config", ResolverConfigPath, "--policy", policyPath, "--state", statePath, "--execute"],
            new StringWriter(), thrown, host, resolver, _clock, ZonePolicyTestFiles.Environment,
            _ => throw new InvalidOperationException("docker socket vanished"), BootId, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("apply failed: InvalidOperationException: docker socket vanished", thrown.ToString());
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.Equal("2026-10-20.3", state.PolicyVersion);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Contains("setup apply expected applied, observed error (InvalidOperationException: docker socket vanished)", capability.Detail);
    }

    [Fact]
    public async Task Remove_is_a_dry_run_by_default_and_idempotent_with_execute()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        host.Commands.Clear();

        var dryOutput = new StringWriter();
        Assert.Equal(0, await Run(["remove", "--state", statePath], host, resolver, dryOutput, new StringWriter()));
        Assert.Empty(host.Mutations);
        Assert.Contains("[would-delete] iptables DOCKER-USER -j AGENT-ZONE-FWD", dryOutput.ToString());
        Assert.Contains("[would-delete] ip6tables chain AGENT-ZONE-IN", dryOutput.ToString());
        Assert.Null(ZonePolicyStateStore.TryRead(statePath)!.Value.State!.RemovedAt);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var output = new StringWriter();
        var exit = await Run(["remove", "--state", statePath, "--execute"], host, resolver, output, new StringWriter(), _clock);

        Assert.True(exit == 0, output.ToString());
        Assert.DoesNotContain(host.Jumps, jump => jump.Contains("AGENT-ZONE", StringComparison.Ordinal));
        Assert.Empty(host.Loaded);
        Assert.Contains("[deleted] iptables chain AGENT-ZONE-FWD", output.ToString());
        Assert.Contains("[unchanged] ip6tables FORWARD -j AGENT-ZONE-FWD", output.ToString());
        // Docker networks, ipsets and the resolver stay for the runbook's later steps.
        Assert.Equal(2, host.Networks.Count);
        Assert.True(host.Sets.Exists("agent-zone-git-hosts"));
        var removedAt = ZonePolicyStateStore.TryRead(statePath)!.Value.State!.RemovedAt;
        Assert.Equal(_clock.GetUtcNow(), removedAt);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("the zone rules were removed at 2026-10-20T09:35:00Z", capability.Detail);

        host.Commands.Clear();
        _clock.Advance(TimeSpan.FromMinutes(5));
        var again = new StringWriter();
        Assert.Equal(0, await Run(["remove", "--state", statePath, "--execute"], host, resolver, again, new StringWriter(), _clock));

        Assert.Empty(host.Mutations);
        Assert.All(again.ToString().Split('\n').Where(line => line.StartsWith("  [", StringComparison.Ordinal)),
            line => Assert.StartsWith("  [unchanged]", line));
        Assert.Equal(removedAt, ZonePolicyStateStore.TryRead(statePath)!.Value.State!.RemovedAt);

        // After a removal, verify --record has no applied state to update.
        var error = new StringWriter();
        Assert.Equal(2, await Run(["verify", "--policy", policyPath, "--state", statePath, "--record"], host, resolver, new StringWriter(), error));
        Assert.Contains("no applied state", error.ToString());
    }

    [Fact]
    public async Task A_remove_that_stops_partway_is_recorded_as_incomplete_until_a_rerun_completes_it()
    {
        var (host, resolver, policyPath, statePath) = Setup();
        await Run(["apply", "--policy", policyPath, "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter());
        host.Failing.Add("ip6tables -D DOCKER-USER -j AGENT-ZONE-FWD");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var error = new StringWriter();

        var exit = await Run(["remove", "--state", statePath, "--execute"], host, resolver, new StringWriter(), error, _clock);

        Assert.Equal(2, exit);
        // The IPv4 jumps are gone already, so the recorded verification no longer holds.
        Assert.DoesNotContain("iptables:DOCKER-USER>AGENT-ZONE-FWD", host.Jumps);
        Assert.Contains("recorded the incomplete removal", error.ToString());
        var state = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.Equal(_clock.GetUtcNow(), state.RemovedAt);
        Assert.Equal("failed at ip6tables DOCKER-USER -j AGENT-ZONE-FWD: simulated failure", state.RemovalProblem);
        var capability = ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", capability.Status);
        Assert.StartsWith("a removal of the zone rules stopped partway at 2026-10-20T09:35:00Z "
                          + "(failed at ip6tables DOCKER-USER -j AGENT-ZONE-FWD: simulated failure)", capability.Detail);

        host.Failing.Clear();
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await Run(["remove", "--state", statePath, "--execute"], host, resolver, new StringWriter(), new StringWriter(), _clock));

        var completed = ZonePolicyStateStore.TryRead(statePath)!.Value.State!;
        Assert.Null(completed.RemovalProblem);
        Assert.Equal(_clock.GetUtcNow(), completed.RemovedAt);
        Assert.Empty(host.Loaded);
        Assert.Equal("the zone rules were removed at 2026-10-20T09:40:00Z",
            ZonePolicyCapability.Describe(statePath, ZoneKinds.Work, _clock, BootId)!.Detail);
    }

    [Fact]
    public async Task Remove_without_root_is_unchecked_as_a_dry_run_and_fails_with_execute()
    {
        var host = new FakeZoneHost { Root = false };
        var statePath = Path.Combine(_root, "state", "applied.json");

        var output = new StringWriter();
        Assert.Equal(0, await Run(["remove", "--state", statePath], host, new FakeZoneResolver(), output, new StringWriter()));
        Assert.Contains("[unchecked] iptables DOCKER-USER -j AGENT-ZONE-FWD", output.ToString());

        var error = new StringWriter();
        Assert.Equal(2, await Run(["remove", "--state", statePath, "--execute"], host, new FakeZoneResolver(), new StringWriter(), error));
        Assert.Contains("remove failed at iptables DOCKER-USER -j AGENT-ZONE-FWD", error.ToString());
        Assert.False(File.Exists(statePath));
    }

    [Theory]
    [InlineData(new[] { "apply", "--force" }, "apply does not accept --force")]
    [InlineData(new[] { "verify", "--execute" }, "verify does not accept --execute")]
    [InlineData(new[] { "apply", "--state" }, "--state needs a value")]
    [InlineData(new[] { "remove", "--policy", "zone-policy.json" }, "remove does not accept --policy")]
    [InlineData(new[] { "purge" }, "usage: agent-host zone-policy render|apply|verify|remove")]
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
            BootId, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\nserver=/github.com/192.0.2.53\nipset=/github.com/", output.ToString());
        Assert.DoesNotContain("192.0.2.54", output.ToString());
    }

    [Fact]
    public async Task Render_out_dir_writes_both_payloads_the_resolver_configuration_and_the_plan()
    {
        var (host, resolver, policyPath, _) = Setup();
        var outDir = Path.Combine(_root, "rendered");

        var exit = await Run(["render", "--policy", policyPath, "--out-dir", outDir], host, resolver, new StringWriter(), new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal(["zone-dnsmasq.conf", "zone-plan.txt", "zone-rules.v4", "zone-rules.v6"],
            Directory.GetFiles(outDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(host.Commands);
    }

    private (FakeZoneHost Host, FakeZoneResolver Resolver, string PolicyPath, string StatePath) Setup()
    {
        var policyPath = Path.Combine(_root, "zone-policy.json");
        File.Copy(ZonePolicyTestFiles.CheckedInPolicyPath, policyPath);
        return (new FakeZoneHost(), new FakeZoneResolver(), policyPath, Path.Combine(_root, "state", "applied.json"));
    }

    private async Task<int> Run(
        string[] args, FakeZoneHost host, FakeZoneResolver resolver, TextWriter output, TextWriter error,
        FakeTimeProvider? clock = null, string bootId = BootId)
    {
        var networks = (await RulesAsync()).Networks;
        var config = ResolverConfigPath;
        if (args[0] == "apply") args = [args[0], "--resolver-config", config, .. args[1..]];
        return await ZonePolicyCommand.RunAsync(args, output, error, host, resolver,
            clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero)),
            ZonePolicyTestFiles.Environment,
            _ => new FakeZoneNetwork(networks, () => host.LoadedRules, resolver, host.Sets,
                () => File.Exists(config) ? File.ReadAllText(config) : null),
            bootId,
            CancellationToken.None);
    }
}
