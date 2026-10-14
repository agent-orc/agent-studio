namespace AgentStudio.Tokens;

/// <summary>
/// One usage ledger entry together with its historical list-price estimate,
/// priced once at the entry's own timestamp. An entry without token usage
/// carries no estimate. Aggregation folds read these and never price.
/// </summary>
public sealed record PricedTokenEntry(OrchestratorLogEntry Entry, TokenCostEstimate? Cost)
{
    public OrchestratorTokenUsage? Usage => Entry.TokenUsage;
}
