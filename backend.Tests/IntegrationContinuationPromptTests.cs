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

    [Theory]
    [InlineData("solution-quality-gate", "Resolve the recorded review findings")]
    [InlineData("merge-into-develop", "one-to-one delivery commit mapping")]
    [InlineData("post-build-test-gate", "Resolve the recorded failure")]
    public void EveryBranchCarriesTheVerificationScope(string stage, string branchMarker)
    {
        var prompt = IntegrationContinuationPrompt.Build(
            "AGT-3093", "refs/heads/task/AGT-3093", "sha", "develop", stage, "Failed");

        Assert.Contains(branchMarker, prompt);
        Assert.Contains(IntegrationContinuationPrompt.VerificationScope.Trim(), prompt);
        Assert.Contains(GuardFirstGatePlan.GuardFilter, prompt);
        Assert.Contains("leave whole test projects to the gates", prompt);
        Assert.DoesNotContain("relevant tests", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceAsksToRerunTheFailingCommandFirst()
    {
        var prompt = IntegrationContinuationPrompt.Build(
            "AGT-3093", null, null, "develop", "post-orchestrator-decision", "Escalated",
            evidence: "post-build-test-gate: `npm --prefix frontend run lint` exited 1");

        Assert.Contains("npm --prefix frontend run lint", prompt);
        Assert.Contains("rerun that exact command first and fix it before any broader test run", prompt);
    }

    [Fact]
    public void WithoutEvidenceRef_DoesNotPointAtTheTaskServerPipelineFile()
    {
        var prompt = IntegrationContinuationPrompt.Build(
            "AGT-3093", null, null, "develop", "post-orchestrator-decision", "Escalated");

        Assert.DoesNotContain("pipeline-execution.json", prompt);
        Assert.DoesNotContain("Failure evidence ref", prompt);
        Assert.DoesNotContain("rerun that exact command first", prompt);
    }
}
