namespace AgentStudio.Watcher;

/// <summary>What the Watcher wants to spend one unit of contingent on.</summary>
public enum WatcherSpendKind
{
    ModelCall,
    Proposal,
    Comment,
}

/// <summary>Consumption inside one window, derived from the durable spend rows.</summary>
public sealed record WatcherContingentUsage(
    long Tokens,
    int ModelCalls,
    int Proposals,
    int Comments)
{
    public static readonly WatcherContingentUsage Zero = new(0, 0, 0, 0);
}

/// <summary>
/// The contingent as the operator sees it: what is configured, what is used,
/// and what is left in each window. Unknown price is carried as null, never as
/// zero.
/// </summary>
public sealed record WatcherContingentSnapshot
{
    public required WatcherContingentOptions Budget { get; init; }
    public required WatcherContingentUsage Daily { get; init; }
    public required WatcherContingentUsage Weekly { get; init; }

    /// <summary>Realized cost in the weekly window, or null when any call had no catalog price.</summary>
    public double? WeeklyDollars { get; init; }

    /// <summary>True when no further model call or proposal is admissible in either window.</summary>
    public bool Exhausted { get; init; }

    /// <summary>Cases that are persistent but have no proposal because the contingent ran out.</summary>
    public int BacklogCases { get; init; }

    public long DailyTokensRemaining => Math.Max(0, Budget.DailyTokens - Daily.Tokens);
    public long WeeklyTokensRemaining => Math.Max(0, Budget.WeeklyTokens - Weekly.Tokens);
    public int DailyProposalsRemaining => Math.Max(0, Budget.DailyProposals - Daily.Proposals);
    public int WeeklyProposalsRemaining => Math.Max(0, Budget.WeeklyProposals - Weekly.Proposals);
}

/// <summary>Whether one spend is admissible, and why not when it is refused.</summary>
public sealed record WatcherContingentDecision(bool Allowed, string Reason)
{
    public static WatcherContingentDecision Allow(string reason) => new(true, reason);
    public static WatcherContingentDecision Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Pure budget arithmetic for the dedicated Watcher contingent of section 10.4.
/// Exhaustion stops model calls and proposals; it never stops detection, and it
/// never hides the backlog.
/// </summary>
public static class WatcherContingentPolicy
{
    public static readonly TimeSpan DailyWindow = TimeSpan.FromDays(1);
    public static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    public static WatcherContingentUsage Usage(
        IEnumerable<WatcherSpendEntry> spend,
        DateTime nowUtc,
        TimeSpan window)
    {
        var since = nowUtc - window;
        var rows = spend.Where(entry => entry.AtUtc > since).ToList();
        return new WatcherContingentUsage(
            rows.Sum(entry => entry.InputTokens + entry.OutputTokens),
            rows.Sum(entry => entry.ModelCalls),
            rows.Sum(entry => entry.Proposals),
            rows.Sum(entry => entry.Comments));
    }

    /// <summary>
    /// A token estimate is required for a model call so the budget is checked
    /// before the spend, not after it. Counting spends pass zero.
    /// </summary>
    public static WatcherContingentDecision Admit(
        WatcherSpendKind kind,
        long estimatedTokens,
        WatcherContingentOptions budget,
        WatcherContingentUsage daily,
        WatcherContingentUsage weekly)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (kind == WatcherSpendKind.ModelCall)
        {
            if (daily.ModelCalls >= budget.DailyModelCalls)
                return WatcherContingentDecision.Refuse($"daily model calls used up ({daily.ModelCalls}/{budget.DailyModelCalls})");
            if (weekly.ModelCalls >= budget.WeeklyModelCalls)
                return WatcherContingentDecision.Refuse($"weekly model calls used up ({weekly.ModelCalls}/{budget.WeeklyModelCalls})");
            if (daily.Tokens + estimatedTokens > budget.DailyTokens)
                return WatcherContingentDecision.Refuse($"daily token budget used up ({daily.Tokens}/{budget.DailyTokens})");
            if (weekly.Tokens + estimatedTokens > budget.WeeklyTokens)
                return WatcherContingentDecision.Refuse($"weekly token budget used up ({weekly.Tokens}/{budget.WeeklyTokens})");
            return WatcherContingentDecision.Allow("within the model-call contingent");
        }

        if (kind == WatcherSpendKind.Proposal)
        {
            if (daily.Proposals >= budget.DailyProposals)
                return WatcherContingentDecision.Refuse($"daily proposals used up ({daily.Proposals}/{budget.DailyProposals})");
            if (weekly.Proposals >= budget.WeeklyProposals)
                return WatcherContingentDecision.Refuse($"weekly proposals used up ({weekly.Proposals}/{budget.WeeklyProposals})");
            return WatcherContingentDecision.Allow("within the proposal contingent");
        }

        if (daily.Comments >= budget.DailyComments)
            return WatcherContingentDecision.Refuse($"daily comments used up ({daily.Comments}/{budget.DailyComments})");
        if (weekly.Comments >= budget.WeeklyComments)
            return WatcherContingentDecision.Refuse($"weekly comments used up ({weekly.Comments}/{budget.WeeklyComments})");
        return WatcherContingentDecision.Allow("within the comment contingent");
    }

    public static WatcherContingentSnapshot Describe(
        WatcherContingentOptions budget,
        IReadOnlyList<WatcherSpendEntry> spend,
        DateTime nowUtc,
        int backlogCases)
    {
        var daily = Usage(spend, nowUtc, DailyWindow);
        var weekly = Usage(spend, nowUtc, WeeklyWindow);
        var weeklyRows = spend.Where(entry => entry.AtUtc > nowUtc - WeeklyWindow).ToList();

        return new WatcherContingentSnapshot
        {
            Budget = budget,
            Daily = daily,
            Weekly = weekly,
            WeeklyDollars = weeklyRows.Count == 0
                ? 0d
                : weeklyRows.Any(entry => entry.ModelCalls > 0 && entry.Dollars is null)
                    ? null
                    : weeklyRows.Sum(entry => entry.Dollars ?? 0d),
            BacklogCases = backlogCases,
            Exhausted =
                !Admit(WatcherSpendKind.Proposal, 0, budget, daily, weekly).Allowed
                && !Admit(WatcherSpendKind.Comment, 0, budget, daily, weekly).Allowed,
        };
    }
}
