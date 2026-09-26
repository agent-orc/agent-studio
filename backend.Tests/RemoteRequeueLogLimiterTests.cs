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
    }
}
