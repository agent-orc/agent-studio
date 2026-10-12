using AgentStudio.Shared;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the selector matches the fixed 2026-10-11 incident date and never reads the clock.

public sealed class DeliveryBounceRepairPolicyTests
{
    [Fact]
    public void SelectsOnlyTheIncidentBounceWithCurrentIntegrationProof()
    {
        var card = new TaskInfo { State = TaskStates.HumanReview };
        var integrated = new TaskIntegrationStatus { Status = IntegrationStatuses.Integrated };
        var bounce = Move(TaskStates.Completed, TaskStates.AutoReview,
            new DateTime(2026, 10, 11, 7, 40, 0, DateTimeKind.Utc));
        var returnToReview = Move(TaskStates.AutoReview, TaskStates.HumanReview,
            new DateTime(2026, 10, 11, 7, 41, 0, DateTimeKind.Utc));

        Assert.True(DeliveryBounceRepairPolicy.IsEligible(card, integrated,
            [bounce, returnToReview]));
        Assert.False(DeliveryBounceRepairPolicy.IsEligible(card,
            integrated with { Status = IntegrationStatuses.Pending }, [bounce, returnToReview]));
        Assert.True(DeliveryBounceRepairPolicy.IsEligible(card,
            integrated with { Status = IntegrationStatuses.Pending },
            [bounce, returnToReview], hasOperatorOverride: true));
        Assert.False(DeliveryBounceRepairPolicy.IsEligible(card,
            integrated, [bounce, returnToReview, Move(TaskStates.HumanReview,
                TaskStates.AutoReview, returnToReview.Ts.AddMinutes(1))]));
        Assert.False(DeliveryBounceRepairPolicy.IsEligible(card,
            integrated, [bounce with { Ts = bounce.Ts.AddDays(1) }, returnToReview]));
    }

    private static TimelineEvent Move(string from, string to, DateTime when) => new()
    {
        Ts = when,
        Kind = TimelineEventKinds.LaneChanged,
        Details = new Dictionary<string, string>
        {
            ["from"] = from,
            ["to"] = to,
            [LaneChangeCauses.DetailKey] = LaneChangeCauses.AcceptanceIntegrationFailed,
            [LaneChangeCauses.DetailQualifierKey] = "delivery-verdict-stale",
        },
    };
}
