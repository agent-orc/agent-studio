using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3719: which moves the shared active-batch membership guard judges.
/// Operator causes are refused; the runner, the batch gate and the
/// orchestrator keep their own Auto Review transitions.
/// </summary>
public sealed class ActiveBatchMemberGuardPolicyTests
{
    [Theory]
    [InlineData("human", true)]
    [InlineData("human:local-default", true)]
    [InlineData("operator-agent:watch/session:s-1", true)]
    [InlineData("service-account:svc-1", true)]
    [InlineData(TimelineActors.System, false)]
    [InlineData("batch-gate:batch-1", false)]
    [InlineData("remote-review:rev-1", false)]
    [InlineData(null, false)]
    public void Only_operator_causes_are_judged(string? cause, bool expected)
        => Assert.Equal(expected, ActiveBatchMemberGuardPolicy.IsOperatorCause(cause));

    [Theory]
    [InlineData(BatchPhase.Formed, true)]
    [InlineData(BatchPhase.Running, true)]
    [InlineData(BatchPhase.Publishing, true)]
    [InlineData(BatchPhase.Paused, true)]
    [InlineData(BatchPhase.Unknown, true)]
    [InlineData(BatchPhase.Published, false)]
    [InlineData(BatchPhase.Red, false)]
    [InlineData(BatchPhase.Abandoned, false)]
    public void A_batch_stays_active_until_it_finishes(BatchPhase phase, bool expected)
        => Assert.Equal(expected, ActiveBatchMemberGuardPolicy.IsActive(phase));

    [Theory]
    [InlineData(TaskStates.AutoReview, TaskStates.Backlog, true)]
    [InlineData(TaskStates.AutoReview, TaskStates.HumanReview, true)]
    [InlineData(TaskStates.AutoReview, TaskStates.AutoReview, false)]
    [InlineData(TaskStates.Progress, TaskStates.Backlog, false)]
    public void Only_a_move_out_of_auto_review_is_judged(string from, string to, bool expected)
        => Assert.Equal(expected, ActiveBatchMemberGuardPolicy.LeavesAutoReview(from, to));

    [Fact]
    public void Refusal_names_the_batch_its_phase_and_its_other_members()
    {
        var membership = new ActiveBatchMembership("demo", "DEMO-1", "batch-7", BatchPhase.Running, ["DEMO-2", "DEMO-3"]);
        var message = ActiveBatchMemberGuardPolicy.Message(membership);
        Assert.Contains("batch-7 (running)", message);
        Assert.Contains("DEMO-2, DEMO-3", message);
    }
}
