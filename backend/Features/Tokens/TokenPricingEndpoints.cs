using AgentStudio.Runner;
using AgentStudio.Bus;
using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Tokens;

public sealed record TokenPriceCalculationRequest(IReadOnlyList<TokenPriceCalculationItem> Items);
public sealed record TokenPriceCalculationItem(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    DateTime? RecordedAt = null,
    string? Label = null,
    decimal? RecordedCostUsd = null,
    bool? RecordedModelPriced = null);

public sealed record TokenPriceCalculationResult(
    string Model,
    string? Label,
    DateTime CalculatedAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    TokenCostEstimate Estimate,
    decimal? RecordedCostUsd,
    bool? RecordedModelPriced);

/// <summary>
/// One model id that is active in recorded usage without a historical price.
/// Diagnostic row for <c>GET /api/token-pricing/unpriced-models</c>.
/// </summary>
public sealed record UnpricedModelRow(string Model, int Calls, long TotalTokens, decimal RecordedCostUsd);

public sealed record UnpricedModelsResponse(IReadOnlyList<UnpricedModelRow> Models, string CheckedAt,
    DateTime WeekStartUtc, DateTime WeekEndUtc, string TimeZone);

public static class TokenPricingEndpoints
{
    public static void MapTokenPricingEndpoints(this WebApplication app)
    {
        var pricing = app.Services.GetRequiredService<ITokenPricing>();
        app.MapPost("/api/token-pricing/calculate", (TokenPriceCalculationRequest request) =>
        {
            if (request.Items.Count is < 1 or > 100)
                return Results.BadRequest(new { error = "Provide between 1 and 100 pricing items." });

            var rows = CalculateItems(pricing, request);
            return Results.Ok(new { items = rows, provider = "TokenEconomy" });
        });

        // Diagnostic: every model id active in recorded workspace usage with
        // at least one call lacking a historical price.
        app.MapGet("/api/token-pricing/unpriced-models",
            (HttpContext context, string? workspaceId, WorkspaceRegistry workspaces, WorkspaceSettingsService settings,
                ProjectRegistry registry, ProjectTokenLedger ledger, AgentMessageBusStore bus,
                TaskScannerService scanner, IConfiguration configuration) =>
            {
                var workspace = string.IsNullOrWhiteSpace(workspaceId)
                    ? workspaces.List().FirstOrDefault(item => item.IsDefault) ?? workspaces.List().FirstOrDefault()
                    : workspaces.Find(workspaceId);
                if (workspace is null) return Results.NotFound(new { error = "Unknown workspace." });
                var allProjects = registry.List().Where(project => !project.Archived
                    && string.Equals(project.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
                var visibleProjects = allProjects.Where(project => human is null
                    || ProjectAccessAuthorization.Allows(human.User, project.Id, registry)).ToList();
                if (human is not null && !ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)
                    && visibleProjects.Count == 0)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var now = DateTime.UtcNow;
                UsageCalendar calendar;
                try
                {
                    var calendarSettings = settings.Get(workspace.Id);
                    calendar = UsageCockpitProjection.Calendar(now, calendarSettings.UsageTimeZone,
                        calendarSettings.UsageWeekStart);
                }
                catch (Exception)
                {
                    calendar = UsageCockpitProjection.Calendar(now, null, null);
                }
                var inputs = UsageCostInputReader.Read(allProjects, visibleProjects, ledger, bus,
                    scanner, configuration, calendar.WeekStartUtc,
                    workspace.IsDefault && (human is null
                        || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)));
                return Results.Ok(new UnpricedModelsResponse(
                    SelectUnpricedModels(pricing, calendar, inputs), now.ToString("o"),
                    calendar.WeekStartUtc, calendar.WeekEndUtc, calendar.TimeZone));
            });
    }

    internal static IReadOnlyList<TokenPriceCalculationResult> CalculateItems(ITokenPricing pricing,TokenPriceCalculationRequest request)
    {
        return request.Items.Select(item =>
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
                pricing.Estimate(item.Model, item.InputTokens, item.OutputTokens,
                    item.CacheReadTokens, item.CacheWriteTokens, at),
                item.RecordedCostUsd, item.RecordedModelPriced);
        }).ToList();
    }

    /// <summary>Counts only unpriced receipts in the same calendar week as the cost marker.</summary>
    internal static IReadOnlyList<UnpricedModelRow> SelectUnpricedModels(ITokenPricing pricing,
        UsageCalendar calendar, IReadOnlyList<UsageCostInput> inputs)
    {
        var rows = new Dictionary<string, (int Calls, long Tokens)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inputs.SelectMany(input => input.Entries))
        {
            var at = UsageCockpitProjection.AsUtc(entry.Ts);
            if (at < calendar.WeekStartUtc || at >= calendar.WeekEndUtc || entry.TokenUsage is not { } usage)
                continue;
            bool priced;
            try
            {
                priced = pricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens, at).ModelKnown;
            }
            catch (Exception) { priced = false; }
            if (priced) continue;
            var model = ModelIdentity.Resolve(usage.Model)?.ModelId ?? usage.Model ?? "(unknown)";
            var current = rows.GetValueOrDefault(model);
            rows[model] = (current.Calls + 1, current.Tokens + usage.InputTokens + usage.OutputTokens
                + usage.CacheReadTokens + usage.CacheCreationTokens);
        }
        return rows.Select(pair => new UnpricedModelRow(pair.Key, pair.Value.Calls, pair.Value.Tokens, 0m))
            .OrderByDescending(row => row.TotalTokens).ToList();
    }
}
