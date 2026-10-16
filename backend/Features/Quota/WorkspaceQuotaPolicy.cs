namespace AgentStudio.Quota;

/// <summary>Pure week and admission decisions shared by every executor boundary.</summary>
public static class WorkspaceQuotaPolicy
{
    public static QuotaOverrideAudit OverrideEntry(TimeProvider clock, string workspaceId, string? projectId,
        string actor, string reason, DateTime expiresAt)
        => new(clock.GetUtcNow(), workspaceId, projectId, actor, reason, expiresAt);
    public static long ModelTokens(long input, long output, long cacheRead, long cacheWrite,
        bool? inputIncludesCached)
        => Math.Max(0, input) + Math.Max(0, output) + Math.Max(0, cacheWrite)
            + (inputIncludesCached == true ? 0 : Math.Max(0, cacheRead));
    public static (DateTime Start, DateTime End) Week(TimeProvider clock)
    {
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return (start, start.AddDays(7));
    }

    public static string Status(QuotaLimits? limits, QuotaUsage usage)
    {
        if (limits is null) return "ok";
        var ratios = new[]
        {
            limits.ModelTokensPerWeek is { } tokens ? Ratio(usage.ModelTokens, tokens) : 0,
            limits.ComputeCoreHoursPerWeek is { } hours ? Ratio(usage.ComputeCoreHours, hours) : 0,
            limits.HostingSlots is { } slots ? Ratio(usage.HostingSlots, slots) : 0
        };
        var highest = ratios.Max();
        return highest >= 1 ? "blocked" : highest >= .8m ? "warning" : "ok";
    }

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

    private static decimal Ratio(decimal used, decimal limit) => limit == 0 ? 1 : used / limit;
}
