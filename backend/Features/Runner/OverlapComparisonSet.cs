namespace AgentStudio.Runner;

/// <summary>
/// DS-0 (decision DS-D10 B of <c>docs/dispatcher-personas/index.html</c>):
/// collects the in-flight cards of a project, the cards a candidate is compared
/// with under the <see cref="OverlapWindows.InFlight"/> window. A card stays
/// exposed from the start of its coding run until it integrates, so the set
/// holds every card of the project that started a coding run and has not
/// integrated yet: Progress, Auto Review (which includes cards waiting for the
/// batch gate), Human Review (cards waiting for the acceptance integration) and
/// queued merge rounds (a started card back in Ready with a pending follow-up).
///
/// <para>
/// The host collects these facts from the card snapshot it already holds and
/// hands them to <see cref="ParallelSlotPolicy.Decide(string, TaskParallelism, IReadOnlyList{RunningTask}, int, IReadOnlyList{InFlightTask}, string?)"/>.
/// No git is read: integration is known from the durable facts the platform
/// writes when a delivery lands (the provenance merge record and the
/// integration records), compared with the start of the current coding run.
/// </para>
///
/// <para>
/// Not in the set: Escalated, Failed pickup and Code not complete cards, which
/// wait on a person or never delivered; Completed and Archive cards, which have
/// integrated; planning and research cards, which deliver no repository
/// change; and cards that declare no branch.
/// </para>
/// </summary>
public static class OverlapComparisonSet
{
    /// <summary>Lanes after coding whose card holds its delivery until it integrates.</summary>
    private static readonly string[] PostCodingLanes = [TaskStates.AutoReview, TaskStates.HumanReview];

    private static readonly string[] IntegratedRecordClasses =
    [
        IntegrationRecordClasses.IntegratedVerified,
        IntegrationRecordClasses.IntegratedHistorical,
        IntegrationRecordClasses.CuratedMapping,
        IntegrationRecordClasses.NoCodeExpected,
    ];

    /// <summary>The comparison set of one project on the remote claim.</summary>
    public sealed record ProjectSet(IReadOnlyList<RunningTask> Running, IReadOnlyList<InFlightTask> InFlight);

    /// <summary>
    /// The remote claim's comparison set: the project's Progress cards, which
    /// hold the leased runs and the project occupancy, as running cards, and
    /// under <see cref="OverlapWindows.InFlight"/> every other in-flight card.
    /// </summary>
    public static ProjectSet ForProject(
        IEnumerable<TaskInfo> cards,
        string project,
        string window,
        Func<TaskInfo, TaskParallelism> predict)
    {
        var projectCards = cards
            .Where(card => card is not null && !card.Fixture
                && string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var running = projectCards
            .Where(card => string.Equals(card.State, TaskStates.Progress, StringComparison.Ordinal))
            .Select(card => new RunningTask(card.Id, predict(card) ?? TaskParallelism.Default))
            .ToArray();
        if (OverlapWindows.Normalize(window) != OverlapWindows.InFlight)
            return new ProjectSet(running, Array.Empty<InFlightTask>());
        var runningIds = new HashSet<string>(running.Select(r => r.TaskId), StringComparer.OrdinalIgnoreCase);
        return new ProjectSet(running, Collect(projectCards, project, runningIds, predict));
    }

    /// <summary>
    /// The in-flight cards of <paramref name="project"/> in
    /// <paramref name="cards"/>, without the cards in
    /// <paramref name="runningIds"/> (they are compared as running cards).
    /// </summary>
    public static IReadOnlyList<InFlightTask> Collect(
        IEnumerable<TaskInfo> cards,
        string project,
        IReadOnlySet<string> runningIds,
        Func<TaskInfo, TaskParallelism> predict)
    {
        var result = new List<InFlightTask>();
        foreach (var card in cards)
        {
            if (card is null || card.Fixture) continue;
            if (!string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase)) continue;
            if (runningIds.Contains(card.Id)) continue;
            if (!IsInFlight(card)) continue;
            // An exclusive card blocks only while it runs, so an in-flight
            // entry carries its scope and never the exclusive flag.
            var scope = predict(card)?.PredictedScope ?? Array.Empty<string>();
            result.Add(new InFlightTask(card.Id, new TaskParallelism(false, scope), card.State));
        }
        return result;
    }

    /// <summary>
    /// The part of <paramref name="inFlight"/> a candidate is compared with. A
    /// candidate that already started (a merge round in Ready, a Progress card
    /// without a run) is compared only with post-coding cards. Two started
    /// cards that both wait for a pickup can then never hold each other, while
    /// a fresh candidate still waits for every started card it overlaps.
    /// </summary>
    public static IReadOnlyList<InFlightTask> For(TaskInfo candidate, IReadOnlyList<InFlightTask> inFlight)
    {
        if (!HasStartedCodingRun(candidate)) return inFlight;
        return inFlight.Where(item => PostCodingLanes.Contains(item.Lane, StringComparer.Ordinal)).ToArray();
    }

    /// <summary>True when the card started a coding run and has not integrated it yet.</summary>
    public static bool IsInFlight(TaskInfo card)
    {
        if (card.NoBranchExpected) return false;
        if (TaskModes.IsReportOnly(card.Mode)) return false;
        var lane = card.State;
        if (string.Equals(lane, TaskStates.Progress, StringComparison.Ordinal))
            return true;
        if (PostCodingLanes.Contains(lane, StringComparer.Ordinal))
            return !HasIntegratedSinceLastStart(card);
        if (string.Equals(lane, TaskStates.Ready, StringComparison.Ordinal))
            return card.PendingIntent is not null
                   && HasStartedCodingRun(card)
                   && !HasIntegratedSinceLastStart(card);
        return false;
    }

    /// <summary>
    /// True when the card entered Progress before or carries a delivery commit.
    /// A card in Progress or a post-coding lane has started by definition.
    /// </summary>
    public static bool HasStartedCodingRun(TaskInfo card)
    {
        if (string.Equals(card.State, TaskStates.Progress, StringComparison.Ordinal)
            || PostCodingLanes.Contains(card.State, StringComparer.Ordinal))
            return true;
        return LastCodingStart(card) is not null
               || card.Commits.Count > 0
               || card.Commit is not null;
    }

    /// <summary>
    /// True when a durable integration fact is newer than the start of the
    /// current coding run: the provenance merge record, or an integration
    /// record of an integrated class. Without a recorded start any such fact
    /// counts.
    /// </summary>
    public static bool HasIntegratedSinceLastStart(TaskInfo card)
    {
        var since = LastCodingStart(card);
        if (card.Provenance?.Merge is { MergeCommit.Length: > 0 } merge
            && (since is null || merge.AtUtc >= since.Value))
            return true;
        return card.IntegrationRecords.Any(record =>
            IntegratedRecordClasses.Contains(record.Classification, StringComparer.Ordinal)
            && (since is null || record.RecordedAtUtc >= since.Value));
    }

    private static DateTime? LastCodingStart(TaskInfo card)
        => card.Provenance?.Transitions
            .LastOrDefault(transition => string.Equals(transition.Lane, TaskStates.Progress, StringComparison.Ordinal))
            ?.AtUtc;
}
