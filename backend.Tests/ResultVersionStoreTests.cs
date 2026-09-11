using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ResultVersionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "result-version-tests-" + Guid.NewGuid().ToString("N"));

    public ResultVersionStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", true, false)]
    [InlineData("# Status\n- Result: Success", true, false)]
    [InlineData("# Status\n\nNotes mention <!-- agent-studio:result-scaffold --> only.", true, false)]
    [InlineData("<!-- agent-studio:result-scaffold -->\n# Status", false, false)]
    [InlineData("<!-- agent-studio:result-scaffold -->\n# Status", true, true)]
    public void ScaffoldGuard_WritesOnlyForMissingOrOwnedScaffold(
        string? existing,
        bool refresh,
        bool expected)
        => Assert.Equal(expected, ResultScaffoldPolicy.ShouldWrite(existing, refresh));

    [Fact]
    public void Replace_PreservesStatusAndDeliverablesWithProducerLaneAndTimeline()
    {
        var firstAt = new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc);
        var secondAt = new DateTime(2026, 9, 11, 8, 21, 0, DateTimeKind.Utc);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var store = new ResultVersionStore(NullLogger<ResultVersionStore>.Instance, timeline);
        Directory.CreateDirectory(Path.Combine(_folder, "results"));

        store.Replace(
            _folder,
            "# Status\n- Result: Success\n- Case: blocked\n",
            ResultProducer.RunAttempt("3"),
            TaskStates.Escalated,
            firstAt);
        File.WriteAllText(Path.Combine(_folder, "results", "deliverables.md"), "first deliverables\n");
        var replacement = store.Replace(
            _folder,
            "# Status\n- Result: Success\n- Case: generic\n",
            ResultProducer.ExternalCompletion("operator-chat"),
            TaskStates.HumanReview,
            secondAt,
            TimelineActors.External);

        var previous = Assert.IsType<ResultHistoryVersion>(replacement.Preserved);
        Assert.Equal(1, previous.Number);
        Assert.Equal(firstAt, previous.ProducedAtUtc);
        Assert.Equal("run-attempt", previous.Producer.Kind);
        Assert.Equal("run attempt #3", previous.Producer.Label);
        Assert.Equal(TaskStates.Escalated, previous.Lane);
        Assert.Equal(
            "# Status\n- Result: Success\n- Case: blocked\n",
            File.ReadAllText(Path.Combine(_folder, previous.StatusPath)));
        Assert.Equal(
            "first deliverables\n",
            File.ReadAllText(Path.Combine(_folder, previous.DeliverablesPath!)));

        var evt = Assert.Single(timeline.ReadAll(_folder), item => item.Kind == TimelineEventKinds.ResultReplaced);
        Assert.Equal("Result replaced by external completion (operator-chat), previous version kept as #1", evt.Summary);
        Assert.Equal(previous.StatusPath, evt.PayloadRef);
    }
}
