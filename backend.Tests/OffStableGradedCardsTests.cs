using AgentStudio.Review;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OffStableGradedCardsTests
{
    private const string Notice = "graded by release 20261105T1059Z-review-b9a3a90d6895 (commit b9a3a90), "
        + "which differs from Stable 0.9.5 (38b28d1)";

    // clock-independent: fixed grade instants, ordered against each other only.
    [Fact]
    public void Collect_ListsCardsWhoseLatestRemoteGradeCameFromANonStableBuild()
    {
        var drifted = Card("BH3-1", TaskStates.HumanReview);
        var regraded = Card("BH3-2", TaskStates.HumanReview);
        var stable = Card("BH3-3", TaskStates.HumanReview);
        var archived = Card("BH3-4", TaskStates.Archive);
        var projections = new Dictionary<string, ReviewProjectionView>(StringComparer.Ordinal)
        {
            [drifted.TaskKey] = View(Attempt("review_1", Notice, new DateTime(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc))),
            // Graded again by the Stable build after the drift: settled.
            [regraded.TaskKey] = View(
                Attempt("review_3", null, new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc)),
                Attempt("review_2", Notice, new DateTime(2026, 11, 5, 21, 0, 0, DateTimeKind.Utc))),
            [stable.TaskKey] = View(Attempt("review_4", null, new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc))),
            [archived.TaskKey] = View(Attempt("review_5", Notice, new DateTime(2026, 11, 6, 11, 0, 0, DateTimeKind.Utc))),
        };

        var cards = OffStableGradedCards.Collect([drifted, regraded, stable, archived], projections);

        var card = Assert.Single(cards);
        Assert.Equal("BH3-1", card.TaskKey);
        Assert.Equal(TaskStates.HumanReview, card.Lane);
        Assert.Equal("review_1", card.AttemptId);
        Assert.Equal(Notice, card.Notice);
        Assert.Equal("20261105T1059Z-review-b9a3a90d6895", card.WorkerReleaseId);
    }

    private static TaskInfo Card(string key, string state) => new()
    {
        Id = key, Key = key, TaskKey = key, Title = $"Card {key}", State = state, ProjectName = "Batch",
    };

    private static ReviewProjectionView View(params ReviewAttempt[] attempts) =>
        ReviewProjectionView.Empty with { Attempts = attempts };

    private static ReviewAttempt Attempt(string id, string? notice, DateTime receivedAt) => new()
    {
        Plane = ReviewPlane.Remote, AttemptId = id, ReceivedAt = receivedAt, Outcome = "Pass",
        BuildTestsResult = "passed", ReportRef = $"remote-review-grade-{id}.md",
        WorkerReleaseId = notice is null ? "stable-release" : "20261105T1059Z-review-b9a3a90d6895",
        WorkerReleaseStable = notice,
    };
}
