using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3756 - published gate evidence is keyed by published SHA and gate step,
/// so every publication of one card keeps its own entry and acceptance of a
/// later card counts each of them.
/// </summary>
public sealed partial class AcceptanceIntegrationRoundTripTests
{
    [Fact]
    public void Acceptance_CardWithThreePublications_KeepsAndCountsEachEntry()
    {
        var deliverySha = PublishDelivery("three-publications-delivery.txt", "verified delivery\n");
        RunGit(_repo, "push", "-q", "origin", $"{deliverySha}:refs/heads/develop");
        RunGit(_repo, "fetch", "-q", "origin", "develop");
        var deps = Build(deliverySha);
        var card = deps.Scanner.FindJob(Slug, _watchPath)!;
        var first = PublishLaterCommit("three-publications-1.txt");
        var second = PublishLaterCommit("three-publications-2.txt");
        var third = PublishLaterCommit("three-publications-3.txt");
        // The AGT-3243 shape: the third push recorded the second tree under its own published SHA.
        Assert.True(PublishedGateEvidenceStore.Append(_watchPath, PassedPublishedGate(first, "AGT-THREE")));
        Assert.True(PublishedGateEvidenceStore.Append(_watchPath,
            PassedPublishedGate(second, "AGT-THREE") with { PublishedSha = third }));
        Assert.True(PublishedGateEvidenceStore.Append(_watchPath, PassedPublishedGate(third, "AGT-THREE")));
        var service = Service(deps, new BatchGateStore(Path.Combine(_tempDir, "three-publications-gates")), deliverySha);
        Assert.Contains($"names published SHA {third}, not this tree",
            service.BuildForAcceptance(card).Verification?.Reason);

        Assert.True(PublishedGateEvidenceStore.Append(_watchPath, PassedPublishedGate(second, "AGT-THREE")));
        Assert.False(PublishedGateEvidenceStore.Append(_watchPath, PassedPublishedGate(third, "AGT-THREE")));

        var exact = PublishedGateEvidenceStore.Read(_watchPath)
            .Where(entry => entry.SourceJobId == "AGT-THREE" && entry.TreeSha == entry.PublishedSha)
            .Select(entry => entry.PublishedSha);
        Assert.Equal(new[] { first, second, third }.Order(StringComparer.Ordinal), exact.Order(StringComparer.Ordinal));
        var verification = service.BuildForAcceptance(card).Verification;
        Assert.Equal(IntegrationVerificationStates.Verified, verification?.State);
        Assert.Null(verification?.UncoveredPublication);
    }

    [Fact]
    public void Refusal_UnverifiedDelivery_NamesTheCheckAndWhatItFound()
    {
        var text = TaskTransitionService.DescribeVerificationRefusal(new TaskIntegrationVerification
        {
            State = IntegrationVerificationStates.Unverified,
            Sha = "de9f7a8bf0b69f6934543a1318c9c6bb86012e98",
            Evidence = IntegrationVerificationEvidence.None,
            Reason = "The delivery is contained in de9f7a8bf0b69f6934543a1318c9c6bb86012e98, but no gate evidence names that tree.",
        });

        Assert.Contains($"expected {IntegrationVerificationStates.Verified} gate evidence", text);
        Assert.Contains($"found state {IntegrationVerificationStates.Unverified}, evidence {IntegrationVerificationEvidence.None} "
            + "on de9f7a8bf0b69f6934543a1318c9c6bb86012e98", text);
        Assert.Contains("no gate evidence names that tree", text);
        Assert.DoesNotContain("commands.build", text);
        Assert.Contains("commands.build", TaskTransitionService.DescribeVerificationRefusal(null));
    }
}
