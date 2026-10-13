using AgentStudio.Pipeline;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3076 - the acceptance lineage matrix. Only a run attempt the authority
/// store no longer holds can be overridden; every other mismatch means a
/// different delivery is current and stays refused.
/// </summary>
public sealed class AcceptanceAttemptLineagePolicyTests
{
    [Theory]
    [InlineData(ReviewSubjectAttemptCheck.Current, false, AcceptanceLineageAdmission.Proceed)]
    [InlineData(ReviewSubjectAttemptCheck.Current, true, AcceptanceLineageAdmission.Proceed)]
    [InlineData(ReviewSubjectAttemptCheck.NoCurrentRunAttempt, false, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.NoCurrentRunAttempt, true, AcceptanceLineageAdmission.AcceptOnDeliveryEvidence)]
    [InlineData(ReviewSubjectAttemptCheck.NoTaskKey, false, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.NoTaskKey, true, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.WrongTask, true, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.NoRunAttemptId, true, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.Stale, false, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.Stale, true, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.NotSettled, true, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.ResultShaMismatch, true, AcceptanceLineageAdmission.Refuse)]
    [InlineData(ReviewSubjectAttemptCheck.ResultRefMismatch, true, AcceptanceLineageAdmission.Refuse)]
    public void Decide_CoversEveryLineageCheckWithAndWithoutOverride(
        ReviewSubjectAttemptCheck check, bool operatorOverride, AcceptanceLineageAdmission expected)
    {
        var decision = AcceptanceAttemptLineagePolicy.Decide(
            check, operatorOverride, "AGT-3042", "run-1", 2, "lineage detail");

        Assert.Equal(expected, decision.Admission);
        Assert.Equal(check, decision.Check);
        Assert.Equal(expected == AcceptanceLineageAdmission.Proceed, decision.Message is null);
    }

    [Fact]
    public void Decide_MissingRunAttemptWithoutOverride_NamesTheEpochAndTheWayOut()
    {
        var decision = AcceptanceAttemptLineagePolicy.Decide(
            ReviewSubjectAttemptCheck.NoCurrentRunAttempt, false, "AGT-3042", "run-1", 2, null);

        Assert.StartsWith(AcceptanceAttemptLineagePolicy.MissingRunAttemptRefusal, decision.Message);
        Assert.Contains("(2)", decision.Message);
        Assert.Contains("'AGT-3042'", decision.Message);
        Assert.Contains("'run-1'", decision.Message);
        Assert.Contains("operator override", decision.Message);
    }

    [Fact]
    public void Decide_MissingRunAttemptWithOverride_SaysNoRunAttemptWasAvailable()
    {
        var decision = AcceptanceAttemptLineagePolicy.Decide(
            ReviewSubjectAttemptCheck.NoCurrentRunAttempt, true, "AGT-3042", null, 2, null);

        Assert.Contains("No run attempt was available for 'AGT-3042' in authority epoch 2", decision.Message);
        Assert.Contains("'unknown'", decision.Message);
    }

    [Fact]
    public void Decide_OtherMismatch_KeepsTheValidationErrorAsTheReason()
    {
        var decision = AcceptanceAttemptLineagePolicy.Decide(
            ReviewSubjectAttemptCheck.Stale, true, "AGT-3042", "old", 2,
            "Review subject RunAttempt 'old' is stale; current RunAttempt is 'new'.");

        Assert.Equal(
            "Acceptance refused: Review subject RunAttempt 'old' is stale; current RunAttempt is 'new'.",
            decision.Message);
    }

    // AGT-3098: fixed instants; the policy reads the ledger order, never the clock.
    private static readonly DateTime LedgerAt = new(2026, 10, 13, 1, 6, 13, DateTimeKind.Utc);

