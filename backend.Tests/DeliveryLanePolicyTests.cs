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

    [Fact]
    public void Reconciliation_ReopensStaleAcceptedDeliveryAndAdvancesIntegrationPhase()
    {
        var folder = Path.Combine(Path.GetTempPath(), "delivery-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var resultSha = new string('a', 40);
            AgentStudio.Pipeline.ReviewSubjectStore.Write(folder,
                new AgentStudio.Pipeline.ReviewSubjectRecord
                {
                    TaskKey = "REC-1", RunAttemptId = "epoch-1", AttemptChainId = "chain-1",
                    ResultSha = resultSha,
                });
            var accepted = new TaskInfo
            {
                State = TaskStates.Completed, FolderPath = folder,
                CompletionClaim = new TaskCompletionClaim
                {
                    Basis = CompletionClaimBases.IntegratedDelivery,
                    DeliveryEpoch = "epoch-1", ResultSha = resultSha,
                    IntegrationBranch = "develop", TargetRefFingerprint = "ref-1",
                },
            };
            var integrated = new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.Integrated,
                IntegrationBranch = "develop", TargetRefFingerprint = "ref-1",
            };
            Assert.Null(DeliveryLanePolicy.ReconciliationTarget(accepted, integrated));
            Assert.Equal(TaskStates.AutoReview, DeliveryLanePolicy.ReconciliationTarget(
                accepted, integrated with { TargetRefFingerprint = "ref-2" }));
            Assert.Equal(TaskStates.AutoReview, DeliveryLanePolicy.ReconciliationTarget(
                accepted with { CompletionClaim = accepted.CompletionClaim with { DeliveryEpoch = "epoch-0" } }, integrated));
            Assert.Equal(TaskStates.AutoReview, DeliveryLanePolicy.ReconciliationTarget(
                accepted with { CompletionClaim = accepted.CompletionClaim with { ResultSha = new string('b', 40) } }, integrated));

            var integrating = accepted with { State = TaskStates.AutoReview, Phase = LifecyclePhases.Integrating };
            Assert.Equal(TaskStates.HumanReview, DeliveryLanePolicy.ReconciliationTarget(integrating, integrated));
            Assert.Equal(TaskStates.Escalated, DeliveryLanePolicy.ReconciliationTarget(integrating,
                integrated with { Status = IntegrationStatuses.ConflictSkipped }));
            Assert.Null(DeliveryLanePolicy.ReconciliationTarget(integrating,
                integrated with { Status = IntegrationStatuses.Pending }));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
