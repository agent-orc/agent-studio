namespace AgentStudio.Tokens;

/// <summary>Token and list-price totals of one timeline row (cell, project, or model).</summary>
internal class TimelineTotals
{
    public int Calls;
    public long Input;
    public long Output;
    public long CacheRead;
    public long CacheWrite;
    public decimal? Dollars;
    public bool HasPricedCall;
    public bool HasUnpricedCall;

    public long Total => Input + Output + CacheRead + CacheWrite;

    /// <summary>A row is priced only when it has priced calls and no unpriced one.</summary>
    public bool AllModelsPriced => HasPricedCall && !HasUnpricedCall;

    public void Add(OrchestratorTokenUsage usage, TokenCostEstimate cost)
    {
        Calls++;
        Input += usage.InputTokens;
        Output += usage.OutputTokens;
        CacheRead += usage.CacheReadTokens;
        CacheWrite += usage.CacheCreationTokens;
        if (cost.ModelKnown)
        {
            Dollars = (Dollars ?? 0m) + cost.Total;
            HasPricedCall = true;
        }
        else
        {
            HasUnpricedCall = true;
        }
    }
}

/// <summary>Totals plus the Agent / Supporting / Orchestrator split.</summary>
internal class CategorizedTimelineTotals : TimelineTotals
{
    public long AgentTokens;
    public long SupportingTokens;
    public long OrchestratorTokens;

    public void AddCategory(string category, long amount)
    {
        switch (category)
        {
            case ProjectTokenCategory.Job: AgentTokens += amount; break;
            case ProjectTokenCategory.Supporting: SupportingTokens += amount; break;
            case ProjectTokenCategory.Orchestrator: OrchestratorTokens += amount; break;
        }
    }
}

/// <summary>One (project, time bucket) cell.</summary>
internal sealed class TimelineCellTotals(string project, DateTime start, DateTime end) : CategorizedTimelineTotals
{
    public string Project { get; } = project;
    public DateTime BucketStart { get; } = start;
    public DateTime BucketEnd { get; } = end;
}

/// <summary>One project's window totals with its peak bucket and executing hosts.</summary>
internal sealed class TimelineProjectTotals(string project) : CategorizedTimelineTotals
{
    public string Project { get; } = project;
    public Dictionary<string, TimelineHostTotals> Hosts { get; } = new(StringComparer.Ordinal);
    public DateTime? PeakBucketStart;
    public long PeakBucketTotal;
    public DateTime? LastActivity;

    public void AddHost(string host, long amount)
    {
        if (!Hosts.TryGetValue(host, out var total))
        {
            total = new TimelineHostTotals(host);
            Hosts[host] = total;
        }
        total.Calls++;
        total.Total += amount;
    }
}

/// <summary>One (project, model id, executing host) row (AGT-2986).</summary>
internal sealed class TimelineModelTotals(string project, string model, string host) : TimelineTotals
{
    public string Project { get; } = project;
    public string Model { get; } = model;
    public string Host { get; } = host;
    public HashSet<string> CliTypes { get; } = new(StringComparer.Ordinal);
}

internal sealed class TimelineHostTotals(string host)
{
    public string Host { get; } = host;
    public int Calls;
    public long Total;
}
