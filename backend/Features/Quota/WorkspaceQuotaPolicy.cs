namespace AgentStudio.Quota;

/// <summary>Pure week and admission decisions shared by every executor boundary.</summary>
public static class WorkspaceQuotaPolicy
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Blocked = "blocked";
    public const string HostingFull = "hosting-full";

    public static QuotaOverrideAudit OverrideEntry(TimeProvider clock, string workspaceId, string? projectId,
        string actor, string reason, DateTime expiresAt)
        => new(clock.GetUtcNow(), workspaceId, projectId, actor, reason, expiresAt);

    /// <summary>
    /// Ledger rows are normalized: input is uncached input and cached reads are
    /// their own column for every provider, so every column counts once.
    /// </summary>
    public static long ModelTokens(long input, long output, long cacheRead, long cacheWrite)
        => Math.Max(0, input) + Math.Max(0, output) + Math.Max(0, cacheRead) + Math.Max(0, cacheWrite);

    public static (DateTime Start, DateTime End) Week(TimeProvider clock)
    {
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return (start, start.AddDays(7));
    }

    /// <summary>
    /// Tokens and core-hours decide warning and blocked. Full hosting slots only
    /// stop deploys, so they have their own state below blocked.
    /// </summary>
    public static string Status(QuotaLimits? limits, QuotaUsage usage)
    {
        if (limits is null) return Ok;
        var highest = Math.Max(
            limits.ModelTokensPerWeek is { } tokens ? Ratio(usage.ModelTokens, tokens) : 0,
            limits.ComputeCoreHoursPerWeek is { } hours ? Ratio(usage.ComputeCoreHours, hours) : 0);
        if (highest >= 1) return Blocked;
        if (limits.HostingSlots is { } slots && usage.HostingSlots >= slots) return HostingFull;
        return highest >= .8m ? Warning : Ok;
    }

    public static string? Message(string status, QuotaLimits? limits) => status switch
    {
        Blocked => "Weekly quota reached. New work is paused until Monday 00:00 UTC or an owner override.",
        HostingFull when limits?.HostingSlots is 0 => "No hosting slots are configured; new deploys are refused.",
        HostingFull => $"All {limits?.HostingSlots} hosting slots are in use; new deploys wait until an environment stops.",
        Warning => "Quota is at least 80% used.",
        _ => null,
    };

    public static bool Allows(QuotaLimits? limits, QuotaUsage usage, bool hosting)
        => limits is null
           || (limits.ModelTokensPerWeek is not { } tokens || usage.ModelTokens < tokens)
           && (limits.ComputeCoreHoursPerWeek is not { } hours || usage.ComputeCoreHours < hours)
           && (!hosting || limits.HostingSlots is not { } slots || usage.HostingSlots < slots);

    public static decimal CoreHours(DateTime startedAt, DateTime? completedAt, int? grantedCores,
        DateTime weekStart, DateTime weekEnd, TimeProvider clock)
    {
        var from = startedAt > weekStart ? startedAt : weekStart;
        var end = completedAt ?? clock.GetUtcNow().UtcDateTime;
        var to = end < weekEnd ? end : weekEnd;
        return to <= from ? 0 : (decimal)(to - from).TotalHours * Math.Max(1, grantedCores ?? 1);
    }

    /// <summary>A claimed deploy job without a report counts until its five-minute lease ends.</summary>
    public static DateTime DeployEnd(DateTime startedAt, DateTime? completedAt, TimeProvider clock)
    {
        if (completedAt is { } done) return done;
        var leaseEnd = startedAt.AddMinutes(5);
        var now = clock.GetUtcNow().UtcDateTime;
        return now < leaseEnd ? now : leaseEnd;
    }

    /// <summary>Longest an open step meters; bounds a step whose end was never written.</summary>
    public static readonly TimeSpan OpenStepBudget = TimeSpan.FromHours(12);

    /// <summary>
    /// End of a pipeline step execution for metering. An open step counts until
    /// now only while it belongs to a live attempt (the current, incomplete
    /// attempt of a card in a working lane). Otherwise it ends when its attempt
    /// ended, or at the last write of the record. No open step meters past
    /// <see cref="OpenStepBudget"/> or past now.
    /// </summary>
    public static DateTime StepEnd(DateTime startedAt, DateTime? completedAt, bool live,
        DateTime? attemptEnd, DateTime lastWrite, TimeProvider clock)
    {
        if (completedAt is { } done) return done;
        var now = clock.GetUtcNow().UtcDateTime;
        var end = live ? now : attemptEnd ?? lastWrite;
        var budget = startedAt + OpenStepBudget;
        if (end > budget) end = budget;
        return end < now ? end : now;
    }

    private static decimal Ratio(decimal used, decimal limit) => limit == 0 ? 1 : used / limit;
}
