namespace AgentStudio.Pipeline;

/// <summary>Resolves one project's verification budget from settings and history.</summary>
public static class GateRunBudgetPolicy
{
    public const int DefaultMinutes = 60;
    public const int MinimumMinutes = 5;
    public const int MaximumMinutes = 180;
    public const int HistoryWindow = 20;

    public static TimeSpan Resolve(int? configuredMinutes, IEnumerable<long>? completedRunDurationsMs)
    {
        if (configuredMinutes is { } minutes)
            return TimeSpan.FromMinutes(Math.Clamp(minutes, MinimumMinutes, MaximumMinutes));

        var history = (completedRunDurationsMs ?? [])
            .Where(value => value > 0)
            .TakeLast(HistoryWindow)
            .Order()
            .ToArray();
        if (history.Length == 0) return TimeSpan.FromMinutes(DefaultMinutes);

        var index = Math.Clamp((int)Math.Ceiling(history.Length * 0.95) - 1, 0, history.Length - 1);
        var measured = TimeSpan.FromMilliseconds(history[index] * 1.5);
        return TimeSpan.FromMinutes(Math.Clamp(
            measured.TotalMinutes,
            MinimumMinutes,
            MaximumMinutes));
    }
}
