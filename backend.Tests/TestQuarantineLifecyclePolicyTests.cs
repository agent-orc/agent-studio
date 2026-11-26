using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestQuarantineLifecyclePolicyTests
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData("base suite", false, false)]
    public void Release_requires_no_line_stop_and_no_held_quarantine(string? lineStop, bool held, bool expected)
        => Assert.Equal(expected, TestQuarantineLifecyclePolicy.MayCreateRelease(lineStop, held));

    [Fact]
    public void A_line_stop_holds_even_unrelated_deliveries_and_admits_the_repair()
    {
        Assert.False(TestQuarantineLifecyclePolicy.MayIntegrateLineStop("base suite", "unrelated", ["repair"]));
        Assert.True(TestQuarantineLifecyclePolicy.MayIntegrateLineStop("base suite", "repair", ["repair"]));
        Assert.True(TestQuarantineLifecyclePolicy.MayIntegrateLineStop(null, "unrelated", ["repair"]));
    }

    [Fact]
    public void Timer_raises_the_rung_after_two_hours_without_changing_a_delivery_owned_repair()
    {
        var facts = new RedTestTriageFacts("test", "routine", RedTestCategory.Routine)
        { FirstResult = RedTestResult.Fail, SameShaRepeat = RedTestResult.Fail, EnvelopeCanary = RedTestResult.Pass,
            FingerprintMatchesReference = true, IncidentAge = TimeSpan.FromHours(2), RepairCardLane = TaskStates.Ready };
        Assert.Equal(RedTestModelRung.High, RedTestTriagePolicy.Decide(facts).Decisions.Single(action => action.Action == RedTestAction.FixForward).ModelRung);
        Assert.Equal(RedTestModelRung.Medium, RedTestTriagePolicy.Decide(facts with { RepairCardLane = TaskStates.AutoReview })
            .Decisions.Single(action => action.Action == RedTestAction.FixForward).ModelRung);
    }
}
