using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2817 - direct matrix over the completion contract. A card may claim
/// completion on exactly three grounds, and the contract refuses every other
/// shape with a typed, answerable reason.
///
/// <para>
/// The two cards that motivated the contract are pinned by name:
/// AGT-2795 sat in the delivered lane with a superseded-only delivery that was
/// never integrated, and AGT-2706 was read as unintegrated purely because its
/// integration record was missing while its delivery was contained.
/// </para>
/// </summary>
public class CompletionContractPolicyTests
{
    private const string Reason = "Delivery abandoned after review; closing the card.";

    private static CompletionContractFacts Coding(
        string? containment,
        bool hasCommits = true,
        bool hasEffective = true,
        bool operatorOverride = false,
        string? reason = null)
        => new(
            IntegrationRequired: true,
            HasAttributedCommits: hasCommits,
            HasEffectiveCommits: hasEffective,
            ContainmentStatus: containment,
            ContainmentCommitSha: containment == IntegrationStatuses.Integrated ? "79c2dcf8c" : null,
            IntegrationBranch: "develop",
            OperatorOverride: operatorOverride,
            OverrideReason: reason,
            Actor: "human:operator");

    [Fact]
    public void ContainedDeliveryCompletesOnItsOwnGround()
    {
        var decision = CompletionContractPolicy.Decide(Coding(IntegrationStatuses.Integrated));

        Assert.True(decision.Accepted);
        Assert.Equal(CompletionClaimBases.IntegratedDelivery, decision.Claim!.Basis);
        Assert.Equal("79c2dcf8c", decision.Claim.CommitSha);
        Assert.Contains("develop", decision.Claim.Evidence);
    }

    /// <summary>
    /// AGT-2706: the record was absent, not negative. An absent record must
    /// never decide the answer, and containment alone completes the card - no
    /// override is needed and none is recorded.
    /// </summary>
    [Fact]
    public void ContainmentOutranksAnOverrideTheOperatorDidNotNeed()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.Integrated, operatorOverride: true, reason: Reason));

        Assert.True(decision.Accepted);
        Assert.Equal(CompletionClaimBases.IntegratedDelivery, decision.Claim!.Basis);
        Assert.Null(decision.Claim.Reason);
    }

    [Theory]
    [InlineData(IntegrationStatuses.Pending)]
    [InlineData(IntegrationStatuses.Partial)]
    [InlineData(IntegrationStatuses.ConflictSkipped)]
    public void UncontainedCodingDeliveryIsRefused(string containment)
    {
        var decision = CompletionContractPolicy.Decide(Coding(containment));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.UnintegratedDelivery, decision.RefusalCode);
        Assert.Contains("develop", decision.Message!);
    }

    /// <summary>An unanswered containment question is never worded as "not integrated".</summary>
    [Fact]
    public void UnknownContainmentRefusesWithoutClaimingTheDeliveryIsMissing()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(CompletionContractPolicy.ContainmentUnknown));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.UnintegratedDelivery, decision.RefusalCode);
        Assert.Contains("could not be determined", decision.Message!);
        Assert.DoesNotContain("not contained", decision.Message!);
    }

    /// <summary>AGT-2795 and AGT-2743: the only attributed delivery is superseded.</summary>
    [Fact]
    public void SupersededOnlyDeliveryIsACompletionContradiction()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.Pending, hasEffective: false));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.SupersededOnlyDelivery, decision.RefusalCode);
    }

    [Fact]
    public void SupersededOnlyDeliveryStillCompletesWithAWrittenReason()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.Pending, hasEffective: false, operatorOverride: true, reason: Reason));

        Assert.True(decision.Accepted);
        Assert.Equal(CompletionClaimBases.OperatorOverride, decision.Claim!.Basis);
        Assert.Equal(Reason, decision.Claim.Reason);
    }

    [Fact]
    public void OverrideWithoutAWrittenReasonIsRefused()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.Pending, operatorOverride: true, reason: "   "));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.OverrideWithoutReason, decision.RefusalCode);
    }

    [Fact]
    public void OverrideReasonMustBeLongerThanAShrug()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.Pending, operatorOverride: true, reason: "ok"));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.OverrideWithoutReason, decision.RefusalCode);
    }

    [Fact]
    public void CodeFreeCardCompletesWhenItNamesItsDeliverable()
    {
        var decision = CompletionContractPolicy.Decide(new CompletionContractFacts(
            IntegrationRequired: false,
            HasAttributedCommits: false,
            HasEffectiveCommits: false,
            ContainmentStatus: IntegrationStatuses.NoBranch,
            DeliverablePath: "docs/operations/telemetry-layer/index.html",
            DeliverableKey: "DOC-42"));

        Assert.True(decision.Accepted);
        Assert.Equal(CompletionClaimBases.DeliverableWithoutCode, decision.Claim!.Basis);
        Assert.Equal("docs/operations/telemetry-layer/index.html", decision.Claim.DeliverablePath);
        Assert.Equal("DOC-42", decision.Claim.DeliverableKey);
        Assert.Contains("DOC-42", decision.Claim.Evidence);
    }

    [Fact]
    public void CodeFreeCardWithoutANamedDeliverableIsRefused()
    {
        var decision = CompletionContractPolicy.Decide(new CompletionContractFacts(
            IntegrationRequired: false,
            HasAttributedCommits: false,
            HasEffectiveCommits: false,
            ContainmentStatus: IntegrationStatuses.NoBranch));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.UndeclaredDeliverable, decision.RefusalCode);
    }

    /// <summary>
    /// A coding card that produced nothing is not a code-free deliverable. It
    /// is told so in its own words rather than through the integration wording.
    /// </summary>
    [Fact]
    public void CodingCardWithNothingToIntegrateIsRefusedWithItsOwnWording()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.NoBranch, hasCommits: false, hasEffective: false));

        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRefusalCodes.UnintegratedDelivery, decision.RefusalCode);
        Assert.Contains("no delivery to integrate", decision.Message!);
    }

    [Theory]
    [InlineData(IntegrationStatuses.Integrated, true)]
    [InlineData(IntegrationStatuses.MergedLocally, true)]
    [InlineData(IntegrationStatuses.Partial, false)]
    [InlineData(IntegrationStatuses.Pending, false)]
    [InlineData(IntegrationStatuses.NoBranch, false)]
    [InlineData(null, false)]
    public void ContainmentIsOnlyPositiveForAProvenAncestor(string? status, bool contained)
        => Assert.Equal(contained, CompletionContractPolicy.IsContained(status));

    /// <summary>
    /// AGT-2849: an unpublished merge is still a merge. The delivery is in the
    /// integration branch graph, so the completion contract accepts it on the
    /// integrated-delivery basis instead of accusing it of being unintegrated.
    /// The outstanding origin push is the push backstop's work and the
    /// merged-locally badge is where the card reports it.
    /// </summary>
    [Fact]
    public void AMergedButUnpublishedDeliveryStillSatisfiesTheContract()
    {
        var decision = CompletionContractPolicy.Decide(
            Coding(IntegrationStatuses.MergedLocally));

        Assert.True(decision.Accepted);
        Assert.Equal(CompletionClaimBases.IntegratedDelivery, decision.Claim!.Basis);
        Assert.Null(decision.RefusalCode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(CompletionContractPolicy.ContainmentUnknown, true)]
    [InlineData(IntegrationStatuses.Pending, false)]
    public void AMissingVerdictIsUnknownRatherThanPending(string? status, bool unknown)
        => Assert.Equal(unknown, CompletionContractPolicy.IsUnknown(status));
}
