namespace AgentStudio.Tokens;

public sealed record ProjectTokenUsageSummary
{
    public string Project { get; init; } = "";
    public bool HasData { get; init; }
    public long LifetimeTotalTokens { get; init; }
    public long LifetimeJobTokens { get; init; }
    public long LifetimeSupportingTokens { get; init; }
    public long LifetimeOrchestratorTokens { get; init; }
    public int LifetimeCalls { get; init; }
    public long Last24hTotalTokens { get; init; }
    public long Last24hJobTokens { get; init; }
    public long Last24hSupportingTokens { get; init; }
    public long Last24hOrchestratorTokens { get; init; }
    public int Last24hCalls { get; init; }
    public long Last7dTotalTokens { get; init; }
    public long Last7dJobTokens { get; init; }
    public long Last7dSupportingTokens { get; init; }
    public long Last7dOrchestratorTokens { get; init; }
    public int Last7dCalls { get; init; }
    public string? FirstActivity { get; init; }
    public string? LastActivity { get; init; }
    public string FetchedAt { get; init; } = "";
    public ProjectTokenDataFreshness Freshness { get; init; } = ProjectTokenDataFreshness.Empty;
    public string Disclaimer { get; init; } = "";
}

public sealed record ProjectTokenHeatmap
{
    public string Project { get; init; } = "";
    public IReadOnlyList<string> Days { get; init; } = [];
    public IReadOnlyList<ProjectTokenHeatmapJob> Jobs { get; init; } = [];
    public bool HasData { get; init; }
    public string FetchedAt { get; init; } = "";
    public ProjectTokenDataFreshness Freshness { get; init; } = ProjectTokenDataFreshness.Empty;
}

/// <summary>
/// Read-health metadata for project token surfaces. <see cref="AsOf"/> is the
/// newest usage timestamp successfully read, not the HTTP fetch time.
/// </summary>
public sealed record ProjectTokenDataFreshness
{
    public static ProjectTokenDataFreshness Empty { get; } = new();

    /// <summary><c>complete</c>, <c>partial</c>, or <c>unavailable</c>.</summary>
    public string Status { get; init; } = "complete";
    public string? AsOf { get; init; }
    public string? Warning { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>
    /// Read health of several project ledgers: unavailable only when all are,
    /// partial when any is not complete; warnings and sources are de-duplicated.
    /// </summary>
    public static ProjectTokenDataFreshness Combine(IReadOnlyList<ProjectTokenDataFreshness> projects)
    {
        if (projects.Count == 0) return Empty;
        var status = projects.All(p => p.Status == "unavailable")
            ? "unavailable"
            : projects.Any(p => p.Status != "complete") ? "partial" : "complete";
        var warnings = projects
            .Select(p => p.Warning)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ProjectTokenDataFreshness
        {
            Status = status,
            AsOf = projects
                .Select(p => p.AsOf)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Max(StringComparer.Ordinal),
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            Sources = projects.SelectMany(p => p.Sources).Distinct(StringComparer.Ordinal).ToList(),
        };
    }
}

public sealed record ProjectTokenHeatmapJob
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? State { get; init; }
    public string Category { get; init; } = ProjectTokenCategory.Job;
    public long Total { get; init; }
    public int Calls { get; init; }
    public string? LastActivity { get; init; }
    public IReadOnlyList<ProjectTokenHeatmapCell> Cells { get; init; } = [];
}

public sealed record ProjectTokenHeatmapCell
{
    public string Day { get; init; } = "";
    public long Total { get; init; }
}

public sealed record ProjectExpensiveJob
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? State { get; init; }
    public string Category { get; init; } = ProjectTokenCategory.Job;
    public long TotalTokens { get; init; }
    public int Calls { get; init; }
    public string? LastActivity { get; init; }
    public string? LastModel { get; init; }
}

public sealed record ProjectJobTokenDetail
{
    public string Project { get; init; } = "";
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? State { get; init; }
    public string Category { get; init; } = ProjectTokenCategory.Job;
    public long TotalTokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public int Calls { get; init; }
    public string? FirstActivity { get; init; }
    public string? LastActivity { get; init; }
    public string? LastModel { get; init; }
    public IReadOnlyList<ProjectJobTokenRun> Runs { get; init; } = [];
    public string FetchedAt { get; init; } = "";
}

public sealed record ProjectJobTokenRun
{
    public int Index { get; init; }
    public string Ts { get; init; } = "";
    public string? Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long Total { get; init; }
    public long? DeltaVsPrev { get; init; }
    public string? Topic { get; init; }
    public string? Summary { get; init; }
}
