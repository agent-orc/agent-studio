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
}
