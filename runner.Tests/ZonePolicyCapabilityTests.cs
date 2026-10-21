using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: coding, review and gate daemons report the zone policy version
/// they run under from the host's applied state, and only while that state
/// still describes the rules of this boot.
/// </summary>
public sealed class ZonePolicyCapabilityTests : IDisposable
{
    private const string BootId = "0f6e1d3c-boot-a";
    private static readonly DateTimeOffset VerifiedAt = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(VerifiedAt.AddMinutes(1));
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zone-capability-{Guid.NewGuid():N}");

    public ZonePolicyCapabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ZonePolicyState State(bool passed = true, string zoneKind = ZoneKinds.Work, string? bootId = BootId)
        => new(ZonePolicyStateStore.SchemaVersion, "2026-10-20.2", "policy-sha", "0123456789abcdef0123", VerifiedAt,
            [new ZonePolicyStateZone(zoneKind, zoneKind, [$"agent-{zoneKind}"])],
            new ZoneVerificationReport(passed, VerifiedAt,
            [
                new ZoneCheck(zoneKind, $"agent-{zoneKind}", "reachable", "github.com:443", "reachable", "connected", true),
                new ZoneCheck(zoneKind, $"agent-{zoneKind}", "blocked", "1.1.1.1:443", "blocked",
                    passed ? "rejected" : "connected", passed),
            ]),
            bootId);

    private static RunnerOptions Options(string role, string statePath) => new()
    {
        ServerUrl = "http://127.0.0.1:5031",
        RunnerId = $"{role}-a",
        RunnerName = $"{role}-a",
        Hostname = "host-a",
        BackendName = "test",
        Role = role,
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        ZonePolicyStatePath = statePath,
    };

    private AdvertisedCapabilityDto Describe(ZonePolicyState state, string? hostBootId = BootId)
        => ZonePolicyCapability.Describe(state, null, ZoneKinds.Work, _clock, hostBootId);

    [Theory]
    [InlineData("coding")]
    [InlineData("review")]
    [InlineData("gate")]
    public void Every_daemon_role_reports_the_applied_policy_version(string role)
    {
        var path = Path.Combine(_root, "applied.json");
        ZonePolicyStateStore.Write(path, State());

        var capability = Assert.Single(
            RunnerCapabilityProbe.Advertise(Options(role, path), gitPushReady: false, clock: _clock, hostBootId: BootId),
            item => item.Key == CapabilityProtocol.ZonePolicy);

        Assert.Equal("zone", capability.Category);
        Assert.Equal("ready", capability.Status);
        Assert.Equal("2026-10-20.2", capability.Version);
        Assert.Equal("work", capability.Identity);
        Assert.Equal("verifiedAt=2026-10-20T09:30:00Z; networks=agent-work; rules=sha256:0123456789ab", capability.Detail);
    }

