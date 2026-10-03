namespace AgentStudio.Runner;

public enum CauseBreakerAction
{
    /// <summary>Below the threshold: the ordinary bounded retry applies.</summary>
    Retry,
    /// <summary>This observation reaches the threshold: raise the cause card and park every affected card.</summary>
    Open,
    /// <summary>The breaker is already open: attach the card to the cause card and park it.</summary>
    Wait,
}

/// <summary>Threshold parameters, read from the reporting card's project settings.</summary>
public sealed record CauseBreakerThresholds(int Attempts, int Cards, TimeSpan Window);

/// <summary>One counted failure: which card, when.</summary>
public sealed record CauseBreakerCount(string TaskKey, DateTime At);

/// <summary>The decision plus the numbers that produced it, so the log can explain a halt.</summary>
public sealed record CauseBreakerDecision(
    CauseBreakerAction Action,
    int Attempts,
    int Cards,
    CauseBreakerThresholds Thresholds,
    string Reason);

public enum CauseBreakerCloseReason
{
    None,
    /// <summary>The cause card reached 6-completed, which the status contract reserves for integrated work.</summary>
    CauseIntegrated,
    /// <summary>An affected card's review ran through the same toolchain without an infrastructure failure.</summary>
    ProbeGreen,
}

/// <summary>
/// Pure decisions of the fleet-wide cause breaker (AGT-W57 §3 A, §5 E1).
/// <para>
/// E1 counted 411 identical <c>PreparationFailed</c> attempts on one card over
/// six days and 59 <c>ToolUnavailable</c> attempts after a provider withdrew the
/// review model. Each attempt was judged per card, so nothing ever saw the
/// repetition. The breaker counts per cause fingerprint across every project and
/// opens at three attempts or two distinct cards, whichever comes first.
/// </para>
/// </summary>
public static class CauseBreakerPolicy
{
    public const int DefaultAttemptThreshold = 3;
    public const int DefaultCardThreshold = 2;
    public const int DefaultWindowHours = 24;
    public const int MaxThreshold = 100;
    public const int MaxWindowHours = 24 * 30;

    public static CauseBreakerThresholds Clamp(int attempts, int cards, int windowHours)
        => new(
            Math.Clamp(attempts, 1, MaxThreshold),
            Math.Clamp(cards, 1, MaxThreshold),
            TimeSpan.FromHours(Math.Clamp(windowHours, 1, MaxWindowHours)));

    public static CauseBreakerThresholds From(AgentStudio.Shared.ProjectSettings settings)
        => Clamp(settings.CauseBreakerAttemptThreshold, settings.CauseBreakerCardThreshold, settings.CauseBreakerWindowHours);

    /// <summary>
    /// Decides one observation. <paramref name="counted"/> already contains the
    /// current observation; entries older than the window do not count.
    /// </summary>
    public static CauseBreakerDecision Decide(
        bool open,
        IReadOnlyList<CauseBreakerCount> counted,
        CauseBreakerThresholds thresholds,
        DateTime now)
    {
        var cutoff = now - thresholds.Window;
        var inWindow = counted.Where(count => count.At >= cutoff).ToArray();
        var attempts = inWindow.Length;
        var cards = inWindow.Select(count => count.TaskKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (open)
            return new CauseBreakerDecision(CauseBreakerAction.Wait, attempts, cards, thresholds,
                "The breaker for this cause is already open.");
        if (attempts >= thresholds.Attempts)
            return new CauseBreakerDecision(CauseBreakerAction.Open, attempts, cards, thresholds,
                $"{attempts} attempts reached the threshold of {thresholds.Attempts}.");
        if (cards >= thresholds.Cards)
            return new CauseBreakerDecision(CauseBreakerAction.Open, attempts, cards, thresholds,
                $"{cards} distinct cards reached the threshold of {thresholds.Cards}.");
        return new CauseBreakerDecision(CauseBreakerAction.Retry, attempts, cards, thresholds,
            $"{attempts}/{thresholds.Attempts} attempts and {cards}/{thresholds.Cards} cards are below the threshold.");
    }

    /// <summary>
    /// When an open breaker closes. Only 6-completed counts as integrated; an
    /// archived cause card did not prove anything (D5 fail-closed), so it keeps
    /// the breaker open until an operator probe or a green review closes it.
    /// </summary>
    public static CauseBreakerCloseReason Close(string? causeCardState, bool probeGreen)
    {
        if (string.Equals(causeCardState, AgentStudio.Shared.TaskStates.Completed, StringComparison.OrdinalIgnoreCase))
            return CauseBreakerCloseReason.CauseIntegrated;
        return probeGreen ? CauseBreakerCloseReason.ProbeGreen : CauseBreakerCloseReason.None;
    }
}
