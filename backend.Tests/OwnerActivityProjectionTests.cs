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
        Assert.Contains(result, row => row.Summary.StartsWith("Reissued DEMO-9"));
        Assert.Contains(result, row => row.Topic == "card-created");
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
