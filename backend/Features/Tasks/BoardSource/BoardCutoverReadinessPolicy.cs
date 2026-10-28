namespace AgentStudio.Tasks;

public static class BoardCutoverReadinessStates
{
    public const string ShadowOff = "shadow-off";
    public const string NoComparisons = "no-comparisons";
    public const string ComparisonsStalled = "comparisons-stalled";
    public const string CleanWindowOpen = "clean-window-open";
    public const string Ready = "ready";
    public const string Switched = "switched";
}

public sealed record BoardCutoverReadiness(
    string State,
    bool Ready,
    DateTimeOffset? CleanSince,
    double CleanDays,
    double RequiredDays);

/// <summary>
/// Pure cutover gate for item 4 of the board read model card: switch to
/// <c>task-server</c> after 7 days of continuous shadow comparison with zero
/// unexplained differences. A gap of more than a day without a comparison
/// restarts the window, and so does an unexplained difference: the window
/// runs from the later of both.
/// </summary>
public static class BoardCutoverReadinessPolicy
{
    public static readonly TimeSpan RequiredCleanWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxComparisonGap = TimeSpan.FromHours(24);

    public static BoardCutoverReadiness Evaluate(
        BoardSourceMode mode,
        DateTimeOffset now,
        DateTimeOffset? continuousSince,
        DateTimeOffset? lastComparisonAt,
        DateTimeOffset? lastUnexplainedSeenAt)
    {
        var required = RequiredCleanWindow.TotalDays;
        if (mode == BoardSourceMode.TaskServer)
            return new BoardCutoverReadiness(BoardCutoverReadinessStates.Switched, false, null, 0, required);
        if (mode == BoardSourceMode.Workspace)
            return new BoardCutoverReadiness(BoardCutoverReadinessStates.ShadowOff, false, null, 0, required);
        if (continuousSince is null || lastComparisonAt is null)
            return new BoardCutoverReadiness(BoardCutoverReadinessStates.NoComparisons, false, null, 0, required);
        if (now - lastComparisonAt.Value > MaxComparisonGap)
            return new BoardCutoverReadiness(BoardCutoverReadinessStates.ComparisonsStalled, false, null, 0, required);

        var cleanSince = lastUnexplainedSeenAt is { } seen && seen > continuousSince.Value ? seen : continuousSince.Value;
        var cleanDays = Math.Max(0, (now - cleanSince).TotalDays);
        return now - cleanSince >= RequiredCleanWindow
            ? new BoardCutoverReadiness(BoardCutoverReadinessStates.Ready, true, cleanSince, cleanDays, required)
            : new BoardCutoverReadiness(BoardCutoverReadinessStates.CleanWindowOpen, false, cleanSince, cleanDays, required);
    }

    /// <summary>
    /// The start of continuous comparison after recording one at <paramref name="now"/>:
    /// kept while comparisons follow each other within the allowed gap.
    /// </summary>
    public static DateTimeOffset NextContinuousSince(
        DateTimeOffset now, DateTimeOffset? continuousSince, DateTimeOffset? lastComparisonAt)
        => continuousSince is { } since && lastComparisonAt is { } last && now - last <= MaxComparisonGap
            ? since
            : now;
}
