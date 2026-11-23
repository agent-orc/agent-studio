using AgentStudio.Runner;
using AgentStudio.Docs;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OwnerActivityProjectionTests
{
    [Fact]
    public void ProjectsAcceptedRefusedReissuedAndCreatedOwnerEvents()
    {
        var task = new TaskInfo
        {
            Id = "job-1", TaskKey = "DEMO-9", Title = "Example",
            ProjectName = "Demo", WatchPath = "/demo",
        };
        var events = new[]
        {
            Event(TimelineEventKinds.LaneChanged, "5-human-review → 6-completed",
                new() { ["from"] = TaskStates.HumanReview, ["to"] = TaskStates.Completed }),
            Event(TimelineEventKinds.OwnerMoveRefused, "Move to 6-completed refused: missing gate",
                new() { ["reason"] = "missing gate" }),
            Event(TimelineEventKinds.LaneChanged, "5-human-review → 2-ready",
                new() { ["from"] = TaskStates.HumanReview, ["to"] = TaskStates.Ready }),
            Event(TimelineEventKinds.PromptCreated, "Created"),
            Event(TimelineEventKinds.LaneChanged, "Automated", actor: TimelineActors.System),
        };

        var result = OwnerActivityProjection.FromTimeline(task, events).ToList();

        Assert.Equal(4, result.Count);
        Assert.All(result, row => Assert.Equal("owner_action", row.Kind));
        Assert.Contains(result, row => row.Summary.Contains("6-completed") && row.JobId == "job-1");
        Assert.Contains(result, row => row.Topic == "refusal" && row.Reasoning == "missing gate");
        Assert.Contains(result, row => row.Summary.StartsWith("Reissued job-1"));
        Assert.Contains(result, row => row.Topic == "card-created");
    }

    [Fact]
    public void TimelineSummariesUseDisplayKeyAndKeepProjectScopedNavigation()
    {
        var events = new[]
        {
            Event(TimelineEventKinds.PromptCreated, "Created"),
            Event(TimelineEventKinds.LaneChanged, "5-human-review → 2-ready",
                new() { ["from"] = TaskStates.HumanReview, ["to"] = TaskStates.Ready }),
            Event(TimelineEventKinds.LaneChanged, "5-human-review → 6-completed",
                new() { ["from"] = TaskStates.HumanReview, ["to"] = TaskStates.Completed }),
            Event(TimelineEventKinds.OwnerMoveRefused, "Move refused"),
        };
        var demo = new TaskInfo
        {
            Id = "shared-id", Key = "DEMO-20", TaskKey = "/tmp/agt3645-stack-b/projects/demo-app::DEMO-20",
            Title = "Demo card", ProjectName = "Demo", WatchPath = "/tmp/agt3645-stack-b/projects/demo-app",
        };
        var other = demo with
        {
            Key = "OTHER-20", TaskKey = "/tmp/other/projects/app::OTHER-20",
            ProjectName = "Other", WatchPath = "/tmp/other/projects/app",
        };

        var demoRows = OwnerActivityProjection.FromTimeline(demo, events).ToList();
        var otherRows = OwnerActivityProjection.FromTimeline(other, events).ToList();

        Assert.Equal(4, demoRows.Count);
        Assert.Contains(demoRows, row => row.Summary == "Created DEMO-20: Demo card");
        Assert.Contains(demoRows, row => row.Summary.StartsWith("Reissued DEMO-20:"));
        Assert.Contains(demoRows, row => row.Summary.StartsWith("Accepted DEMO-20:"));
        Assert.Contains(demoRows, row => row.Summary.StartsWith("DEMO-20: Move refused"));
        Assert.All(demoRows.Concat(otherRows), row => Assert.DoesNotContain("/tmp/", row.Summary));
        Assert.All(demoRows, row =>
        {
            Assert.Equal("Demo", row.Project);
            Assert.Equal("shared-id", row.JobId);
            Assert.Equal(demo.WatchPath, row.WatchPath);
        });
        Assert.All(otherRows, row =>
        {
            Assert.StartsWith("OTHER-20", row.Summary.Replace("Created ", "").Replace("Reissued ", "").Replace("Accepted ", ""));
            Assert.Equal("Other", row.Project);
            Assert.Equal("shared-id", row.JobId);
            Assert.Equal(other.WatchPath, row.WatchPath);
        });
    }

    [Theory]
    [InlineData("system")]
    [InlineData("pipeline")]
    [InlineData("Orchestrator")]
    public void SystemCreatedCardIsNotAnOwnerAction_EvenWithALegacyOwnerActor(string createdBy)
    {
        var task = new TaskInfo { Id = "health-production-1", Key = "NS-2", Title = "Health incident", ProjectName = "Demo" };
        var events = new[]
        {
            Event(TimelineEventKinds.PromptCreated, "Created", new()
            {
                ["creationSource"] = "environment-health", ["createdBy"] = createdBy,
            }, actor: "human:local-default"),
        };

        Assert.Empty(OwnerActivityProjection.FromTimeline(task, events));
    }

    [Fact]
    public void ProjectsConfirmedDossierDecisionWithItsDossierLink()
    {
        var decidedAt = DateTime.UnixEpoch.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var dossier = new WorkbenchListItem("demo-w2", "Decision", "", "decided", null,
            DateTime.UnixEpoch, "docs/demo/index.html", true, null, [])
        {
            Key = "DEMO-W2",
            Decision = new WorkbenchDecisionProjection(
                Outcome: "feature-spawn", State: "confirmed", OperationId: "op-1",
                SourceRevision: null, SourceFingerprint: null, PreparedAt: "", PreparedBy: "",
                ConfirmedAt: decidedAt, ConfirmedBy: "owner",
                DecidedAt: decidedAt, Reason: "Use option A", Failure: null,
                SpawnedTaskKeys: ["DA-1"], Responses: [], TaskDraft: null, Action: null,
                CliType: null, Model: null, ThinkingLevel: null, SourceEntryFingerprint: null),
        };

        var row = OwnerActivityProjection.FromDossier("Demo", dossier);

        Assert.NotNull(row);
        Assert.Equal("demo-w2", row.DossierId);
        Assert.Contains("DEMO-W2", row.Summary);
        Assert.Equal("Use option A", row.Reasoning);
    }

    private static TimelineEvent Event(string kind, string summary,
        Dictionary<string, string>? details = null, string actor = "human:owner") => new()
        { Kind = kind, Summary = summary, Details = details, Actor = actor, Ts = DateTime.UnixEpoch };
}
