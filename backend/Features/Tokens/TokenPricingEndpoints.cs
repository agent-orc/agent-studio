using AgentStudio.Runner;

namespace AgentStudio.Tokens;

public sealed record TokenPriceCalculationRequest(IReadOnlyList<TokenPriceCalculationItem> Items);
public sealed record TokenPriceCalculationItem(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    DateTime? RecordedAt = null,
    string? Label = null);

public sealed record TokenPriceCalculationResult(
    string Model,
    string? Label,
    DateTime CalculatedAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    TokenCostEstimate Estimate);

/// <summary>
/// One model id that is active in recorded usage but absent from the pinned
/// TokenEconomy catalog. Diagnostic row for <c>GET /api/token-pricing/unpriced-models</c>.
/// </summary>
public sealed record UnpricedModelRow(string Model, int Calls, long TotalTokens, decimal RecordedCostUsd);

public sealed record UnpricedModelsResponse(IReadOnlyList<UnpricedModelRow> Models, string CheckedAt);

public static class TokenPricingEndpoints
{
    public static void MapTokenPricingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/token-pricing/calculate", (TokenPriceCalculationRequest request) =>
        {
            if (request.Items.Count is < 1 or > 100)
                return Results.BadRequest(new { error = "Provide between 1 and 100 pricing items." });

            var rows = request.Items.Select(item =>
            {
                var at = (item.RecordedAt ?? DateTime.UtcNow).ToUniversalTime();
                return new TokenPriceCalculationResult(
                    item.Model,
                    item.Label,
                    at,
                    Math.Max(0, item.InputTokens),
                    Math.Max(0, item.OutputTokens),
                    Math.Max(0, item.CacheReadTokens),
                    Math.Max(0, item.CacheWriteTokens),
                    TokenPricing.Estimate(item.Model, item.InputTokens, item.OutputTokens,
                        item.CacheReadTokens, item.CacheWriteTokens, at));
            }).ToList();
            return Results.Ok(new { items = rows, provider = "TokenEconomy" });
        });

        // Diagnostic: every model id active in recorded workspace usage that
        // the pinned TokenEconomy catalog does not recognize at all, so a
        // catalog gap is visible in an endpoint instead of a silent "Unknown"
        // cost on every surface that renders the model.
        app.MapGet("/api/token-pricing/unpriced-models",
            (HttpContext context, TaskScannerService scanner, ITokenAggregator tokens,
                AgentStudio.Registry.ProjectRegistry registry) =>
            {
                var projects = scanner.GetWatchPaths()
                    .Where(project => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
                                      || ProjectAccessAuthorization.Allows(human.User, project.Name, registry))
                    .Select(e => (e.Name, e.Path))
                    .ToList();
                var aggregate = tokens.WorkspaceAggregate(projects);
                return Results.Ok(new UnpricedModelsResponse(
                    SelectUnpricedModels(aggregate),
                    DateTime.UtcNow.ToString("o")));
            });
    }

    /// <summary>Pure projection so the selection rule is unit-testable without standing up the endpoint.</summary>
    internal static IReadOnlyList<UnpricedModelRow> SelectUnpricedModels(TokenSummaryAggregate aggregate)
        => aggregate.ByModel
            .Where(m => !m.ModelInCatalog)
            .Select(m => new UnpricedModelRow(
                m.Model,
                m.Calls,
                m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheCreationTokens,
                m.EstimatedApiCostUsd))
            .OrderByDescending(m => m.TotalTokens)
            .ToList();
}
