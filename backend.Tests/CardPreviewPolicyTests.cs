using AgentStudio.Deploy;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CardPreviewPolicyTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AddressIsStablePerCardAndProjectAndRejectsInvalidDomain()
    {
        Assert.Equal("https://agt-3180.web-project.preview.internal",
            CardPreviewPolicy.Address("AGT-3180", "Web Project", "preview.internal"));
        Assert.NotEqual(CardPreviewPolicy.Address("AGT-3180", "web-project", "preview.internal"),
            CardPreviewPolicy.Address("AGT-3181", "web-project", "preview.internal"));
        Assert.Throws<ArgumentException>(() => CardPreviewPolicy.Address("AGT-3180", "web-project", "bad/domain"));
    }

    [Fact]
    public void GreenBuildMustMatchTheCurrentImmutableDelivery()
    {
        var first = new string('a', 40);
        var second = new string('b', 40);
        var subject = new ReviewSubjectRecord { ResultSha = first };
        Assert.True(CardPreviewPolicy.IsGreenDelivery(subject,
            new ReviewVerificationRecord { ResultSha = first, BuildTestGate = ReviewBuildTestGateClasses.Passed }));
        Assert.False(CardPreviewPolicy.IsGreenDelivery(subject,
            new ReviewVerificationRecord { ResultSha = second, BuildTestGate = ReviewBuildTestGateClasses.Passed }));
        Assert.False(CardPreviewPolicy.IsGreenDelivery(subject,
            new ReviewVerificationRecord { ResultSha = first, BuildTestGate = ReviewBuildTestGateClasses.Failed }));
    }

    [Fact]
    public void IntegrationRejectionAndSevenIdleDaysRemoveThePreview()
    {
        var changed = _clock.GetUtcNow().UtcDateTime;
        var first = new string('a', 40);
        var second = new string('b', 40);
        TaskPreview? current = null;
        Assert.Equal(PreviewLifecycleAction.Create,
            CardPreviewPolicy.Decide(current, first, false, false, changed));
        current = new("https://agt-3180.demo.preview.internal", "ready", first, null,
            changed, changed + CardPreviewPolicy.Lifetime, "job-1");
        Assert.Equal(PreviewLifecycleAction.Sync,
            CardPreviewPolicy.Decide(current, first, false, false, changed));
        Assert.Equal(PreviewLifecycleAction.Update,
            CardPreviewPolicy.Decide(current, second, false, false, changed));
        Assert.Equal(PreviewLifecycleAction.Remove,
            CardPreviewPolicy.Decide(current, first, true, false, changed));
        Assert.Equal(PreviewLifecycleAction.Remove,
            CardPreviewPolicy.Decide(current, first, false, true, changed));
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(PreviewLifecycleAction.Remove,
            CardPreviewPolicy.Decide(current, first, false, false, _clock.GetUtcNow().UtcDateTime));
        Assert.Equal(PreviewLifecycleAction.Update,
            CardPreviewPolicy.Decide(current, second, false, false, _clock.GetUtcNow().UtcDateTime));
        current = current with { Status = "expired" };
        Assert.Equal(PreviewLifecycleAction.None,
            CardPreviewPolicy.Decide(current, first, false, false, _clock.GetUtcNow().UtcDateTime));
        Assert.Equal(PreviewLifecycleAction.Update,
            CardPreviewPolicy.Decide(current, second, false, false, _clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void PreviewSecretsMayNotReuseStageOrProductionNames()
    {
        Assert.True(CardPreviewPolicy.SecretsIsolated(["test-data"], ["prod-db", "stage-db"]));
        Assert.False(CardPreviewPolicy.SecretsIsolated(["STAGE-DB"], ["prod-db", "stage-db"]));
    }
}
