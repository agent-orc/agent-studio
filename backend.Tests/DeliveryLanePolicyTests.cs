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
    public void Operator_decision_park_admits_pending_integration_only_into_human_review()
    {
        Assert.True(DeliveryLanePolicy.Decide(TaskStates.AutoReview, TaskStates.HumanReview,
            true, IntegrationStatuses.Pending, operatorDecisionPark: true).Allowed);
        Assert.False(DeliveryLanePolicy.Decide(TaskStates.AutoReview, TaskStates.Completed,
            true, IntegrationStatuses.Pending, operatorDecisionPark: true).Allowed);
        Assert.False(DeliveryLanePolicy.Decide(TaskStates.HumanReview, TaskStates.Completed,
            true, IntegrationStatuses.Pending, operatorDecisionPark: true).Allowed);
        Assert.False(DeliveryLanePolicy.Decide(TaskStates.AutoReview, TaskStates.HumanReview,
            true, IntegrationStatuses.Pending).Allowed);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("old-ref")]
    [InlineData("current-ref")]
    public void Reconciliation_TargetHeadChangeDoesNotStaleContainedDelivery(string? fingerprint)
    {
        var sha = new string('a', 40);
        var card = new TaskInfo
        {
            State = TaskStates.Completed,
            FolderPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Commits = [new TaskCommitInfo { Sha = sha, RunAttemptId = "epoch-1" }],
            CompletionClaim = new TaskCompletionClaim
            {
                Basis = CompletionClaimBases.IntegratedDelivery,
                DeliveryEpoch = "epoch-1", ResultSha = sha,
                IntegrationBranch = "develop", TargetRefFingerprint = fingerprint,
            },
        };
        var status = new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Integrated, IntegrationBranch = "develop",
            TargetRefFingerprint = "current-ref",
        };

        Assert.Null(OperatorDecisionHarness.ReconciliationTarget(card, status));
    }

    [Fact]
    public void Reconciliation_LegacyClaimRemainsAcceptedWhenDeliveryIsIntegrated()
    {
        var card = new TaskInfo
        {
            State = TaskStates.Completed,
            CompletionClaim = new TaskCompletionClaim
            {
                Basis = CompletionClaimBases.IntegratedDelivery,
                IntegrationBranch = "develop",
            },
        };
        var integrated = new TaskIntegrationStatus { Status = IntegrationStatuses.Integrated };

        Assert.Null(OperatorDecisionHarness.ReconciliationTarget(card, integrated));
        Assert.Equal(TaskStates.AutoReview, OperatorDecisionHarness.ReconciliationTarget(
            card, integrated with { Status = IntegrationStatuses.Pending }));
    }

    [Fact]
    public void Reconciliation_OperatorOverrideRemainsAccepted()
    {
        var card = new TaskInfo
        {
            State = TaskStates.Completed,
            Commits = [new TaskCommitInfo { Sha = new string('a', 40), RunAttemptId = "accepted-epoch" }],
            CompletionClaim = new TaskCompletionClaim
            {
                Basis = CompletionClaimBases.OperatorOverride,
                Reason = "Accepted by the operator after review.",
                ResultSha = new string('a', 40),
                DeliveryEpoch = "accepted-epoch",
            },
        };

        Assert.Null(OperatorDecisionHarness.ReconciliationTarget(card,
            new TaskIntegrationStatus { Status = IntegrationStatuses.Integrated }));
        Assert.Null(OperatorDecisionHarness.ReconciliationTarget(card,
            new TaskIntegrationStatus { Status = IntegrationStatuses.Pending }));
        Assert.Equal(TaskStates.AutoReview, OperatorDecisionHarness.ReconciliationTarget(
            card with { Commits = [new TaskCommitInfo
                { Sha = new string('b', 40), RunAttemptId = "new-epoch" }] },
            new TaskIntegrationStatus { Status = IntegrationStatuses.Integrated }));
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
            Assert.Null(OperatorDecisionHarness.ReconciliationTarget(accepted, integrated));
            Assert.Null(OperatorDecisionHarness.ReconciliationTarget(
                accepted, integrated with { TargetRefFingerprint = "ref-2" }));
            Assert.Equal(TaskStates.AutoReview, OperatorDecisionHarness.ReconciliationTarget(
                accepted with { CompletionClaim = accepted.CompletionClaim with { DeliveryEpoch = "epoch-0" } }, integrated));
            Assert.Equal(TaskStates.AutoReview, OperatorDecisionHarness.ReconciliationTarget(
                accepted with { CompletionClaim = accepted.CompletionClaim with { ResultSha = new string('b', 40) } }, integrated));

            var integrating = accepted with { State = TaskStates.AutoReview, Phase = LifecyclePhases.Integrating };
            Assert.Equal(TaskStates.HumanReview, OperatorDecisionHarness.ReconciliationTarget(integrating, integrated));
            Assert.Equal(TaskStates.Escalated, OperatorDecisionHarness.ReconciliationTarget(integrating,
                integrated with { Status = IntegrationStatuses.ConflictSkipped }));
            Assert.Null(OperatorDecisionHarness.ReconciliationTarget(integrating,
                integrated with { Status = IntegrationStatuses.Pending }));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // AGT-3586: an acceptance park states the fact once, as a sentence. The
    // category travels as the transition detail; the next step is offered by
    // the lane buttons, not by an instruction to "rerun the gate".
    [Fact]
    public void AcceptanceParkReason_IsOneSentenceWithoutSlugOrInstruction()
    {
        var missing = DeliveryChainReconciler.IntegrationParkReason("missing-delivery", null);
        Assert.Equal("No delivery branch was found for this card, so its work cannot be integrated.", missing);

        var failed = DeliveryChainReconciler.IntegrationParkReason("integration-failed", new TaskIntegrationStatus
        {
            Failure = new TaskIntegrationFailure { Reason = "Merge conflict in src/app.ts." },
        });
        Assert.Equal("The delivery could not be integrated: Merge conflict in src/app.ts.", failed);
        Assert.Equal("The delivery could not be integrated.",
            DeliveryChainReconciler.IntegrationParkReason("integration-failed", null));
        Assert.DoesNotContain("rerun", missing + failed);
    }

    // AGT-3586 review: the recorded failure detail is never discarded, also
    // on a missing-delivery park, and the sentence always ends.
    [Fact]
    public void AcceptanceParkReason_KeepsTheRecordedFailureDetail()
    {
        var status = new TaskIntegrationStatus
        {
            Failure = new TaskIntegrationFailure { Reason = "Branch agent/AGT-1 was deleted on origin" },
        };
        Assert.Equal(
            "No delivery branch was found for this card, so its work cannot be integrated: "
            + "Branch agent/AGT-1 was deleted on origin.",
            DeliveryChainReconciler.IntegrationParkReason("missing-delivery", status));
        Assert.Equal("The delivery could not be integrated: Branch agent/AGT-1 was deleted on origin.",
            DeliveryChainReconciler.IntegrationParkReason("merge-conflict", status));
    }

    // AGT-3586 review: a nonfatal (stale verdict) park states its fact too,
    // not the old "Integrate the current delivery and repeat review." order.
    [Fact]
    public void StaleVerdictParkReason_IsAFactNotTheOldInstruction()
    {
        var stale = DeliveryChainReconciler.IntegrationParkReason("delivery-verdict-stale", new TaskIntegrationStatus
        {
            Failure = new TaskIntegrationFailure { Reason = "Merge conflict in an older delivery." },
        });
        Assert.Equal("The review verdict no longer matches the current delivery, so it cannot be accepted.", stale);
        Assert.DoesNotContain("repeat review", stale);
        Assert.DoesNotContain("delivery-verdict-stale", stale);
    }
}