    [Theory]
    [InlineData(TaskStates.Completed, "run-1", IntegrationStatuses.Integrated, true)]
    [InlineData(TaskStates.Archive, "run-1", IntegrationStatuses.Integrated, true)]
    [InlineData(TaskStates.Completed, "RUN-1", IntegrationStatuses.Integrated, true)]
    [InlineData(TaskStates.HumanReview, "run-1", IntegrationStatuses.Integrated, false)]
    [InlineData(TaskStates.Completed, "run-2", IntegrationStatuses.Integrated, false)]
    [InlineData(TaskStates.Completed, null, IntegrationStatuses.Integrated, false)]
    [InlineData(TaskStates.Completed, "run-1", IntegrationStatuses.Pending, false)]
    [InlineData(TaskStates.Completed, "run-1", "unknown", false)]
    public void FindAcceptanceWithoutRunAttempt_HoldsOnlyForTheIntegratedCurrentDelivery(
        string state, string? subjectRunAttemptId, string recordedStatus, bool expected)
    {
        var timeline = new[] { Accepted(0), WithoutRunAttempt(1, "run-1", recordedStatus) };

        var found = AcceptanceAttemptLineagePolicy.FindAcceptanceWithoutRunAttempt(
            state, subjectRunAttemptId, timeline);

        Assert.Equal(expected, found is not null);
        if (found is not null) Assert.False(found.OutcomeRecorded);
    }

    [Fact]
    public void FindAcceptanceWithoutRunAttempt_ALaterAcceptanceSupersedesTheRecord()
    {
        var timeline = new[]
        {
            Accepted(0),
            WithoutRunAttempt(1, "run-1", IntegrationStatuses.Integrated),
            LaneChange(2, TaskStates.Completed, TaskStates.HumanReview),
            LaneChange(3, TaskStates.HumanReview, TaskStates.Completed),
        };

        Assert.Null(AcceptanceAttemptLineagePolicy.FindAcceptanceWithoutRunAttempt(
            TaskStates.Completed, "run-1", timeline));
        Assert.NotNull(AcceptanceAttemptLineagePolicy.FindAcceptanceWithoutRunAttempt(
            TaskStates.Archive, "run-1", timeline[..2].Append(
                LaneChange(2, TaskStates.Completed, TaskStates.Archive)).ToArray()));
    }

    [Fact]
    public void FindAcceptanceWithoutRunAttempt_SeesTheOutcomeOnlyWhenRecordedAfterTheAcceptance()
    {
        var recorded = new TimelineEvent
        {
            Ts = LedgerAt.AddMinutes(6),
            Kind = TimelineEventKinds.IntegrationSucceeded,
            Details = new() { ["outcome"] = AcceptanceAttemptLineagePolicy.AcceptedWithoutRunAttemptOutcome },
        };
        var acceptance = new[] { Accepted(0), WithoutRunAttempt(1, "run-1", IntegrationStatuses.Integrated) };

        Assert.True(AcceptanceAttemptLineagePolicy.FindAcceptanceWithoutRunAttempt(
            TaskStates.Completed, "run-1", [.. acceptance, recorded])!.OutcomeRecorded);
        Assert.False(AcceptanceAttemptLineagePolicy.FindAcceptanceWithoutRunAttempt(
            TaskStates.Completed, "run-1", [recorded, .. acceptance])!.OutcomeRecorded);
        Assert.False(AcceptanceAttemptLineagePolicy.FindAcceptanceWithoutRunAttempt(
            TaskStates.Completed, "run-1",
            [.. acceptance, recorded with { Details = new() { ["outcome"] = "Merged" } }])!.OutcomeRecorded);
    }

    private static TimelineEvent Accepted(int second)
        => LaneChange(second, TaskStates.HumanReview, TaskStates.Completed);

    private static TimelineEvent LaneChange(int second, string from, string to) => new()
    {
        Ts = LedgerAt.AddSeconds(second),
        Kind = TimelineEventKinds.LaneChanged,
        Details = new() { ["from"] = from, ["to"] = to },
    };

    private static TimelineEvent WithoutRunAttempt(int second, string runAttemptId, string integrationStatus) => new()
    {
        Ts = LedgerAt.AddSeconds(second),
        Kind = TimelineEventKinds.AcceptedWithoutRunAttempt,
        Details = new()
        {
            ["subjectRunAttemptId"] = runAttemptId,
            ["integrationStatus"] = integrationStatus,
        },
    };
}
