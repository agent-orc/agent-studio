using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteRequeueLogLimiterTests
{
    [Fact]
    public void Deferral_is_logged_once_per_lease()
    {
        var limiter = new RemoteRequeueLogLimiter();
        Assert.True(limiter.ShouldLog("AGT-1", "lease-a"));
        Assert.False(limiter.ShouldLog("AGT-1", "lease-a"));
        Assert.True(limiter.ShouldLog("AGT-1", "lease-b"));
        Assert.False(limiter.ShouldLog("AGT-1", "lease-a"));
    }

    [Fact]
    public void Active_lease_stays_suppressed_after_more_than_ten_thousand_other_cards()
    {
        var limiter = new RemoteRequeueLogLimiter();
        Assert.True(limiter.ShouldLog("AGT-active", "lease-active"));
        for (var i = 0; i < 10_001; i++)
            Assert.True(limiter.ShouldLog($"AGT-{i}", $"lease-{i}"));

        Assert.False(limiter.ShouldLog("AGT-active", "lease-active"));
        limiter.RetainProgressTasks(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AGT-active" });
        Assert.False(limiter.ShouldLog("AGT-active", "lease-active"));
        Assert.True(limiter.ShouldLog("AGT-0", "new-lease"));
        limiter.ForgetTask("AGT-active");
        Assert.True(limiter.ShouldLog("AGT-active", "next-lease"));
    }
}
