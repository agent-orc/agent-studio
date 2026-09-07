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
    long BillableInputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    TokenCostEstimate Estimate);

public static class TokenPricingEndpoints
{
    public static void MapTokenPricingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/token-pricing");

        group.MapPost("/calculate", (TokenPriceCalculationRequest request) =>
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
                    TokenPricing.BillableInputTokens(item.Model, item.InputTokens, item.CacheReadTokens),
                    Math.Max(0, item.OutputTokens),
                    Math.Max(0, item.CacheReadTokens),
                    Math.Max(0, item.CacheWriteTokens),
                    TokenPricing.Estimate(item.Model, item.InputTokens, item.OutputTokens,
                        item.CacheReadTokens, item.CacheWriteTokens, at));
            }).ToList();
            return Results.Ok(new { items = rows, provider = "TokenEconomy" });
        });

        group.MapGet("/diagnostics",
            (HttpContext context, TaskScannerService scanner, ITokenAggregator tokens,
                AgentStudio.Registry.ProjectRegistry registry) =>
            {
                var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
                var projects = scanner.GetWatchPaths(includeArchived: true)
                    .Where(project => human is null
                                      || ProjectAccessAuthorization.Allows(human.User, project.Name, registry))
                    .Select(project => (project.Name, project.Path))
                    .ToList();

                var includeWorkspaceAdHoc = human is null
                    || human.User.Role == StudioRoles.Owner
                    || human.User.Projects.Count == 0;
                var summaries = projects
                    .Select(project => (project.Name, tokens.LifetimeSummary(project.Name, project.Path)))
                    .ToList();
                var diagnostics = TokenPricingDiagnostics.Build(
                    TokenSummaryService.AggregateSummaries(summaries),
                    includeWorkspaceAdHoc ? tokens.AdHocAggregate() : null);
                return Results.Ok(diagnostics);
            });
    }
}
