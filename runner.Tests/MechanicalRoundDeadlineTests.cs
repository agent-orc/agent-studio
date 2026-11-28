using Xunit;

namespace AgentRunner.Tests;

public sealed class MechanicalRoundDeadlineTests
{
    [Fact]
    public void CpuStarvationDoesNotSpendTheResumedRoundLimit()
    {
        var deadline = new MechanicalRoundDeadline(TimeSpan.FromSeconds(300), 100_000_000);
        Assert.Equal(TimeSpan.FromSeconds(240), deadline.Remaining(TimeSpan.FromSeconds(360), 400_000_000));
        Assert.Equal(TimeSpan.FromSeconds(300), deadline.ExcludedCpuWait);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining(TimeSpan.FromSeconds(600), 400_000_000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void WithoutCpuStallTheExistingDurationBoundStillApplies(long? counter)
    {
        var deadline = new MechanicalRoundDeadline(TimeSpan.FromSeconds(60), counter);
        Assert.Equal(TimeSpan.FromSeconds(1), deadline.Remaining(TimeSpan.FromSeconds(59), counter));
        Assert.Equal(TimeSpan.Zero, deadline.Remaining(TimeSpan.FromSeconds(60), counter));
    }

    [Fact]
    public void MissingOrResetCountersCannotGrantUnmeasuredExtraTime()
    {
        var deadline = new MechanicalRoundDeadline(TimeSpan.FromSeconds(300), 100_000_000);
        Assert.Equal(TimeSpan.FromSeconds(290), deadline.Remaining(TimeSpan.FromSeconds(10), null));
        Assert.Equal(TimeSpan.FromSeconds(280), deadline.Remaining(TimeSpan.FromSeconds(20), 0));
        Assert.Equal(TimeSpan.FromSeconds(280), deadline.Remaining(TimeSpan.FromSeconds(30), 500_000_000));
        Assert.Equal(TimeSpan.FromSeconds(270), deadline.Remaining(TimeSpan.FromSeconds(40), 0));
    }

    [Fact]
    public void OnlyWorkerFullPressureCountsInMicroseconds()
    {
        Assert.Equal(42, MechanicalRoundDeadline.ParseCpuStallMicroseconds(
            "some avg10=90.00 total=999999\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=42\n"));
        Assert.Null(MechanicalRoundDeadline.ParseCpuStallMicroseconds("some total=999\n"));
        Assert.Null(MechanicalRoundDeadline.ParseCpuStallMicroseconds("full total=invalid\n"));
    }
}
