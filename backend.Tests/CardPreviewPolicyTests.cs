using AgentStudio.Shared;
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

    [Fact]
    public void Integration_is_asked_only_when_it_can_change_the_decision()
    {
        // AGT-3793: the sweep skips the completion verdict whenever an
        // integrated and a pending delivery decide alike.
        var now = _clock.GetUtcNow().UtcDateTime;
        var green = new string('a', 40);
        var other = new string('b', 40);
        TaskPreview?[] previews =
        [
            null,
            .. new[] { "queued", "ready", "failed", "removing", "expired", "removed" }.SelectMany(status =>
                new[] { green, other }.Select(sha => new TaskPreview(
                    "https://a.b.preview.internal", status, sha, null, now.AddDays(-1), now.AddDays(6), "job-1"))),
        ];
        foreach (var preview in previews)
        foreach (var delivery in new[] { green, null })
        foreach (var rejected in new[] { false, true })
        {
            if (CardPreviewPolicy.NeedsIntegration(preview, delivery, rejected)) continue;
            Assert.Equal(
                CardPreviewPolicy.Decide(preview, delivery, integrated: false, rejected, now),
                CardPreviewPolicy.Decide(preview, delivery, integrated: true, rejected, now));
        }
        Assert.False(CardPreviewPolicy.NeedsIntegration(null, green, rejected: true));
        Assert.False(CardPreviewPolicy.NeedsIntegration(null, null, rejected: false));
        Assert.True(CardPreviewPolicy.NeedsIntegration(null, green, rejected: false));
    }
}
