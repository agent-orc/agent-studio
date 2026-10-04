using System.Text.Json;

namespace AgentStudio.Tokens;

/// <summary>
/// One-time repair of repeated completion receipts. The immutable bus remains
/// on the read-time deduplication path; task.json is rewritten through the task
/// mutation service so its stored totals and card receipt agree with readers.
/// </summary>
public sealed class TokenReceiptDuplicateRepair
{
    public const string ReportFileName = "token-receipt-duplicates-v1.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly string _reportPath;
    private readonly string _pendingPath;
    private readonly ILogger<TokenReceiptDuplicateRepair> _logger;

    public TokenReceiptDuplicateRepair(
        TaskScannerService scanner,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<TokenReceiptDuplicateRepair> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _logger = logger;
        _reportPath = Path.Combine(Path.GetFullPath(configuration["TaskRepository"]
            ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
            ".metadata", "migrations", ReportFileName);
        _pendingPath = _reportPath + ".pending";
    }

    public TokenReceiptDuplicateRepairReport RunOnce()
    {
        if (File.Exists(_reportPath))
            return JsonSerializer.Deserialize<TokenReceiptDuplicateRepairReport>(
                File.ReadAllText(_reportPath), Json)!;

        var projects = new Dictionary<string, ProjectDuplicateCount>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var task in _scanner.ScanAllAutomationJobsWithArchive())
        {
            var path = Path.Combine(task.FolderPath, "task.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("tokenSummary", out var element)
                    || element.ValueKind != JsonValueKind.Object) continue;
                var summary = element.Deserialize<TaskTokenSummary>(Json);
                if (summary is null) continue;
                var source = summary.Entries ?? [];
                var seen = new HashSet<TokenUsageIdentity>();
                var unique = source
                    .Where(call => seen.Add(TokenUsageIdentity.From(task.Id, call)))
                    .ToList();
                var removed = source.Count - unique.Count;
                var corrected = ProjectTokenReceiptReader.NormalizeSummary(summary, task.Id);
                if (removed > 0 && !_mutations.ReplaceTokenSummaryForMigration(task.FolderPath, corrected))
                {
                    failures.Add(task.Id);
                    continue;
                }
                var afterEntries = removed > 0 ? corrected.Entries : source;

                var project = task.ProjectName;
                projects.TryGetValue(project, out var count);
                count ??= new ProjectDuplicateCount(project, 0, 0, 0, 0, 0, 0);
                var onDay = source
                    .Where(call => call.Ts.ToUniversalTime().Date == new DateTime(2026, 9, 29))
                    .Sum(Tokens);
                var uniqueOnDay = afterEntries
                    .Where(call => call.Ts.ToUniversalTime().Date == new DateTime(2026, 9, 29))
                    .Sum(Tokens);
                var beforeDayCost = source
                    .Where(call => call.Ts.ToUniversalTime().Date == new DateTime(2026, 9, 29))
                    .Sum(Cost);
                var afterDayCost = afterEntries
                    .Where(call => call.Ts.ToUniversalTime().Date == new DateTime(2026, 9, 29))
                    .Sum(Cost);
                projects[project] = count with
                {
                    EntriesCollapsed = count.EntriesCollapsed + removed,
                    TokensCollapsed = count.TokensCollapsed + source.Sum(Tokens) - afterEntries.Sum(Tokens),
                    September29BeforeTokens = count.September29BeforeTokens + onDay,
                    September29AfterTokens = count.September29AfterTokens + uniqueOnDay,
                    Agt3004BeforeTokens = count.Agt3004BeforeTokens
                        + (task.Id == "AGT-3004" ? source.Sum(Tokens) : 0),
                    Agt3004AfterTokens = count.Agt3004AfterTokens
                        + (task.Id == "AGT-3004" ? afterEntries.Sum(Tokens) : 0),
                    September29BeforeCostUsd = count.September29BeforeCostUsd + beforeDayCost,
                    September29AfterCostUsd = count.September29AfterCostUsd + afterDayCost,
                    Agt3004BeforeCostUsd = count.Agt3004BeforeCostUsd
                        + (task.Id == "AGT-3004" ? source.Sum(Cost) : 0),
                    Agt3004AfterCostUsd = count.Agt3004AfterCostUsd
                        + (task.Id == "AGT-3004" ? afterEntries.Sum(Cost) : 0),
                };
            }
            catch (Exception ex)
            {
                failures.Add($"{task.Id}: {ex.Message}");
            }
        }

        if (File.Exists(_pendingPath))
        {
            var prior = JsonSerializer.Deserialize<TokenReceiptDuplicateRepairReport>(
                File.ReadAllText(_pendingPath), Json);
            if (prior is not null)
            {
                foreach (var old in prior.Projects)
                {
                    projects.TryGetValue(old.Project, out var current);
                    current ??= new ProjectDuplicateCount(old.Project, 0, 0, 0, 0, 0, 0);
                    projects[old.Project] = current with
                    {
                        EntriesCollapsed = current.EntriesCollapsed + old.EntriesCollapsed,
                        TokensCollapsed = current.TokensCollapsed + old.TokensCollapsed,
                        September29BeforeTokens = current.September29BeforeTokens
                            + old.September29BeforeTokens - old.September29AfterTokens,
                        Agt3004BeforeTokens = current.Agt3004BeforeTokens
                            + old.Agt3004BeforeTokens - old.Agt3004AfterTokens,
                        September29BeforeCostUsd = current.September29BeforeCostUsd
                            + old.September29BeforeCostUsd - old.September29AfterCostUsd,
                        Agt3004BeforeCostUsd = current.Agt3004BeforeCostUsd
                            + old.Agt3004BeforeCostUsd - old.Agt3004AfterCostUsd,
                    };
                }
            }
        }
        var report = new TokenReceiptDuplicateRepairReport(
            DateTimeOffset.UtcNow,
            failures.Count == 0,
            projects.Values.OrderBy(value => value.Project, StringComparer.Ordinal).ToList(),
            failures);
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        File.WriteAllText(report.Completed ? _reportPath : _pendingPath,
            JsonSerializer.Serialize(report, Json));
        if (report.Completed && File.Exists(_pendingPath)) File.Delete(_pendingPath);
        _logger.LogInformation(
            "token-receipt-duplicate-repair completed={Completed} projects={Projects} collapsed={Collapsed} failures={Failures} report={Report}",
            report.Completed, report.Projects.Count,
            report.Projects.Sum(project => project.EntriesCollapsed), failures.Count, _reportPath);
        return report;
    }

    private static long Tokens(TaskTokenCall call)
        => call.InputTokens + call.OutputTokens + call.CacheReadTokens + call.CacheCreationTokens;

    private static decimal Cost(TaskTokenCall call)
    {
        var price = TokenPricing.Estimate(call.Model, call.InputTokens, call.OutputTokens,
            call.CacheReadTokens, call.CacheCreationTokens, call.Ts);
        return price.ModelKnown ? price.Total : 0;
    }
}

public sealed record TokenReceiptDuplicateRepairReport(
    DateTimeOffset CompletedAtUtc, bool Completed,
    IReadOnlyList<ProjectDuplicateCount> Projects,
    IReadOnlyList<string> Failures);

public sealed record ProjectDuplicateCount(
    string Project, int EntriesCollapsed, long TokensCollapsed,
    long September29BeforeTokens, long September29AfterTokens,
    long Agt3004BeforeTokens, long Agt3004AfterTokens,
    decimal September29BeforeCostUsd = 0,
    decimal September29AfterCostUsd = 0,
    decimal Agt3004BeforeCostUsd = 0,
    decimal Agt3004AfterCostUsd = 0);
