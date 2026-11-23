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
    bool? RecordedModelPriced = null,
    long? PromptTokens = null);

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

        app.MapPost("/api/token-pricing/what-if", (RoutingWhatIfRequest request, HttpContext context,
            WorkspaceRegistry workspaces, ProjectRegistry registry, ProjectTokenLedger ledger,
            AgentMessageBusStore bus, TaskScannerService scanner, IConfiguration configuration,
            AgentStudio.Pipeline.ModelRoutingPolicyRegistry policy) =>
        {
            if (!RoutingWhatIf.Valid(request))
                return Results.BadRequest(new { error = "Choose UTC timestamps spanning at most 93 days, a consumer (coding, review-aspect, one-shot), and an alternative model." });
            var workspace = string.IsNullOrWhiteSpace(request.WorkspaceId)
                ? workspaces.List().FirstOrDefault(item => item.IsDefault) ?? workspaces.List().FirstOrDefault()
                : workspaces.Find(request.WorkspaceId);
            if (workspace is null) return Results.NotFound(new { error = "Unknown workspace." });
            var all = registry.List().Where(project => !project.Archived
                && string.Equals(project.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
            var visible = all.Where(project => human is null
                || ProjectAccessAuthorization.Allows(human.User, project.Id, registry)).ToList();
            if (human is not null && !ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)
                && visible.Count == 0) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var includeUnassigned = workspace.IsDefault && (human is null
                || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User));
            var inputs = UsageCostInputReader.Read(all, visible, ledger, bus, scanner, configuration,
                request.From.ToUniversalTime(), includeUnassigned);
            var cards = new Dictionary<string, TaskDetail?>();
            string? EconomyModel(OrchestratorLogEntry entry)
            {
                if (entry.JobId is not { } jobId) return null;
                if (!cards.TryGetValue(jobId, out var card)) cards[jobId] = card = scanner.GetJobDetail(jobId);
                return RoutingWhatIf.EconomyModel(policy, entry, card, request.EconomyMode!.Value);
            }
            var result = RoutingWhatIf.Calculate(pricing, request, inputs,
                request.EconomyMode.HasValue ? EconomyModel : null);
            var floorNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in inputs.SelectMany(input => input.Entries).Where(entry => entry.TokenUsage is not null
                && UsageCockpitProjection.AsUtc(entry.Ts) >= request.From.ToUniversalTime()
                && UsageCockpitProjection.AsUtc(entry.Ts) < request.To.ToUniversalTime()
                && RoutingWhatIf.Consumer(entry) == request.Consumer))
            {
                if (entry.JobId is not { } jobId) { floorNames.Add("unknown (no card)"); continue; }
                if (!cards.TryGetValue(jobId, out var card)) cards[jobId] = card = scanner.GetJobDetail(jobId);
                floorNames.Add(card is null ? "unknown (missing card)"
                    : policy.CorrectnessFloor(card.Info.TaskType, card.Info.Title, card.PromptMarkdown)?.Id ?? "no hard tier floor");
            }
            return Results.Ok(result with { CorrectnessFloor = result.CorrectnessFloor
                + " Current card floors: " + (floorNames.Count == 0 ? "no matching receipts" : string.Join(", ", floorNames.Order())) + "." });
        });

        app.MapGet("/api/token-pricing/route-models", () => Results.Ok(new
        {
            models = ModelMetadataRegistry.All.Where(model => model.Available && !model.Deprecated)
                .SelectMany(model => CliTypes.All
                    .Where(cli => ModelMetadataRegistry.IsCompatibleWithCli(cli, model.Id))
                    .Select(cli => new
                    {
                        cli, model = model.Id, label = model.Label,
                        thinkingLevels = ModelMetadataRegistry.ThinkingLevelsFor(cli, model.Id),
                        price = pricing.Estimate(model.Id, 1000000, 0, 0, 0, DateTime.UtcNow).PriceBasis,
                    })).ToList(),
        }));

        // Diagnostic: every model id active in recorded workspace usage with
        // at least one call lacking a historical price.
        app.MapGet("/api/token-pricing/unpriced-models",
            async (HttpContext context, string? workspaceId, WorkspaceRegistry workspaces, WorkspaceSettingsService settings,
                ProjectRegistry registry, ProjectTokenLedger ledger, AgentMessageBusStore bus,
                TaskScannerService scanner, IConfiguration configuration, TokenEndpointReads reads,
                CancellationToken ct) =>
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
                var includeUnassigned = workspace.IsDefault && (human is null
                    || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User));
                // The visible scope is part of the key so permission-filtered results are never shared.
                var key = "unpriced:" + workspace.Id + "|" + includeUnassigned + "\n" + string.Join("\n", visibleProjects
                    .Select(project => project.Id).Order(StringComparer.Ordinal));
                return await reads.ReadAsync(key, () => Task.Run<object>(() =>
                {
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
                        scanner, configuration, calendar.WeekStartUtc, includeUnassigned);
                    return new UnpricedModelsResponse(
                        SelectUnpricedModels(pricing, calendar, inputs), now.ToString("o"),
                        calendar.WeekStartUtc, calendar.WeekEndUtc, calendar.TimeZone);
                }), ct);
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
                item.PromptTokens is { } promptTokens
                    ? pricing.EstimateRequest(item.Model, item.InputTokens, item.OutputTokens,
                        item.CacheReadTokens, item.CacheWriteTokens, Math.Max(0, promptTokens), at)
                    : pricing.Estimate(item.Model, item.InputTokens, item.OutputTokens,
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
