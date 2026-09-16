using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2817 - direct matrix over the delivery-claim reconciliation policy.
/// Each case is one of the shapes the operator's 2026-09-14 sweep found in the
/// 91 completed cards of the project, plus the repair the policy is allowed to
/// propose for it.
/// </summary>
public class DeliveryClaimSweepPolicyTests
{
    private static DeliveryClaimCommitFact Commit(
        string sha,
        bool contained,
        string supersession = CommitSupersessionStates.Current,
        bool carriesFiles = true)
        => new(sha, contained, supersession, carriesFiles);

    private static DeliveryClaimCardFacts Card(
        IReadOnlyList<DeliveryClaimCommitFact> commits,
        string? containment,
        bool hasRecord = true,
        bool integrationRequired = true,
        bool hasNamedDeliverable = false)
        => new(integrationRequired, commits, containment, hasRecord, hasNamedDeliverable);

    [Fact]
    public void ContainedDeliveryWithARecordIsClean()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(
            Card([Commit("79c2dcf8c", contained: true)], IntegrationStatuses.Integrated));

        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, assessment.Class);
        Assert.Empty(assessment.Findings);
        Assert.False(assessment.Repairs.HasWork);
    }

    /// <summary>
    /// AGT-2706: contained in develop and in main, integrated by an operator
    /// card-scoped merge that left no record, and still carrying the
    /// <c>next-attempt</c> placeholder. Both caches contradict containment and
    /// both are repairable.
    /// </summary>
    [Fact]
    public void ContainedDeliveryWithoutARecordAndWithAStalePlaceholderIsRepairable()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [Commit("79c2dcf8c", contained: true, CommitSupersessionStates.ReplacementPending)],
            IntegrationStatuses.Integrated,
            hasRecord: false));

        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, assessment.Class);
        Assert.Contains(DeliveryClaimFindings.MissingIntegrationRecord, assessment.Findings);
        Assert.Contains(DeliveryClaimFindings.StalePendingSupersession, assessment.Findings);
        Assert.True(assessment.Repairs.AppendIntegrationRecord);
        Assert.True(assessment.Repairs.ClearPendingSupersession);
    }

    /// <summary>AGT-2795: the attributed delivery failed review and never landed.</summary>
    [Fact]
    public void UncontainedDeliveryIsReportedAndNeverRepaired()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(
            Card([Commit("8bc82130f", contained: false)], IntegrationStatuses.Pending));

        Assert.Equal(DeliveryClaimClasses.UnintegratedDelivery, assessment.Class);
        Assert.Contains(DeliveryClaimFindings.UnintegratedDelivery, assessment.Findings);
        Assert.False(assessment.Repairs.HasWork);
    }

    /// <summary>AGT-2743: only a superseded commit is recorded; the successor never was.</summary>
    [Fact]
    public void SupersededOnlyDeliveryIsItsOwnClass()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [Commit("de829d6e8", contained: false, CommitSupersessionStates.Replaced)],
            IntegrationStatuses.NoBranch));

        Assert.Equal(DeliveryClaimClasses.SupersededOnlyDelivery, assessment.Class);
        Assert.Contains(DeliveryClaimFindings.SupersededOnlyDelivery, assessment.Findings);
        Assert.False(assessment.Repairs.HasWork);
    }

    /// <summary>
    /// AGT-2744: two replaced deliveries kept as live attributions next to the
    /// accepted one. The card is integrated, but its attribution is untruthful.
    /// </summary>
    [Fact]
    public void ReplacedDeliveriesLeftUnmarkedBesideAContainedOneAreReported()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [
                Commit("aaaaaaa", contained: false),
                Commit("bbbbbbb", contained: false),
                Commit("ccccccc", contained: true),
            ],
            IntegrationStatuses.Partial));

        Assert.Contains(DeliveryClaimFindings.UnmarkedReplacedDelivery, assessment.Findings);
        Assert.False(assessment.Repairs.ClearPendingSupersession);
        Assert.False(assessment.Repairs.AppendIntegrationRecord);
    }

    /// <summary>
    /// Zero-file runner lifecycle markers are not delivery expectations, so a
    /// card whose only uncontained commit is a marker is not called
    /// unintegrated.
    /// </summary>
    [Fact]
    public void LifecycleMarkersDoNotMakeACardLookUnintegrated()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [
                Commit("ddddddd", contained: true),
                Commit("eeeeeee", contained: false, carriesFiles: false),
            ],
            IntegrationStatuses.Integrated));

        Assert.Equal(DeliveryClaimClasses.IntegratedDelivery, assessment.Class);
        Assert.DoesNotContain(DeliveryClaimFindings.UnintegratedDelivery, assessment.Findings);
        Assert.DoesNotContain(DeliveryClaimFindings.UnmarkedReplacedDelivery, assessment.Findings);
    }

    [Fact]
    public void ConceptCardWithoutCommitsAndWithADossierIsClean()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [],
            IntegrationStatuses.NoBranch,
            integrationRequired: false,
            hasNamedDeliverable: true));

        Assert.Equal(DeliveryClaimClasses.DeliverableWithoutCode, assessment.Class);
        Assert.Empty(assessment.Findings);
    }

    [Fact]
    public void CardWithNeitherDeliveryNorDeliverableClaimsNothing()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [],
            IntegrationStatuses.NoBranch,
            integrationRequired: false));

        Assert.Equal(DeliveryClaimClasses.NothingClaimed, assessment.Class);
        Assert.Contains(DeliveryClaimFindings.NothingClaimed, assessment.Findings);
    }

    /// <summary>
    /// A contained commit that already has a named successor is history, not
    /// evidence that the card's current delivery landed - so it does not
    /// silently earn the card an integration record.
    /// </summary>
    [Fact]
    public void AContainedButReplacedCommitIsNotEvidenceOfTheCurrentDelivery()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [
                Commit("0000001", contained: true, CommitSupersessionStates.Replaced),
                Commit("0000002", contained: false),
            ],
            IntegrationStatuses.Pending,
            hasRecord: false));

        Assert.Equal(DeliveryClaimClasses.UnintegratedDelivery, assessment.Class);
        Assert.DoesNotContain(DeliveryClaimFindings.MissingIntegrationRecord, assessment.Findings);
        Assert.False(assessment.Repairs.HasWork);
    }

    /// <summary>An unanswerable containment question is its own class, not a failure.</summary>
    [Fact]
    public void UnknownContainmentIsNotReportedAsUnintegrated()
    {
        var assessment = DeliveryClaimSweepPolicy.Assess(Card(
            [Commit("fffffff", contained: false)],
            CompletionContractPolicy.ContainmentUnknown,
            hasRecord: false));

        Assert.Equal(DeliveryClaimClasses.Unknown, assessment.Class);
        Assert.DoesNotContain(DeliveryClaimFindings.UnintegratedDelivery, assessment.Findings);
        Assert.False(assessment.Repairs.HasWork);
    }
}
