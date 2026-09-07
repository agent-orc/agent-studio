namespace AgentStudio.Watcher;

/// <summary>The three countable things the Watcher spends its contingent on.</summary>
public enum WatcherSpendKind
{
    /// <summary>A bounded Mini/high compression call or a Sol/medium analysis call.</summary>
    ModelCall,

    /// <summary>A ticket proposal written into the proposal lane.</summary>
    Proposal,

    /// <summary>A note appended to an existing card instead of a new proposal.</summary>
    Comment,
}

/// <summary>
/// Per-day and per-week budgets in tokens and in counts (dossier section 10.4).
/// Zero means "not allowed", which is the configuration the acceptance criteria
/// exercise: detection keeps running, spending stops.
/// </summary>
public sealed record WatcherContingent(
    int ModelCallsPerDay,
    int ModelCallsPerWeek,
    int ProposalsPerDay,
    int ProposalsPerWeek,
    int CommentsPerDay,
    int CommentsPerWeek,
    long TokensPerDay,
    long TokensPerWeek)
{
    public static WatcherContingent Defaults() => new(
        ModelCallsPerDay: 40,
        ModelCallsPerWeek: 150,
        ProposalsPerDay: 8,
        ProposalsPerWeek: 25,
        CommentsPerDay: 20,
        CommentsPerWeek: 60,
        TokensPerDay: 400_000,
        TokensPerWeek: 1_500_000);

    /// <summary>All budgets set to zero. Detection continues; no model call and no proposal is admitted.</summary>
    public static WatcherContingent Zero() => new(0, 0, 0, 0, 0, 0, 0, 0);

    public static WatcherContingent FromConfiguration(IConfiguration configuration)
    {
        var section = configuration
            .GetSection(WatcherDefaults.ConfigurationSection)
            .GetSection("Contingent");
        var defaults = Defaults();
        return new WatcherContingent(
            ModelCallsPerDay: NonNegative(section, "ModelCallsPerDay", defaults.ModelCallsPerDay),
            ModelCallsPerWeek: NonNegative(section, "ModelCallsPerWeek", defaults.ModelCallsPerWeek),
            ProposalsPerDay: NonNegative(section, "ProposalsPerDay", defaults.ProposalsPerDay),
            ProposalsPerWeek: NonNegative(section, "ProposalsPerWeek", defaults.ProposalsPerWeek),
            CommentsPerDay: NonNegative(section, "CommentsPerDay", defaults.CommentsPerDay),
            CommentsPerWeek: NonNegative(section, "CommentsPerWeek", defaults.CommentsPerWeek),
            TokensPerDay: NonNegative(section, "TokensPerDay", defaults.TokensPerDay),
            TokensPerWeek: NonNegative(section, "TokensPerWeek", defaults.TokensPerWeek));
    }

    private static int NonNegative(IConfiguration section, string key, int fallback)
        => Math.Max(0, section.GetValue<int?>(key) ?? fallback);

    private static long NonNegative(IConfiguration section, string key, long fallback)
        => Math.Max(0, section.GetValue<long?>(key) ?? fallback);
}

/// <summary>One recorded spend event. The ledger keeps them so day and week windows can be recomputed after a restart.</summary>
public sealed record WatcherSpendEntry(DateTime At, WatcherSpendKind Kind, long Tokens, string? CaseId = null);

/// <summary>Counts inside one rolling window, plus what remains of the budget.</summary>
public sealed record WatcherContingentUsage(
    int ModelCalls,
    int Proposals,
    int Comments,
    long Tokens);

/// <summary>Why an admission was refused. <see cref="None"/> means it was granted.</summary>
public enum WatcherContingentBlock
{
    None,
    ModelCallsPerDay,
    ModelCallsPerWeek,
    ProposalsPerDay,
    ProposalsPerWeek,
    CommentsPerDay,
    CommentsPerWeek,
    TokensPerDay,
    TokensPerWeek,
}

