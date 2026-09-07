using System.Globalization;

namespace AgentStudio.Watcher;

/// <summary>What a Watcher action wants to spend from the contingent.</summary>
public static class WatcherContingentKinds
{
    public const string ModelCall = "model-call";
    public const string Proposal = "proposal";
    public const string Comment = "comment";

    public static readonly string[] All = [ModelCall, Proposal, Comment];
}

/// <summary>
/// One consumption event. The ledger is append-only, so the usage of a past
/// day can never be rewritten by a later configuration change.
/// </summary>
public sealed record WatcherContingentEntry
{
    public required DateTime AtUtc { get; init; }
    public required string Kind { get; init; }
    public int Count { get; init; } = 1;
    public long Tokens { get; init; }
    /// <summary>Null when the price catalogue has no entry for the model.</summary>
    public decimal? CostUsd { get; init; }
    public bool PriceKnown { get; init; }
    public string? CaseId { get; init; }
    public string? Model { get; init; }
}

/// <summary>
/// The dedicated Watcher budget of dossier section 10.4, per day and per week,
/// in tokens and in counts. Null means the operator declared no ceiling for
/// that dimension; zero means the dimension is closed, which is how the
/// acceptance case "contingent set to zero" is expressed.
/// </summary>
public sealed record WatcherContingentLimits
{
    public int? ModelCallsPerDay { get; init; }
    public int? ModelCallsPerWeek { get; init; }
    public long? TokensPerDay { get; init; }
    public long? TokensPerWeek { get; init; }
    public int? ProposalsPerDay { get; init; }
    public int? ProposalsPerWeek { get; init; }
    public int? CommentsPerDay { get; init; }
    public int? CommentsPerWeek { get; init; }

    /// <summary>
    /// Defaults sized for a five-minute sweep: enough to work through a normal
    /// evening of findings, small enough that a detector storm cannot become a
    /// board full of cards.
    /// </summary>
    public static WatcherContingentLimits Default { get; } = new()
    {
        ModelCallsPerDay = 20,
        ModelCallsPerWeek = 80,
        TokensPerDay = 500_000,
        TokensPerWeek = 2_000_000,
        ProposalsPerDay = 5,
        ProposalsPerWeek = 20,
        CommentsPerDay = 20,
        CommentsPerWeek = 80,
    };

    /// <summary>Everything closed. Detection continues, spending does not.</summary>
    public static WatcherContingentLimits Zero { get; } = new()
    {
        ModelCallsPerDay = 0,
        ModelCallsPerWeek = 0,
        TokensPerDay = 0,
        TokensPerWeek = 0,
        ProposalsPerDay = 0,
        ProposalsPerWeek = 0,
        CommentsPerDay = 0,
        CommentsPerWeek = 0,
    };
}

/// <summary>Consumption already booked in the current day and week windows.</summary>
public sealed record WatcherContingentUsage
{
    public int ModelCallsDay { get; init; }
    public int ModelCallsWeek { get; init; }
    public long TokensDay { get; init; }
    public long TokensWeek { get; init; }
    public int ProposalsDay { get; init; }
    public int ProposalsWeek { get; init; }
    public int CommentsDay { get; init; }
    public int CommentsWeek { get; init; }
    public decimal CostUsdDay { get; init; }
    public decimal CostUsdWeek { get; init; }
    /// <summary>Model calls in the window whose price the catalogue could not resolve.</summary>
    public int UnpricedCallsDay { get; init; }
}

/// <summary>An admission answer with the exact dimension that closed the door.</summary>
public sealed record WatcherContingentVerdict(bool Allowed, string? Reason)
{
    public static readonly WatcherContingentVerdict Ok = new(true, null);

    public static WatcherContingentVerdict Denied(string dimension, long used, long limit) =>
        new(false, $"The Watcher contingent for {dimension} is exhausted ({used}/{limit}).");
}

