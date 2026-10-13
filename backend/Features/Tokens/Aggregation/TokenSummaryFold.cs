namespace AgentStudio.Tokens;

/// <summary>
/// Pure folds from a priced usage ledger to the per-project
/// <see cref="TokenSummary"/>, and from per-project summaries to the
/// workspace <see cref="TokenSummaryAggregate"/>. Both levels fold models
/// through the one <see cref="ModelUsageFold"/>.
/// </summary>
public static class TokenSummaryFold
{
    public static TokenSummary ForProject(string projectName, IReadOnlyList<PricedTokenEntry> ledger)
    {
        var models = new ModelUsageFold();
        long totalInput = 0, totalOutput = 0, totalCacheRead = 0, totalCacheCreate = 0;
        var callCount = 0;
        DateTime? firstAt = null;
        DateTime? lastAt = null;

        foreach (var priced in ledger)
        {
            var usage = priced.Usage;
            if (usage is null || priced.Cost is null) continue;
            callCount++;
            totalInput += usage.InputTokens;
            totalOutput += usage.OutputTokens;
            totalCacheRead += usage.CacheReadTokens;
            totalCacheCreate += usage.CacheCreationTokens;

            var ts = priced.Entry.Ts.ToUniversalTime();
            if (firstAt == null || ts < firstAt) firstAt = ts;
            if (lastAt == null || ts > lastAt) lastAt = ts;

            models.AddCall(usage, priced.Cost);
        }

        var byModel = models.Rows();
        return new TokenSummary(
            Project: projectName,
            OrchestratorEntries: ledger.Count,
            OrchestratorLlmCalls: callCount,
            TotalInputTokens: totalInput,
            TotalOutputTokens: totalOutput,
            TotalCacheReadTokens: totalCacheRead,
            TotalCacheCreationTokens: totalCacheCreate,
            EstimatedApiCostUsd: byModel.Aggregate(0m, (sum, model) => sum + model.EstimatedApiCostUsd),
            AllModelsPriced: byModel.Count > 0 && byModel.All(model => model.ModelPriced),
            UnknownModelCount: byModel.Count(model => !model.ModelInCatalog),
            ByModel: byModel,
            FirstActivity: firstAt?.ToString("o"),
            LastActivity: lastAt?.ToString("o"),
            Disclaimer: TokenCostDisclaimer.Text);
    }

    public static TokenSummaryAggregate ForWorkspace(IReadOnlyList<(string Name, TokenSummary Summary)> projectSummaries)
    {
        var perProject = new List<TokenSummaryByProject>();
        var models = new ModelUsageFold();
        long totalInput = 0, totalOutput = 0, totalCacheRead = 0, totalCacheCreate = 0;
        int totalEntries = 0, totalCalls = 0;
        decimal grandTotal = 0;
        var allPriced = true;
        DateTime? firstAt = null;
        DateTime? lastAt = null;

        foreach (var (name, summary) in projectSummaries)
        {
            totalEntries += summary.OrchestratorEntries;
            totalCalls += summary.OrchestratorLlmCalls;
            if (summary.FirstActivity is { } fa && DateTime.TryParse(fa, out var faTs))
                if (firstAt == null || faTs < firstAt) firstAt = faTs;
            if (summary.LastActivity is { } la && DateTime.TryParse(la, out var laTs))
                if (lastAt == null || laTs > lastAt) lastAt = laTs;
            totalInput += summary.TotalInputTokens;
            totalOutput += summary.TotalOutputTokens;
            totalCacheRead += summary.TotalCacheReadTokens;
            totalCacheCreate += summary.TotalCacheCreationTokens;
            grandTotal += summary.EstimatedApiCostUsd;
            if (summary.OrchestratorLlmCalls > 0 && !summary.AllModelsPriced) allPriced = false;

            perProject.Add(new TokenSummaryByProject(
                Project: name,
                OrchestratorLlmCalls: summary.OrchestratorLlmCalls,
                InputTokens: summary.TotalInputTokens,
                OutputTokens: summary.TotalOutputTokens,
                CacheReadTokens: summary.TotalCacheReadTokens,
                CacheCreationTokens: summary.TotalCacheCreationTokens,
                EstimatedApiCostUsd: summary.EstimatedApiCostUsd));

            foreach (var row in summary.ByModel)
                models.AddRow(row);
        }

        return new TokenSummaryAggregate(
            Projects: projectSummaries.Count,
            OrchestratorEntries: totalEntries,
            OrchestratorLlmCalls: totalCalls,
            TotalInputTokens: totalInput,
            TotalOutputTokens: totalOutput,
            TotalCacheReadTokens: totalCacheRead,
            TotalCacheCreationTokens: totalCacheCreate,
            EstimatedApiCostUsd: grandTotal,
            // Without a single recorded call "all priced" carries no information.
            AllModelsPriced: totalCalls > 0 && allPriced,
            ByModel: models.Rows(),
            ByProject: perProject
                .OrderByDescending(p => p.InputTokens + p.OutputTokens)
                .ToList(),
            FetchedAt: DateTime.UtcNow.ToString("o"),
            FirstActivity: firstAt?.ToString("o"),
            LastActivity: lastAt?.ToString("o"),
            Disclaimer: TokenCostDisclaimer.Text);
    }
}
