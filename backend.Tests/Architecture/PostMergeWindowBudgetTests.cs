using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests.Architecture;

// clock-independent: opening and observation times are fixed policy inputs.
public sealed class PostMergeWindowBudgetTests
{
    [Theory]
    [InlineData(29, "busy", false)]
    [InlineData(30, "busy", false)]
    [InlineData(31, "busy", true)]
    [InlineData(31, null, false)]
    public void WindowBudgetSignalsOnlyOverdueAdmissionWait(int ageMinutes, string? admission, bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
        var observation = new PostMergeWindowObservation(new string('a', 40), now.AddMinutes(-ageMinutes), now, 30, admission);
        Assert.Equal(expected, observation.Starved);
        Assert.False((observation with { OpenedAtUtc = null }).Starved);
    }
}
