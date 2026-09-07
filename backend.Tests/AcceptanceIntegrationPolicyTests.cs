using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptanceIntegrationPolicyTests
{
    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Merged, false, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.MergedAfterRebase, false, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.AlreadyMerged, false, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.AlreadyOnIntegrationBranch, false, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.NoTaskBranch, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.Error, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.AgentRoundRequired, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.NoTaskBranch, true, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.NoTaskBranch, false, false, AcceptedIntegrationLaneDecision.Complete)]
    public void WorkerOutcomeMatrix_DecidesAcceptedLane(
        MergeIntoIntegrationOutcome outcome,
        bool operatorOverride,
        bool integrationRequired,
        AcceptedIntegrationLaneDecision expected)
    {
        Assert.Equal(
            expected,
            AcceptanceIntegrationPolicy.Decide(outcome, operatorOverride, integrationRequired));
    }

    [Theory]
    [InlineData("concept")]
    [InlineData("decision")]
    public void ConceptAndDecisionTaskTypes_ExpectNoIntegration(string taskType)
    {
        Assert.False(AcceptanceIntegrationPolicy.IsIntegrationRequired(new TaskInfo
        {
            Mode = TaskModes.Coding,
            Kind = TaskKinds.Task,
            TaskType = taskType,
        }));
    }

    [Theory]
    [InlineData("concept")]
    [InlineData("decision")]
    public void PersistedNoBranchTaskTypes_ArePreservedAsAnExpectation(string taskType)
    {
        using var document = JsonDocument.Parse($$"""{"taskType":"{{taskType}}"}""");

        Assert.True(TaskScannerService.ReadNoBranchExpected(document.RootElement));
    }

    [Fact]
    public void ExplicitNoBranchExpectation_ExemptsCodingCard()
    {
        Assert.False(AcceptanceIntegrationPolicy.IsIntegrationRequired(new TaskInfo
        {
            Mode = TaskModes.Coding,
            Kind = TaskKinds.Task,
            TaskType = TaskTypes.Chore,
            NoBranchExpected = true,
        }));
    }

    [Theory]
    [InlineData(null, null, false, "PreInvariantNotEvaluated")]
    [InlineData(null, IntegrationStatuses.Pending, false, "PreInvariantNotEvaluated")]
    [InlineData(null, IntegrationStatuses.Integrated, false, null)]
    [InlineData(null, null, true, "Null")]
    [InlineData(null, IntegrationStatuses.Pending, true, "Null")]
    [InlineData("error", IntegrationStatuses.Pending, true, "Error")]
    [InlineData("no-branch", IntegrationStatuses.NoBranch, true, "NoTaskBranch")]
    [InlineData("operator-override", IntegrationStatuses.Pending, true, null)]
    public void AcceptedInventory_ClassifiesSilentHistoricalOutcomes(
        string? verdict,
        string? integrationStatus,
        bool hasIntegrationRecord,
        string? expected)
    {
        var step = verdict is null ? null : new PipelineStepExecution { Verdict = verdict };
        var status = integrationStatus is null
            ? null
            : new TaskIntegrationStatus { Status = integrationStatus };

        Assert.Equal(
            expected,
            AcceptedIntegrationInventorySweep.ClassifyFinding(step, status, hasIntegrationRecord));
    }

    [Theory]
    [InlineData(IntegrationRecordClasses.IntegratedVerified, null)]
    [InlineData(IntegrationRecordClasses.IntegratedHistorical, null)]
    [InlineData(IntegrationRecordClasses.NoCodeExpected, null)]
    [InlineData(IntegrationRecordClasses.NoAttributionLegacy, null)]
    [InlineData(IntegrationRecordClasses.ContentOnFence, IntegrationRecordClasses.ContentOnFence)]
    [InlineData(IntegrationRecordClasses.GenuinelyMissing, IntegrationRecordClasses.GenuinelyMissing)]
    public void AcceptedInventory_OnlyKeepsActionableHistoricalClasses(
        string verificationClass,
        string? expected)
    {
        Assert.Equal(
            expected,
            AcceptedIntegrationInventorySweep.ClassifyFinding(
                null,
                new TaskIntegrationStatus { Status = IntegrationStatuses.Pending },
                hasIntegrationRecord: true,
                verificationClass));
    }
}
