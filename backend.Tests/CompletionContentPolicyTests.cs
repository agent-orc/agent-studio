using AgentStudio.Tasks;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CompletionContentPolicyTests
{
    private const string Current = "brief-v2";

    [Fact]
    public void ConceptWithOnlyBuildAndLintCannotComplete()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "concept", Current, Current, Current, "concept-fit", null,
            ["build-tests", "lint"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("no content verdict", decision.Message);
    }

    [Fact]
    public void OlderDeliveryCannotCompleteEvenWithPassedContentReview()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, "brief-v1", Current, "requirement-fit", "pass",
            ["requirement-fit"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("delivered against brief", decision.Message);
    }

    [Fact]
    public void OlderContentVerdictCannotCompleteCurrentDelivery()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, "brief-v1", "requirement-fit", "pass",
            ["requirement-fit"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("content verdict was for brief", decision.Message);
    }

    [Fact]
    public void WrittenOverrideRecordsTheGapAndBothBriefVersions()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "concept", Current, "brief-v1", null, "concept-fit", null,
            ["build-tests", "lint"], [], true, "Accepted after manual review."));
        Assert.True(decision.Accepted);
        Assert.True(decision.Overridden);
        Assert.Contains("brief-v1", decision.Message);
        Assert.Contains(Current, decision.Message);
    }

    [Fact]
    public void CurrentDeliveryAndPassedCurrentContentCanComplete()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, Current, "requirement-fit", "pass",
            ["build-tests", "requirement-fit"], []));
        Assert.True(decision.Accepted);
        Assert.False(decision.Overridden);
    }

    [Fact]
    public void CodingCardCannotUseConceptFitAsItsContentVerdict()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, Current, "concept-fit", "pass",
            ["concept-fit"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("requirement-fit", decision.Message);
    }

    [Fact]
    public void CurrentConceptDeliveryWithPassedConceptFitCanComplete()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "concept", Current, Current, Current, "concept-fit", "pass",
            ["build-tests", "lint", "concept-fit"], []));
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void HistoricalReportFlagsLegacyClaimsAndLeavesBoundPassesOut()
    {
        Assert.True(CompletionContentPolicy.NeedsHistoricalReview(null));
        Assert.True(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim
        {
            CurrentBriefVersion = Current,
            DeliveryBriefVersion = Current,
            ReviewBriefVersion = Current,
            ContentStatus = null,
        }));
        Assert.False(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim
        {
            CurrentBriefVersion = Current,
            DeliveryBriefVersion = Current,
            ReviewBriefVersion = Current,
            ContentStatus = "pass",
            ContentAspect = "requirement-fit",
        }));
        Assert.True(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim
        {
            CurrentBriefVersion = Current,
            DeliveryBriefVersion = Current,
            ReviewBriefVersion = Current,
            ContentStatus = "pass",
            ContentAspect = "requirement-fit",
        }, "concept"));
    }

    [Fact]
    public void LocalContentArtifactKeepsItsReviewBriefWhenTheCardChanges()
    {
        var folder = Path.Combine(Path.GetTempPath(), "completion-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var oldVersion = BriefVersionStore.Record(folder, "First brief.");
            CompletionContentEvidence.StampLocalRun(folder);
            Assert.Equal(oldVersion, CompletionContentEvidence.ReadLocalRunVersion(folder));
            File.WriteAllText(Path.Combine(folder, CompletionContentEvidence.LocalContextFile),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    briefVersion = CompletionContentEvidence.ReadLocalRunVersion(folder),
                }));
            File.WriteAllText(Path.Combine(folder, "aspect-requirement-fit.json"),
                "{\"status\":\"pass\",\"summary\":\"Matches the first brief.\"}");
            var task = new TaskInfo { FolderPath = folder, Mode = "coding" };
            Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(task, null)).Accepted);

            BriefVersionStore.Record(folder, "Second brief.");
            var stale = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(task, null));
            Assert.False(stale.Accepted);
            Assert.Contains("delivered against brief", stale.Message);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