/// <summary>Admission verdict for one intended spend.</summary>
public sealed record WatcherContingentVerdict(bool Allowed, WatcherContingentBlock Block)
{
    public static readonly WatcherContingentVerdict Granted = new(true, WatcherContingentBlock.None);

    public static WatcherContingentVerdict Refused(WatcherContingentBlock block) => new(false, block);
}

/// <summary>
/// Pure contingent accounting over a spend history. When the contingent is used
/// up the Watcher keeps detecting and counting but stops calling models and
/// creating proposals, so the unanalysed backlog stays visible instead of being
/// silently dropped.
/// </summary>
public static class WatcherContingentLedger
{
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public static WatcherContingentUsage Usage(IReadOnlyList<WatcherSpendEntry> history, DateTime at, TimeSpan window)
    {
        var since = at - window;
        var inWindow = history.Where(entry => entry.At > since).ToList();
        return new WatcherContingentUsage(
            ModelCalls: inWindow.Count(entry => entry.Kind == WatcherSpendKind.ModelCall),
            Proposals: inWindow.Count(entry => entry.Kind == WatcherSpendKind.Proposal),
            Comments: inWindow.Count(entry => entry.Kind == WatcherSpendKind.Comment),
            Tokens: inWindow.Sum(entry => entry.Tokens));
    }

    /// <summary>
    /// Decides whether one more unit of <paramref name="kind"/> costing
    /// <paramref name="tokens"/> fits. Both the day and the week window must
    /// admit it; the narrower one is reported so the operator sees which budget
    /// bound the run.
    /// </summary>
    public static WatcherContingentVerdict Admit(
        IReadOnlyList<WatcherSpendEntry> history,
        WatcherContingent contingent,
        WatcherSpendKind kind,
        DateTime at,
        long tokens = 0)
    {
        var day = Usage(history, at, Day);
        var week = Usage(history, at, Week);

        var countBlock = kind switch
        {
            WatcherSpendKind.ModelCall when day.ModelCalls >= contingent.ModelCallsPerDay
                => WatcherContingentBlock.ModelCallsPerDay,
            WatcherSpendKind.ModelCall when week.ModelCalls >= contingent.ModelCallsPerWeek
                => WatcherContingentBlock.ModelCallsPerWeek,
            WatcherSpendKind.Proposal when day.Proposals >= contingent.ProposalsPerDay
                => WatcherContingentBlock.ProposalsPerDay,
            WatcherSpendKind.Proposal when week.Proposals >= contingent.ProposalsPerWeek
                => WatcherContingentBlock.ProposalsPerWeek,
            WatcherSpendKind.Comment when day.Comments >= contingent.CommentsPerDay
                => WatcherContingentBlock.CommentsPerDay,
            WatcherSpendKind.Comment when week.Comments >= contingent.CommentsPerWeek
                => WatcherContingentBlock.CommentsPerWeek,
            _ => WatcherContingentBlock.None,
        };
        if (countBlock != WatcherContingentBlock.None) return WatcherContingentVerdict.Refused(countBlock);

        // Token budgets bind only the spends that actually consume tokens. A
        // proposal drafted by deterministic code costs nothing and must not be
        // refused because an unrelated analysis exhausted the token window.
        if (tokens <= 0) return WatcherContingentVerdict.Granted;
        if (day.Tokens + tokens > contingent.TokensPerDay)
            return WatcherContingentVerdict.Refused(WatcherContingentBlock.TokensPerDay);
        if (week.Tokens + tokens > contingent.TokensPerWeek)
            return WatcherContingentVerdict.Refused(WatcherContingentBlock.TokensPerWeek);

        return WatcherContingentVerdict.Granted;
    }

    /// <summary>Drops entries older than the widest window so the persisted history stays bounded.</summary>
    public static IReadOnlyList<WatcherSpendEntry> Prune(IReadOnlyList<WatcherSpendEntry> history, DateTime at)
        => [.. history.Where(entry => entry.At > at - Week).OrderBy(entry => entry.At)];
}