    [Fact]
    public void A_host_without_an_applied_policy_reports_nothing()
    {
        var advertised = RunnerCapabilityProbe.Advertise(
            Options("review", Path.Combine(_root, "missing.json")), gitPushReady: false, clock: _clock, hostBootId: BootId);
        Assert.DoesNotContain(advertised, item => item.Key == CapabilityProtocol.ZonePolicy);
        Assert.DoesNotContain(RunnerCapabilityProbe.Advertise(Options("coding", ""), gitPushReady: false, clock: _clock, hostBootId: BootId),
            item => item.Key == CapabilityProtocol.ZonePolicy);
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("/var/lib/agent-studio/zone-policy/applied.json", null, "/var/lib/agent-studio/zone-policy/applied.json")]
    [InlineData(null, "/srv/zone/applied.json", "/srv/zone/applied.json")]
    public void Only_an_explicit_state_path_opts_a_daemon_in(string? runnerVariable, string? hostVariable, string expected)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RUNNER_ZONE_POLICY_STATE"] = runnerVariable,
            ["AGENT_HOST_ZONE_POLICY_STATE"] = hostVariable,
            ["RUNNER_STATE_DIR"] = Path.Combine(Path.GetTempPath(), "zone-policy-test-state"),
        };

        var (options, _, _, _) = RunnerOptions.Parse(["AGT-3231"], name => environment.GetValueOrDefault(name));

        Assert.Equal(expected, options.ZonePolicyStatePath);
    }

    [Fact]
    public void A_failed_verification_reports_unavailable_with_the_first_failure()
    {
        var capability = Describe(State(passed: false));

        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("2026-10-20.2", capability.Version);
        Assert.Equal("verification failed at 2026-10-20T09:30:00Z: 1 of 2 checks; blocked 1.1.1.1:443 expected blocked, "
                     + "observed connected; networks=agent-work; rules=sha256:0123456789ab", capability.Detail);
    }

    [Fact]
    public void A_record_from_an_earlier_boot_reports_unavailable()
    {
        var capability = Describe(State(), hostBootId: "7d2a9b41-boot-b");

        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("work", capability.Identity);
        Assert.StartsWith("recorded before the last reboot (verified at 2026-10-20T09:30:00Z)", capability.Detail);
        Assert.Equal("unavailable", Describe(State(bootId: null)).Status);
        Assert.Contains("boot id is unreadable", Describe(State(), hostBootId: null).Detail);
    }

    [Fact]
    public void A_verification_older_than_twice_the_timer_interval_reports_unavailable()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), ZonePolicyCapability.MaxVerificationAge);
        _clock.SetUtcNow(VerifiedAt + ZonePolicyCapability.MaxVerificationAge);
        Assert.Equal("ready", Describe(State()).Status);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var stale = Describe(State());

        Assert.Equal("unavailable", stale.Status);
        Assert.Equal("2026-10-20.2", stale.Version);
        Assert.Equal("last verification at 2026-10-20T09:30:00Z is older than 30 minutes; check agent-zone-policy.timer",
            stale.Detail);
    }

    [Fact]
    public void A_removed_policy_reports_unavailable()
    {
        var capability = Describe(State() with { RemovedAt = VerifiedAt.AddMinutes(1) });

        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("the zone rules were removed at 2026-10-20T09:31:00Z", capability.Detail);
    }

    [Fact]
    public void A_removal_that_stopped_partway_reports_unavailable()
    {
        var capability = Describe(State() with
        {
            RemovedAt = VerifiedAt.AddMinutes(1),
            RemovalProblem = "failed at iptables chain AGENT-ZONE-IN: Device or resource busy",
        });

        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("a removal of the zone rules stopped partway at 2026-10-20T09:31:00Z "
                     + "(failed at iptables chain AGENT-ZONE-IN: Device or resource busy); "
                     + "rerun zone-policy remove --execute or apply --execute", capability.Detail);
    }

    [Fact]
    public void A_policy_without_a_work_zone_or_an_unreadable_state_reports_unavailable()
    {
        var runOnly = ZonePolicyCapability.Describe(State(zoneKind: ZoneKinds.Run), null, ZoneKinds.Work, _clock, BootId);
        Assert.Equal("unavailable", runOnly.Status);
        Assert.Equal("the applied policy has no work zone", runOnly.Detail);

        var path = Path.Combine(_root, "applied.json");
        File.WriteAllText(path, "{ not json");
        var unreadable = ZonePolicyCapability.Describe(path, ZoneKinds.Work, _clock, BootId)!;
        Assert.Equal("unavailable", unreadable.Status);
        Assert.Contains("is unreadable: JsonException", unreadable.Detail);

        // A record of the first cut has no boot id and proves nothing about this boot.
        File.WriteAllText(path, ZonePolicyStateStore.Serialize(State()).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"));
        Assert.Equal($"unsupported zone policy state in {path}", ZonePolicyCapability.Describe(path, ZoneKinds.Work, _clock, BootId)!.Detail);
    }

    [Fact]
    public void State_round_trips_through_the_store()
    {
        var path = Path.Combine(_root, "nested", "applied.json");
        var state = State() with { RemovedAt = VerifiedAt.AddHours(1) };

        ZonePolicyStateStore.Write(path, state);
        var read = ZonePolicyStateStore.TryRead(path)!.Value;

        Assert.Null(read.Error);
        Assert.Equal(state.PolicyVersion, read.State!.PolicyVersion);
        Assert.Equal(BootId, read.State.BootId);
        Assert.Equal(state.RemovedAt, read.State.RemovedAt);
        Assert.Equal(state.Verification.Checks, read.State.Verification.Checks);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(path));
    }

    [Fact]
    public void Boot_id_reads_the_kernel_file_and_is_null_without_it()
    {
        var path = Path.Combine(_root, "boot_id");
        File.WriteAllText(path, BootId + "\n");

        Assert.Equal(BootId, ZonePolicyStateStore.CurrentBootId(path));
        Assert.Null(ZonePolicyStateStore.CurrentBootId(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void The_systemd_units_match_the_staleness_window_and_start_at_boot()
    {
        var systemd = Path.Combine(ZonePolicyTestFiles.RepoRoot(), "deploy", "systemd");
        var timer = File.ReadAllLines(Path.Combine(systemd, "agent-zone-policy.timer"));
        var service = File.ReadAllText(Path.Combine(systemd, "agent-zone-policy.service")).ReplaceLineEndings("\n");

        Assert.Contains($"OnUnitActiveSec={ZonePolicyCapability.VerificationInterval.TotalMinutes:0}min", timer);
        Assert.Contains("\n[Install]\nWantedBy=multi-user.target\n", service);
        // apply starts the resolver with a blocking systemctl call, so the
        // resolver unit must not order itself after the policy unit.
        var resolver = File.ReadAllText(Path.Combine(systemd, "agent-zone-dns.service"));
        Assert.DoesNotContain("agent-zone-policy.service", resolver.Split('\n').Where(line => !line.StartsWith('#')));
    }
}
