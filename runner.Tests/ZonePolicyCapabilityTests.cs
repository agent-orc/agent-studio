using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: coding, review and gate daemons report the zone policy version
/// they run under from the host's applied state.
/// </summary>
public sealed class ZonePolicyCapabilityTests : IDisposable
{
    // clock-independent: the state carries a fixed verification time that is only formatted, never compared with now.
    private static readonly DateTimeOffset VerifiedAt = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zone-capability-{Guid.NewGuid():N}");

    public ZonePolicyCapabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ZonePolicyState State(bool passed = true, string zoneKind = ZoneKinds.Work)
        => new(ZonePolicyStateStore.SchemaVersion, "2026-10-20.1", "policy-sha", "0123456789abcdef0123", VerifiedAt,
            [new ZonePolicyStateZone(zoneKind, zoneKind, [$"agent-{zoneKind}"])],
            new ZoneVerificationReport(passed, VerifiedAt,
            [
                new ZoneCheck(zoneKind, $"agent-{zoneKind}", "reachable", "github.com:443", "reachable", "connected", true),
                new ZoneCheck(zoneKind, $"agent-{zoneKind}", "blocked", "1.1.1.1:443", "blocked",
                    passed ? "refused" : "connected", passed),
            ]));

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

    [Theory]
    [InlineData("coding")]
    [InlineData("review")]
    [InlineData("gate")]
    public void Every_daemon_role_reports_the_applied_policy_version(string role)
    {
        var path = Path.Combine(_root, "applied.json");
        ZonePolicyStateStore.Write(path, State());

        var capability = Assert.Single(RunnerCapabilityProbe.Advertise(Options(role, path), gitPushReady: false),
            item => item.Key == CapabilityProtocol.ZonePolicy);

        Assert.Equal("zone", capability.Category);
        Assert.Equal("ready", capability.Status);
        Assert.Equal("2026-10-20.1", capability.Version);
        Assert.Equal("work", capability.Identity);
        Assert.Equal("verifiedAt=2026-10-20T09:30:00Z; networks=agent-work; rules=sha256:0123456789ab", capability.Detail);
    }

    [Fact]
    public void A_host_without_an_applied_policy_reports_nothing()
    {
        var advertised = RunnerCapabilityProbe.Advertise(
            Options("review", Path.Combine(_root, "missing.json")), gitPushReady: false);
        Assert.DoesNotContain(advertised, item => item.Key == CapabilityProtocol.ZonePolicy);
        Assert.DoesNotContain(RunnerCapabilityProbe.Advertise(Options("coding", ""), gitPushReady: false),
            item => item.Key == CapabilityProtocol.ZonePolicy);
    }

    [Fact]
    public void A_failed_verification_reports_unavailable_with_the_first_failure()
    {
        var capability = ZonePolicyCapability.Describe(State(passed: false), null, ZoneKinds.Work);

        Assert.Equal("unavailable", capability.Status);
        Assert.Equal("2026-10-20.1", capability.Version);
        Assert.Equal("verification failed at 2026-10-20T09:30:00Z: 1 of 2 checks; blocked 1.1.1.1:443 expected blocked, "
                     + "observed connected; networks=agent-work; rules=sha256:0123456789ab", capability.Detail);
    }

    [Fact]
    public void A_policy_without_a_work_zone_or_an_unreadable_state_reports_unavailable()
    {
        var runOnly = ZonePolicyCapability.Describe(State(zoneKind: ZoneKinds.Run), null, ZoneKinds.Work);
        Assert.Equal("unavailable", runOnly.Status);
        Assert.Equal("the applied policy has no work zone", runOnly.Detail);

        var path = Path.Combine(_root, "applied.json");
        File.WriteAllText(path, "{ not json");
        var unreadable = ZonePolicyCapability.Describe(path, ZoneKinds.Work)!;
        Assert.Equal("unavailable", unreadable.Status);
        Assert.Contains("is unreadable: JsonException", unreadable.Detail);
    }

    [Fact]
    public void State_round_trips_through_the_store()
    {
        var path = Path.Combine(_root, "nested", "applied.json");
        var state = State();

        ZonePolicyStateStore.Write(path, state);
        var read = ZonePolicyStateStore.TryRead(path)!.Value;

        Assert.Null(read.Error);
        Assert.Equal(state.PolicyVersion, read.State!.PolicyVersion);
        Assert.Equal(state.Verification.Checks, read.State.Verification.Checks);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(path));
    }
}