/// <summary>
/// Pure admission policy for the contingent. Exhaustion stops model calls and
/// proposals; it never stops detection, so the unanalysed backlog stays
/// countable and visible.
/// </summary>
public static class WatcherContingentPolicy
{
    /// <summary>Start of the UTC day the instant falls in.</summary>
    public static DateTime DayStart(DateTime nowUtc) =>
        DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);

    /// <summary>Start of the ISO week (Monday) the instant falls in.</summary>
    public static DateTime WeekStart(DateTime nowUtc)
    {
        var day = DayStart(nowUtc);
        var offset = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-offset);
    }

    /// <summary>Fold the ledger into the current day and week windows.</summary>
    public static WatcherContingentUsage Usage(IEnumerable<WatcherContingentEntry> ledger, DateTime nowUtc)
    {
        var dayStart = DayStart(nowUtc);
        var weekStart = WeekStart(nowUtc);
        var usage = new WatcherContingentUsage();
        foreach (var entry in ledger)
        {
            var inWeek = entry.AtUtc >= weekStart;
            if (!inWeek) continue;
            var inDay = entry.AtUtc >= dayStart;

            usage = entry.Kind switch
            {
                WatcherContingentKinds.ModelCall => usage with
                {
                    ModelCallsWeek = usage.ModelCallsWeek + entry.Count,
                    ModelCallsDay = inDay ? usage.ModelCallsDay + entry.Count : usage.ModelCallsDay,
                    TokensWeek = usage.TokensWeek + entry.Tokens,
                    TokensDay = inDay ? usage.TokensDay + entry.Tokens : usage.TokensDay,
                    CostUsdWeek = usage.CostUsdWeek + (entry.CostUsd ?? 0m),
                    CostUsdDay = inDay ? usage.CostUsdDay + (entry.CostUsd ?? 0m) : usage.CostUsdDay,
                    UnpricedCallsDay = inDay && !entry.PriceKnown
                        ? usage.UnpricedCallsDay + entry.Count
                        : usage.UnpricedCallsDay,
                },
                WatcherContingentKinds.Proposal => usage with
                {
                    ProposalsWeek = usage.ProposalsWeek + entry.Count,
                    ProposalsDay = inDay ? usage.ProposalsDay + entry.Count : usage.ProposalsDay,
                },
                WatcherContingentKinds.Comment => usage with
                {
                    CommentsWeek = usage.CommentsWeek + entry.Count,
                    CommentsDay = inDay ? usage.CommentsDay + entry.Count : usage.CommentsDay,
                },
                _ => usage,
            };
        }

        return usage;
    }

    /// <summary>
    /// May the Watcher spend one unit of <paramref name="kind"/> right now?
    /// Token ceilings are checked against the estimate the caller declares
    /// before the call, so an unbounded prompt cannot slip past the budget.
    /// </summary>
    public static WatcherContingentVerdict Admit(
        string kind,
        WatcherContingentLimits limits,
        WatcherContingentUsage usage,
        long estimatedTokens = 0)
    {
        switch (kind)
        {
            case WatcherContingentKinds.ModelCall:
                return FirstDenial(
                    Check("model calls per day", usage.ModelCallsDay, 1, limits.ModelCallsPerDay),
                    Check("model calls per week", usage.ModelCallsWeek, 1, limits.ModelCallsPerWeek),
                    Check("tokens per day", usage.TokensDay, estimatedTokens, limits.TokensPerDay),
                    Check("tokens per week", usage.TokensWeek, estimatedTokens, limits.TokensPerWeek));
            case WatcherContingentKinds.Proposal:
                return FirstDenial(
                    Check("proposals per day", usage.ProposalsDay, 1, limits.ProposalsPerDay),
                    Check("proposals per week", usage.ProposalsWeek, 1, limits.ProposalsPerWeek));
            case WatcherContingentKinds.Comment:
                return FirstDenial(
                    Check("comments per day", usage.CommentsDay, 1, limits.CommentsPerDay),
                    Check("comments per week", usage.CommentsWeek, 1, limits.CommentsPerWeek));
            default:
                return new WatcherContingentVerdict(false, $"Unknown contingent kind '{kind}'.");
        }
    }

    private static WatcherContingentVerdict Check(string dimension, long used, long wanted, long? limit)
    {
        if (limit is null) return WatcherContingentVerdict.Ok;
        return used + wanted > limit.Value
            ? WatcherContingentVerdict.Denied(dimension, used, limit.Value)
            : WatcherContingentVerdict.Ok;
    }

    private static WatcherContingentVerdict FirstDenial(params WatcherContingentVerdict[] verdicts)
        => verdicts.FirstOrDefault(verdict => !verdict.Allowed) ?? WatcherContingentVerdict.Ok;
}

/// <summary>
/// What the Workspace CLI Management page shows next to the quota strips: the
/// budget, what has been spent, and how many cases are waiting because the
/// budget ran out.
/// </summary>
public sealed record WatcherContingentSnapshot
{
    public required WatcherContingentLimits Limits { get; init; }
    public required WatcherContingentUsage Usage { get; init; }
    public required DateTime DayStartUtc { get; init; }
    public required DateTime WeekStartUtc { get; init; }
    /// <summary>Dimensions that currently refuse a new unit, named for the UI.</summary>
    public IReadOnlyList<string> ExhaustedDimensions { get; init; } = [];
    /// <summary>Cases counted but not analysed because the budget ran out.</summary>
    public int BacklogCases { get; init; }
    public bool ProposalsBlocked { get; init; }
    public bool ModelCallsBlocked { get; init; }

    /// <summary>Cost of the day so far, or null when a call in the window had no price.</summary>
    public string CostUsdDayDisplay => Usage.UnpricedCallsDay > 0
        ? "unknown"
        : Usage.CostUsdDay.ToString("0.####", CultureInfo.InvariantCulture);
}
