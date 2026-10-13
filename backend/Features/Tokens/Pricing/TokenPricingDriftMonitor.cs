using System.Collections.Concurrent;

namespace AgentStudio.Tokens;

/// <summary>
/// Emits one warning per project and active model when recorded usage names a
/// model that the exactly pinned TokenEconomy catalog does not contain.
/// </summary>
public sealed class TokenPricingDriftMonitor
{
    private readonly ILogger<TokenPricingDriftMonitor> _logger;
    private readonly ConcurrentDictionary<string, byte> _reported =
        new(StringComparer.OrdinalIgnoreCase);

    public TokenPricingDriftMonitor(ILogger<TokenPricingDriftMonitor> logger)
    {
        _logger = logger;
    }

    public void Observe(TokenSummary summary)
    {
        foreach (var model in summary.ByModel.Where(IsActiveUnknownModel))
        {
            var key = $"{summary.Project}\n{model.ModelId}";
            if (!_reported.TryAdd(key, 0)) continue;

            _logger.LogWarning(
                "Token price catalog drift: active model {Model} in project {Project} is absent from the pinned TokenEconomy catalog; {Calls} recorded calls have no catalog price",
                model.ModelId,
                summary.Project,
                model.Calls);
        }
    }

    private static bool IsActiveUnknownModel(TokenSummaryByModel model)
        => !model.ModelInCatalog
           && model.InputTokens + model.OutputTokens
               + model.CacheReadTokens + model.CacheCreationTokens > 0;
}
