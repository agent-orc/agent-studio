using AgentStudio.Projects.Previews;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PreviewEnvironmentPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static PreviewFacts Facts(string sha = "first", DateTimeOffset? changed = null) =>
        new("sample", "AGT-3180", sha, "sha256:digest", true, true, true,
            false, false, true, changed ?? Now);

    [Fact]
    public void FirstGreenDeliveryCreatesAnAddressForTheCard()
    {
        var decision = PreviewEnvironmentPolicy.Decide(null, Facts(), Now);

        Assert.Equal(PreviewOperation.Create, decision.Operation);
        Assert.Equal("https://agt-3180.sample.preview.internal/", decision.Desired!.Address.ToString());
        Assert.Equal("first", decision.Desired.DeliverySha);
    }

    [Fact]
    public void NewDeliveryUpdatesTheExistingCardAddress()
    {
        var current = PreviewEnvironmentPolicy.Decide(null, Facts(), Now).Desired!;
        var decision = PreviewEnvironmentPolicy.Decide(current, Facts("second"), Now);

        Assert.Equal(PreviewOperation.Update, decision.Operation);
        Assert.Equal(current.Address, decision.Desired!.Address);
        Assert.Equal("second", decision.Desired.DeliverySha);
        Assert.Equal(PreviewOperation.None,
            PreviewEnvironmentPolicy.Decide(decision.Desired, Facts("second"), Now).Operation);
    }

    [Theory]
    [InlineData(true, false, "integrated")]
    [InlineData(false, true, "rejected")]
    public void TerminalCardRemovesItsPreview(bool integrated, bool rejected, string reason)
    {
        var current = PreviewEnvironmentPolicy.Decide(null, Facts(), Now).Desired!;
        var facts = Facts() with { Integrated = integrated, Rejected = rejected };

        var decision = PreviewEnvironmentPolicy.Decide(current, facts, Now);

        Assert.Equal(PreviewOperation.Remove, decision.Operation);
        Assert.Equal(reason, decision.Reason);
        Assert.Null(decision.Desired);
    }

    [Fact]
    public void SevenDaysWithoutChangeRemovesPreview()
    {
        var changed = Now.AddDays(-7);
        var current = PreviewEnvironmentPolicy.Decide(null, Facts(changed: changed), changed).Desired!;

        Assert.Equal(PreviewOperation.Remove,
            PreviewEnvironmentPolicy.Decide(current, Facts(changed: changed), Now).Operation);
    }

    [Fact]
    public void EligibilityGateAndQuotaMustAllPass()
    {
        var facts = Facts();
        Assert.Equal("ineligible", PreviewEnvironmentPolicy.Decide(null, facts with { WebProject = false }, Now).Reason);
        Assert.Equal("ineligible", PreviewEnvironmentPolicy.Decide(null, facts with { ChangesVisibleOutput = false }, Now).Reason);
        Assert.Equal("awaiting-green-delivery", PreviewEnvironmentPolicy.Decide(null, facts with { GreenBuild = false }, Now).Reason);
        Assert.Equal("hosting-quota", PreviewEnvironmentPolicy.Decide(null, facts with { HostingQuotaAdmitted = false }, Now).Reason);
    }

    [Fact]
    public void AddressRejectsInvalidNamesAndUsesConfiguredDomain()
    {
        Assert.Equal("https://agt-3180.sample.private.example/",
            PreviewEnvironmentPolicy.AllocateAddress("AGT-3180", "Sample", "private.example").ToString());
        Assert.Throws<ArgumentException>(() => PreviewEnvironmentPolicy.AllocateAddress("bad.key", "sample"));
        Assert.Throws<ArgumentException>(() => PreviewEnvironmentPolicy.AllocateAddress("AGT-3180", "sample", "example"));
    }

    [Fact]
    public void PreviewSecretsNeverIncludeStageOrProduction()
    {
        Assert.True(PreviewSecretScope.Allows([new("sample-data", "preview")]));
        Assert.False(PreviewSecretScope.Allows([new("database", "preview"), new("stage-token", "stage")]));
        Assert.False(PreviewSecretScope.Allows([new("production-token", "production")]));
    }

    [Fact]
    public void AnotherCardsTerminalStateCannotRemoveThisPreview()
    {
        var current = PreviewEnvironmentPolicy.Decide(null, Facts(), Now).Desired!;
        var other = Facts() with { CardKey = "AGT-3181", Rejected = true };

        Assert.Equal("AGT-3180", current.CardKey);
        Assert.Equal("AGT-3181", other.CardKey);
        Assert.Throws<ArgumentException>(() => PreviewEnvironmentPolicy.Decide(current, other, Now));
    }
}
