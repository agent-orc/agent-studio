using AgentStudio.Git;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationContinuationPromptTests
{
    [Fact]
    public void ConflictPromptCarriesBothSidesStagesAndFiles()
    {
        var report = new IntegrationConflictReport
        {
            IntegrationTipSha = "integration-tip-123",
            DeliverySha = "delivery-sha-456",
            ConflictedFiles = ["src/a.cs", "src/b.cs"],
            Stages = [
                new IntegrationConflictStageReport { Stage = "direct-merge", Outcome = "conflict", ConflictedFileCount = 2 },
                new IntegrationConflictStageReport { Stage = "rebase-fallback", Outcome = "conflict", ConflictedFileCount = 1,
                    StoppedCommitSha = "commit-789" },
            ],
        };

        var prompt = IntegrationContinuationPrompt.Build(
            "AGT-2909", "refs/heads/task/AGT-2909", "delivery-sha-456", "develop",
            "merge-into-develop", "Merge failed", report);

        Assert.Contains("integration-tip-123", prompt);
        Assert.Contains("refs/heads/task/AGT-2909 at delivery-sha-456", prompt);
        Assert.Contains("rebase-fallback", prompt);
        Assert.Contains("commit-789", prompt);
        Assert.Contains("src/a.cs, src/b.cs", prompt);
        Assert.Contains("model, CLI, and reasoning pins", prompt);
    }

    [Fact]
    public void ReviewPromptCarriesFindingWithoutDemandingIntegrationRebase()
    {
        var prompt = IntegrationContinuationPrompt.Build(
            "AGT-2909", null, null, "develop", "solution-quality-gate",
            "Review concerns", evidence: "Test Foo.Bar failed: expected 2, got 1");

        Assert.Contains("Test Foo.Bar failed: expected 2, got 1", prompt);
        Assert.Contains("Resolve the recorded review findings", prompt);
        Assert.DoesNotContain("reconcile it with the latest", prompt);
    }

    [Fact]
    public void PendingPromptCarriesCurrentIntegrationTipWithoutClaimingMergeAttempts()
    {
        var prompt = IntegrationContinuationPrompt.Build(
            "AGT-2909", "refs/heads/task/AGT-2909", "delivery-sha", "develop",
            "integration reach", "Delivery is pending", integrationTip: "tip-789",
            evidenceRef: "results/gate-output.txt");

        Assert.Contains("tip-789", prompt);
        Assert.Contains("Delivery is pending", prompt);
        Assert.Contains("results/gate-output.txt", prompt);
        Assert.DoesNotContain("The platform attempted", prompt);
    }
}
