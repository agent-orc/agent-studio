using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewPlaneBudgetProbeTests
{
    [Fact]
    public void Cpu_max_parses_unlimited_quota()
    {
        var parsed = ReviewPlaneBudgetProbe.ParseCpuMax("max 100000\n");

        Assert.True(parsed.IsUnlimited);
        Assert.Null(parsed.QuotaCores);
        Assert.Null(parsed.QuotaPercent);
        Assert.Equal("max 100000", parsed.Raw);
    }

    [Fact]
    public void Cpu_max_parses_numeric_quota()
    {
        var parsed = ReviewPlaneBudgetProbe.ParseCpuMax("400000 100000");

        Assert.False(parsed.IsUnlimited);
        Assert.Equal(4, parsed.QuotaCores);
        Assert.Equal(400, parsed.QuotaPercent);
    }

    [Theory]
    [InlineData("0-11", 12)]
    [InlineData("0-3,8-11", 8)]
    [InlineData("0,2,4", 3)]
    public void Online_cpu_topology_reports_host_cores_independently_of_role_quota(
        string onlineCpus,
        int expected)
    {
        Assert.Equal(expected, ReviewPlaneBudgetProbe.ParseOnlineCpuCount(onlineCpus));
    }
}
