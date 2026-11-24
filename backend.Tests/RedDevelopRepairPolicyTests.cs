using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RedDevelopRepairPolicyTests
{
    [Theory]
    [InlineData(1, true, false, 119, RedDevelopRepairAction.Revert)]
    [InlineData(0, true, false, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(2, true, false, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(1, false, false, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(1, true, true, 119, RedDevelopRepairAction.FixForward)]
    [InlineData(1, true, false, 120, RedDevelopRepairAction.AskOwner)]
    [InlineData(0, false, true, 121, RedDevelopRepairAction.AskOwner)]
    [InlineData(0, false, false, 60 * 72, RedDevelopRepairAction.AskOwner)]
    public void D2DecisionTable(int suspects, bool clean, bool dependent, int incidentMinutes,
        RedDevelopRepairAction expected)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(suspects, clean, dependent,
            TimeSpan.FromMinutes(incidentMinutes)));
        Assert.Equal(expected, decision.Action);
        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void The_vocabulary_has_no_escalation_or_lane_move()
        => Assert.Equal(["Revert", "FixForward", "Hold", "AskOwner"], Enum.GetNames<RedDevelopRepairAction>());

    [Theory]
    [InlineData(1, true, false, RedDevelopOwnerOption.RevertAttributedMerge)]
    [InlineData(0, true, false, RedDevelopOwnerOption.KeepFixingForward)]
    [InlineData(2, true, false, RedDevelopOwnerOption.KeepFixingForward)]
    [InlineData(1, false, false, RedDevelopOwnerOption.KeepFixingForward)]
    [InlineData(1, true, true, RedDevelopOwnerOption.KeepFixingForward)]
    public void The_owner_question_recommends_revert_only_for_one_clean_independent_suspect(
        int suspects, bool clean, bool dependent, RedDevelopOwnerOption expected)
    {
        var decision = RedDevelopRepairPolicy.Decide(new(suspects, clean, dependent, TimeSpan.FromHours(2)));

        Assert.Equal(RedDevelopRepairAction.AskOwner, decision.Action);
        var question = Assert.IsType<RedDevelopOwnerQuestion>(decision.Question);
        Assert.Equal([RedDevelopOwnerOption.RevertAttributedMerge, RedDevelopOwnerOption.KeepFixingForward,
            RedDevelopOwnerOption.SwitchProjectToStrict], question.Options);
        Assert.Equal(expected, question.Recommended);
        Assert.NotEmpty(question.RecommendationReason);
        Assert.Equal(expected == RedDevelopOwnerOption.RevertAttributedMerge
            ? RedDevelopRepairAction.Revert : RedDevelopRepairAction.FixForward, decision.Repair);
    }

    [Fact]
    public void Only_the_owner_decision_carries_a_question()
    {
        Assert.Null(RedDevelopRepairPolicy.Decide(new(1, true, false, TimeSpan.FromMinutes(119))).Question);
        Assert.Null(RedDevelopRepairPolicy.Decide(new(1, true, false, TimeSpan.FromMinutes(5),
            TaskStates.AutoReview)).Question);
    }

    [Theory]
    [InlineData(TaskStates.Completed, RecurringRepairCardAction.CreateNew)]
    [InlineData(TaskStates.Archive, RecurringRepairCardAction.CreateNew)]
    [InlineData(null, RecurringRepairCardAction.CreateNew)]
    [InlineData(TaskStates.Progress, RecurringRepairCardAction.Attach)]
    [InlineData(TaskStates.AutoReview, RecurringRepairCardAction.Attach)]
    [InlineData(TaskStates.HumanReview, RecurringRepairCardAction.Attach)]
    [InlineData(TaskStates.Backlog, RecurringRepairCardAction.Reopen)]
    [InlineData(TaskStates.Preparation, RecurringRepairCardAction.Reopen)]
    [InlineData(TaskStates.OrchestratorPrep, RecurringRepairCardAction.Reopen)]
    [InlineData(TaskStates.Ready, RecurringRepairCardAction.Reopen)]
    [InlineData(TaskStates.FailedPickup, RecurringRepairCardAction.Reopen)]
    [InlineData(TaskStates.CodeNotComplete, RecurringRepairCardAction.Reopen)]
    [InlineData(TaskStates.Escalated, RecurringRepairCardAction.Reopen)]
    public void A_recurring_fingerprint_never_pulls_a_delivery_or_settled_card_back(string? lane,
        RecurringRepairCardAction expected)
        => Assert.Equal(expected, RedDevelopRepairPolicy.RecurringCard(lane));

    [Fact]
    public void ContainmentHoldsOnlyOverlappingMergesAndEveryReleaseCandidate()
    {
        Assert.False(RedDevelopRepairPolicy.MayMerge(["backend/A.cs"], ["backend/A.cs"]));
        Assert.True(RedDevelopRepairPolicy.MayMerge(["frontend/B.ts"], ["backend/A.cs"]));
        Assert.False(RedDevelopRepairPolicy.MayCreateRelease(true));
        Assert.True(RedDevelopRepairPolicy.MayCreateRelease(false));
    }
}
