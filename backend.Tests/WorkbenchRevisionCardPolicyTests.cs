using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The card a Dossier rework request runs its revision on (AGT-3703). The
/// rework form shows it before confirmation, and confirm refuses before any
/// write when no source concept card can take the follow-up round.
/// </summary>
public sealed class WorkbenchRevisionCardPolicyTests
{
    [Fact]
    public void Resolve_PicksTheSourceConceptCardOfTheSameProject()
    {
        var card = WorkbenchRevisionCardPolicy.Resolve("Project", ["PRJ-7"],
        [
            Task("other", "PRJ-7", TaskStates.Completed, project: "Elsewhere"),
            Task("concept", "PRJ-7", TaskStates.Completed),
        ]);

        Assert.True(card.Available);
        Assert.Equal("PRJ-7", card.Key);
        Assert.Equal("concept", card.JobId);
        Assert.Equal(TaskStates.Completed, card.Lane);
        Assert.Equal(TaskModes.Concept, card.Mode);
        Assert.Null(card.Reason);
    }

    [Fact]
    public void Resolve_SkipsAnArchivedSourceForALaterLiveOne()
    {
        var card = WorkbenchRevisionCardPolicy.Resolve("Project", ["PRJ-1", "PRJ-2"],
        [
            Task("first", "PRJ-1", TaskStates.Archive),
            Task("second", "PRJ-2", TaskStates.Ready),
        ]);

        Assert.True(card.Available);
        Assert.Equal("PRJ-2", card.Key);
    }

    [Theory]
    [InlineData(new string[0], "names no source concept card")]
    [InlineData(new[] { "PRJ-1" }, "is archived")]
    [InlineData(new[] { "PRJ-9" }, "was not found")]
    [InlineData(new[] { "PRJ-3" }, "more than one card")]
    public void Resolve_ExplainsWhyNoCardCanRunTheRevision(string[] sourceKeys, string reason)
    {
        var card = WorkbenchRevisionCardPolicy.Resolve("Project", sourceKeys,
        [
            Task("archived", "PRJ-1", TaskStates.Archive),
            Task("twin-a", "PRJ-3", TaskStates.Ready),
            Task("twin-b", "PRJ-3", TaskStates.Backlog),
        ]);

        Assert.False(card.Available);
        Assert.Null(card.Key);
        Assert.Contains(reason, card.Reason);
    }

    [Theory]
    [InlineData(FollowUpQueueReasons.ProjectBusy, "runner slot")]
    [InlineData(FollowUpQueueReasons.LaneNotRunnable, "moved to Ready")]
    [InlineData(FollowUpQueueReasons.DeliveryUnderReview, "under review")]
    [InlineData(FollowUpQueueReasons.RemoteExecution, "remote runner")]
    [InlineData(null, "next runner pickup")]
    public void QueueDetail_NamesTheReasonAQueuedRoundWaits(string? reason, string expected)
    {
        Assert.Contains(expected, WorkbenchRevisionCardPolicy.QueueDetail(reason));
    }

    [Fact]
    public void FallbackPrompt_CarriesTheRequestAndTheOwnerSelections()
    {
        var prompt = WorkbenchRevisionCardPolicy.FallbackPrompt("notes-export", " Add a privacy section. ",
        [
            new WorkbenchDecisionResponse { DecisionId = "d3", Kind = "single", SelectedOptionIds = ["local-only"] },
            new WorkbenchDecisionResponse { DecisionId = "d4", Kind = "multi", SelectedOptionIds = [] },
        ]);

        Assert.Contains("Revise Dossier notes-export.", prompt);
        Assert.Contains("Owner request:\nAdd a privacy section.", prompt);
        Assert.Contains("- d3: local-only", prompt);
        Assert.DoesNotContain("d4", prompt);
        Assert.Contains("Keep the pending rework receipt", prompt);
    }

    private static TaskInfo Task(string id, string key, string state, string project = "Project") => new()
    {
        Id = id,
        Key = key,
        TaskKey = $"{project}::{key}",
        Title = $"Card {id}",
        State = state,
        ProjectName = project,
        Mode = TaskModes.Concept,
    };
}
