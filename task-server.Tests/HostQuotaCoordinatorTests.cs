using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.TaskServer.Tests;

public sealed class HostQuotaCoordinatorTests
{
    private const string AccountOne = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AccountTwo = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTime Now = new(2026, 11, 1, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FiveHealthyHostsProduceOneReadingPerTtl()
    {
        var coordinator = new HostQuotaCoordinator();
        var hosts = Enumerable.Range(1, 5).Select(i => "host-" + i).ToHashSet();
        var claims = hosts.Select(host => (Host: host, Claim: coordinator.Claim(host,
            new("instance-" + host, "codex", AccountOne), Now, true, hosts))).ToArray();
        var granted = Assert.Single(claims.Where(item => item.Claim.Granted));
        Assert.All(claims.Where(item => !item.Claim.Granted), item => Assert.Null(item.Claim.ProbeId));
        Assert.True(coordinator.Report(granted.Host, new(
            "instance-" + granted.Host, "codex", AccountOne, granted.Claim.ProbeId!,
            Now.AddSeconds(20), null, "Plus", [new("Weekly", 18, null, null)], null),
            Now.AddSeconds(20), true));
        Assert.All(hosts, host => Assert.False(coordinator.Claim(host,
            new("instance-" + host, "codex", AccountOne), Now.AddMinutes(9), true, hosts).Granted));
        Assert.Single(coordinator.Readings());
        Assert.True(coordinator.Claim("host-2", new("instance-host-2", "codex", AccountOne),
            Now.AddMinutes(11), true, hosts).Granted);
    }

    [Fact]
    public void AnotherHostTakesOverAfterOwnerStopsOrSignInFails()
    {
        var coordinator = new HostQuotaCoordinator();
        var both = new HashSet<string> { "host-a", "host-b" };
        var initial = coordinator.Claim("host-a", new("instance-a", "claude", AccountOne), Now, true, both);
        Assert.True(initial.Granted);
        Assert.True(coordinator.Claim("host-b", new("instance-b", "claude", AccountOne),
            Now.AddSeconds(5), true, new HashSet<string> { "host-b" }).Granted);

        var failed = new HostQuotaCoordinator();
        var first = failed.Claim("host-a", new("instance-a", "claude", AccountOne), Now, true, both);
        Assert.True(failed.Report("host-a", new("instance-a", "claude", AccountOne,
            first.ProbeId!, Now.AddSeconds(4), null, null, [], "Sign-in failed"),
            Now.AddSeconds(4), true));
        Assert.False(failed.Claim("host-a", new("instance-a", "claude", AccountOne),
            Now.AddSeconds(5), true, both).Granted);
        Assert.True(failed.Claim("host-b", new("instance-b", "claude", AccountOne),
            Now.AddSeconds(5), true, both).Granted);
    }

    [Fact]
    public void SeparateAccountDigestsNeverShareAProbeOrReading()
    {
        var coordinator = new HostQuotaCoordinator();
        var hosts = new HashSet<string> { "host-a", "host-b" };
        var first = coordinator.Claim("host-a", new("i-a", "codex", AccountOne), Now, true, hosts);
        var second = coordinator.Claim("host-b", new("i-b", "codex", AccountTwo), Now, true, hosts);
        Assert.True(first.Granted);
        Assert.True(second.Granted);
        Assert.True(coordinator.Report("host-a", new("i-a", "codex", AccountOne,
            first.ProbeId!, Now, null, "Plus", [new("Weekly", 18, null, null)], null), Now, true));
        Assert.True(coordinator.Report("host-b", new("i-b", "codex", AccountTwo,
            second.ProbeId!, Now, null, "Pro", [new("Weekly", 80, null, null)], null), Now, true));
        Assert.Equal(2, coordinator.Readings().Count);
        Assert.Equal(18, coordinator.Readings().Single(r => r.SubscriptionKey == AccountOne).Windows[0].UsedPct);
        Assert.Equal(80, coordinator.Readings().Single(r => r.SubscriptionKey == AccountTwo).Windows[0].UsedPct);
    }

    [Fact]
    public void HostWithoutProviderCapabilityCannotClaimOrReport()
    {
        var coordinator = new HostQuotaCoordinator();
        var hosts = new HashSet<string> { "host-a" };
        Assert.False(coordinator.Claim("host-a", new("i-a", "claude", AccountOne),
            Now, false, hosts).Granted);
        var claim = coordinator.Claim("host-a", new("i-a", "claude", AccountOne),
            Now, true, hosts);
        Assert.False(coordinator.Report("host-a", new("i-a", "claude", AccountOne,
            claim.ProbeId!, Now, null, null, [new("Weekly", 20, null, null)], null),
            Now, false));
        Assert.Empty(coordinator.Readings());
    }

    [Fact]
    public void ReportRejectsClearEmailInParsedFields()
    {
        var coordinator = new HostQuotaCoordinator();
        var hosts = new HashSet<string> { "host-a" };
        var claim = coordinator.Claim("host-a", new("i-a", "codex", AccountOne),
            Now, true, hosts);
        var email = "person" + "@" + "example.test";
        Assert.False(coordinator.Report("host-a", new("i-a", "codex", AccountOne,
            claim.ProbeId!, Now, null, email, [new("Weekly", 18, null, null)], null),
            Now, true));
        Assert.Empty(coordinator.Readings());
    }

    [Fact]
    public void ReportAcceptsIanaTimezoneInResetLabel()
    {
        var coordinator = new HostQuotaCoordinator();
        var hosts = new HashSet<string> { "host-a" };
        var claim = coordinator.Claim("host-a", new("i-a", "claude", AccountOne), Now, true, hosts);
        Assert.True(coordinator.Report("host-a", new("i-a", "claude", AccountOne,
            claim.ProbeId!, Now, null, null,
            [new("Current session (5h)", 25, null, "3:40am (America/New_York)")], null),
            Now, true));
        Assert.Single(coordinator.Readings());
    }
}
