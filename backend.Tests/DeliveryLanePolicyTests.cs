using AgentStudio.Shared;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DeliveryLanePolicyTests
{
    [Theory]
    [InlineData(TaskStates.AutoReview, TaskStates.HumanReview)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed)]
    [InlineData(TaskStates.Completed, TaskStates.Archive)]
    public void EveryGuardedEdge_AdmitsOnlyIntegratedOrNotApplicable(string source, string target)
    {
        Assert.True(DeliveryLanePolicy.Decide(source, target, true, IntegrationStatuses.Integrated).Allowed);
        Assert.True(DeliveryLanePolicy.Decide(source, target, false, IntegrationStatuses.NotApplicable).Allowed);
        Assert.False(DeliveryLanePolicy.Decide(source, target, true, IntegrationStatuses.NotApplicable).Allowed);
        foreach (var status in new[]
        {
            IntegrationStatuses.Pending, IntegrationStatuses.Partial,
            IntegrationStatuses.MergedLocally, IntegrationStatuses.NoBranch,
            IntegrationStatuses.ConflictSkipped,
        })
            Assert.False(DeliveryLanePolicy.Decide(source, target, true, status).Allowed);
    }

    [Fact]
    public void ArchiveOverride_IsOneCardAndOnlyAffectsArchive()
    {
        Assert.True(DeliveryLanePolicy.Decide(TaskStates.Completed, TaskStates.Archive,
            true, IntegrationStatuses.ConflictSkipped, archiveOverride: true).Allowed);
        Assert.False(DeliveryLanePolicy.Decide(TaskStates.HumanReview, TaskStates.Completed,
            true, IntegrationStatuses.ConflictSkipped, archiveOverride: true).Allowed);
        Assert.False(DeliveryLanePolicy.Decide(TaskStates.AutoReview, TaskStates.HumanReview,
            true, IntegrationStatuses.ConflictSkipped, archiveOverride: true).Allowed);
    }

    [Fact]
    public void IntegrationPhase_EscalatesTypedGateFailureEvenWhenStatusIsPending()
    {
        Assert.True(DeliveryLanePolicy.RequiresEscalation(new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Pending,
            Failure = new TaskIntegrationFailure { Code = "gate-failed" },
        }));
        Assert.True(DeliveryLanePolicy.RequiresEscalation(new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.NoBranch,
        }));
        Assert.False(DeliveryLanePolicy.RequiresEscalation(new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Integrated,
            Failure = new TaskIntegrationFailure { Code = "earlier-gate-failed" },
        }));
    }

    [Fact]
    public void EveryKnownLaneEdge_UsesTheSameProtectedDestinationMatrix()
    {
        foreach (var source in TaskStates.All)
        foreach (var target in TaskStates.All)
        {
            var protectedDestination = target is TaskStates.HumanReview
                or TaskStates.Completed or TaskStates.Archive;
            Assert.Equal(!protectedDestination,
                DeliveryLanePolicy.Decide(source, target, true, IntegrationStatuses.Pending).Allowed);
            Assert.True(DeliveryLanePolicy.Decide(source, target, true,
                IntegrationStatuses.Integrated).Allowed);
            Assert.True(DeliveryLanePolicy.Decide(source, target, false,
                IntegrationStatuses.NotApplicable).Allowed);
        }
    }
}
