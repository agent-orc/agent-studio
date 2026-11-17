namespace AgentStudio.Review;

/// <summary>
/// One card whose latest Remote Review grade came from a review host that did
/// not run the Stable build (AGT-3845). <see cref="Notice"/> is the
/// <c>workerReleaseStable</c> sentence the grade report recorded (AGT-3689).
/// </summary>
public sealed record OffStableGradedCard(
    string TaskKey,
    string Id,
    string ProjectName,
    string Title,
    string Lane,
    string AttemptId,
    DateTime? GradedAt,
    string? WorkerReleaseId,
    string Notice);

/// <summary>
/// Lists the cards a review role graded while it differed from Stable, from
/// the published review projections only: no file, Git or scan work.
/// </summary>
public static class OffStableGradedCards
{
    public static IReadOnlyList<OffStableGradedCard> Collect(
        IEnumerable<TaskInfo> tasks,
        IReadOnlyDictionary<string, ReviewProjectionView> projections)
    {
        var cards = new List<OffStableGradedCard>();
        foreach (var task in tasks)
        {
            if (task.Fixture || task.State == TaskStates.Archive) continue;
            if (!projections.TryGetValue(task.TaskKey, out var projection)) continue;
            // A later grade by the Stable build settles the card, so only the
            // newest remote attempt counts.
            var latest = projection.Attempts.FirstOrDefault(attempt => attempt.Plane == ReviewPlane.Remote);
            var notice = latest?.WorkerReleaseStable?.Trim();
            if (latest is null || string.IsNullOrEmpty(notice)) continue;
            cards.Add(new OffStableGradedCard(task.Key ?? task.Id, task.Id, task.ProjectName, task.Title,
                task.State, latest.AttemptId, latest.ReceivedAt, latest.WorkerReleaseId, notice));
        }
        return cards
            .OrderByDescending(card => card.GradedAt ?? DateTime.MinValue)
            .ThenBy(card => card.TaskKey, StringComparer.Ordinal)
            .ToArray();
    }
}
