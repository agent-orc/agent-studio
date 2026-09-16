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

    /// <summary>
    /// AGT-2795 acceptance item 3: a continue must never destroy an existing
    /// result. The concept card's dossier scaffold in
    /// <see cref="SummaryGenerationService"/> writes every post-run summary
    /// through <see cref="ResultVersionStore.Replace"/> (never a raw
    /// <c>File.WriteAllText</c>), so an implementation result that predates a
    /// card being repurposed into a concept card - exactly what AGT-2795's
    /// card AGT-2795/Dossier AGT-W54 did - survives a later concept-mode
    /// continue in <c>results/history/</c> instead of being overwritten in
    /// place.
    /// </summary>
    [Fact]
    public void Replace_OnConceptCard_PreservesPriorImplementationResultInHistory()
    {
        var store = new ResultVersionStore(NullLogger<ResultVersionStore>.Instance);

        var implementationResult =
            "# Status\n- Result: Success\n- Implemented the demo route count fix (81 to 83).\n";
        store.Replace(
            _folder,
            implementationResult,
            ResultProducer.RunAttempt("1"),
            TaskStates.Completed,
            new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc));

        // The card is repurposed into the concept card of Dossier AGT-W54 and a
        // later continue (allowed here, e.g. via an explicit mode override)
        // produces a fresh concept-mode summary.
        var conceptSummary = "# Status\n- Dossier: docs/agt-w54/index.html\n";
        var replacement = store.Replace(
            _folder,
            conceptSummary,
            ResultProducer.RunAttempt("2"),
            TaskStates.HumanReview,
            new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));

        // The current status.md is the new concept summary...
        Assert.Equal(conceptSummary, File.ReadAllText(Path.Combine(_folder, "status.md")));

        // ...and the prior implementation result is not lost: it is byte-for-byte
        // preserved under results/history/, still readable as version #1.
        var preserved = Assert.IsType<ResultHistoryVersion>(replacement.Preserved);
        Assert.Equal(1, preserved.Number);
        Assert.Equal(
            implementationResult,
            File.ReadAllText(Path.Combine(_folder, preserved.StatusPath)));
        Assert.Equal(implementationResult, store.ReadLocalVersion(_folder, 1));
    }
}
