using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3215: a lane that diverged from origin drops a commit only when origin
/// holds its delivery or a newer delivery of the same card. Anything else
/// stalls once, visibly, with the blocked cards.
/// </summary>
public sealed class IntegrationLaneDivergencePolicyTests
{
    private const string Lane = "1111111111111111111111111111111111111111";
    private const string Delivery = "2222222222222222222222222222222222222222";
    private const string Successor = "3333333333333333333333333333333333333333";

    [Theory]
    // Not a delivery merge: nothing proves origin carries it.
    [InlineData(false, false, null, null, false, LaneCommitStatus.Unpublished)]
    [InlineData(false, true, null, null, false, LaneCommitStatus.Unpublished)]
    // Origin contains the delivery itself.
    [InlineData(true, true, null, null, false, LaneCommitStatus.Contained)]
    [InlineData(true, true, "AGT-1", Successor, false, LaneCommitStatus.Contained)]
    // Origin contains a newer delivery of the same card.
    [InlineData(true, false, "AGT-1", Successor, true, LaneCommitStatus.Superseded)]
    // The successor is not published, the card is unknown, or the "successor" is the delivery itself.
    [InlineData(true, false, "AGT-1", Successor, false, LaneCommitStatus.Unpublished)]
    [InlineData(true, false, null, Successor, true, LaneCommitStatus.Unpublished)]
    [InlineData(true, false, "AGT-1", null, true, LaneCommitStatus.Unpublished)]
    [InlineData(true, false, "AGT-1", Delivery, true, LaneCommitStatus.Unpublished)]
    public void Classify_DropsOnlyWhatOriginProvablyHolds(
        bool isDeliveryMerge,
        bool deliveryContained,
        string? cardKey,
        string? successor,
        bool successorPublished,
        LaneCommitStatus expected)
    {
        var classified = IntegrationLaneDivergencePolicy.Classify(new LaneCommitFacts(
            Lane, isDeliveryMerge ? Delivery : null, deliveryContained, cardKey, successor, successorPublished));

        Assert.Equal(expected, classified.Status);
        Assert.Equal(expected == LaneCommitStatus.Superseded ? Successor : null, classified.SuccessorSha);
    }

    [Fact]
    public void Decide_RestartsOnlyWhenNoUnpublishedCommitRemains()
    {
        var contained = new LaneCommitFacts(Lane, Delivery, true);
        var superseded = new LaneCommitFacts(Lane, Delivery, false, "AGT-1", Successor, true);
        var unpublished = new LaneCommitFacts(Lane, Delivery, false);

        Assert.Equal(LaneDivergenceAction.RestartAtPublished, IntegrationLaneDivergencePolicy.Decide([]).Action);
        Assert.Equal(
            LaneDivergenceAction.RestartAtPublished,
            IntegrationLaneDivergencePolicy.Decide([contained, superseded]).Action);
        var stalled = IntegrationLaneDivergencePolicy.Decide([contained, unpublished]);
        Assert.Equal(LaneDivergenceAction.Stall, stalled.Action);
        Assert.Single(stalled.Unpublished);
    }

    [Fact]
    public void Describe_NamesWhyACommitWasDropped()
    {
        var superseded = IntegrationLaneDivergencePolicy.Classify(
            new LaneCommitFacts(Lane, Delivery, false, "AGT-3065", Successor, true));

        Assert.Equal("111111111 AGT-3065 (delivery 222222222) superseded by published delivery 333333333", superseded.Describe());
    }

    [Fact]
    public void BoardLineage_FindsTheCardByItsLaneMergeAndReportsItsCurrentDelivery()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lane-lineage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var card = new TaskInfo
            {
                Id = "AGT-3065",
                FolderPath = folder,
                Provenance = new TaskProvenance { Merge = new TaskProvenanceMerge { MergeCommit = Lane } },
                CompletionClaim = new TaskCompletionClaim { ResultSha = Successor },
            };
            var other = new TaskInfo { Id = "AGT-3166", FolderPath = folder };
            var lineage = new TaskBoardLaneDeliveryLineage(() => [other, card]);

            var found = lineage.Find(Lane, Delivery);

            Assert.Equal(new LaneDeliveryCard("AGT-3065", Successor), found);
            Assert.Null(lineage.Find("4444444444444444444444444444444444444444", Delivery));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void LaneStall_RaisesOneFeedEventCountsBlockedCardsAndClearsOnRecovery()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "lane-stall-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(workspace, "projects", "agent-taskboard");
        Directory.CreateDirectory(watchPath);
        try
        {
            var (service, orchestratorLog) = HealthService(watchPath);
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero));
            var commits = new[]
            {
                IntegrationLaneDivergencePolicy.Classify(new LaneCommitFacts(Lane, Delivery, false)),
                IntegrationLaneDivergencePolicy.Classify(new LaneCommitFacts(Successor, Successor, true)),
            };

            service.IntegrationLaneStalled(new IntegrationLaneStall(
                "agent-taskboard", watchPath, "develop", "AGT-3201", commits, time.GetUtcNow().UtcDateTime));
            time.Advance(TimeSpan.FromMinutes(5));
            service.IntegrationLaneStalled(new IntegrationLaneStall(
                "agent-taskboard", watchPath, "develop", "AGT-3202", commits, time.GetUtcNow().UtcDateTime));

            var entry = Assert.Single(orchestratorLog.Read(watchPath));
            Assert.Equal(OrchestratorLogKinds.Alert, entry.Kind);
            Assert.Contains("Integration lane develop diverged from origin", entry.Summary);
            Assert.Contains("222222222", entry.Reasoning);
            Assert.DoesNotContain("333333333", entry.Reasoning);

            var snapshot = service.Snapshot("agent-taskboard", time.GetUtcNow().UtcDateTime)!;
            Assert.Equal("alarm", snapshot.Status);
            var alert = Assert.Single(snapshot.Alerts);
            Assert.Equal(PipelineHealthConventions.IntegrationLaneDivergedKind, alert.Kind);
            Assert.Contains("2 card(s) blocked", alert.Summary);
            Assert.Contains("AGT-3201, AGT-3202", alert.Detail);

            service.IntegrationLaneRecovered("agent-taskboard", "develop", time.GetUtcNow().UtcDateTime);

            Assert.Empty(service.Snapshot("agent-taskboard", time.GetUtcNow().UtcDateTime)!.Alerts);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static (PipelineHealthService Service, OrchestratorLog Log) HealthService(string watchPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Path.GetFileName(watchPath),
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
            })
            .Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var service = new PipelineHealthService(
            new PipelineHealthDetector(),
            scanner,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            orchestratorLog,
            NullLogger<PipelineHealthService>.Instance);
        return (service, orchestratorLog);
    }
}
