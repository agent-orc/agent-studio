using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>One usage counter bucket for one calendar day or ISO week.</summary>
public sealed record WatcherContingentUsage
{
    public required string PeriodKey { get; init; }
    public long TokensUsed { get; init; }
    public int ModelCalls { get; init; }
    public int ProposalsCreated { get; init; }
    public int CommentsAppended { get; init; }
}

/// <summary>Current day+week usage against budget, for the CLI Management contingent strip.</summary>
public sealed record WatcherContingentSnapshot(
    WatcherContingentBudgets Budgets,
    WatcherContingentUsage Day,
    WatcherContingentUsage Week,
    bool ModelCallsExhausted,
    bool ProposalsExhausted);

/// <summary>
/// Durable per-day/per-week Watcher spend tracker (§10.4). Priced token
/// counts are read from the Token Economy catalog by the caller
/// (<see cref="AgentStudio.Runner.TokenPricing"/>) for display; this service
/// only owns the count/token ledger and the admission checks. Single JSON
/// file per workspace, rewritten atomically; the Watcher sweep is
/// sequential so a coarse read-check-write is sufficient (no concurrent
/// writers within one process).
/// </summary>
public sealed class WatcherContingentService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<WatcherContingentService> _logger;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    public WatcherContingentService(ILogger<WatcherContingentService> logger)
    {
        _logger = logger;
    }

    private static string Path_(string workspaceRoot) =>
        System.IO.Path.Combine(workspaceRoot, "logs", "watcher", "contingent-usage.json");

    private static string DayKey(DateTime utc) => "day:" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string WeekKey(DateTime utc)
    {
        var week = System.Globalization.ISOWeek.GetWeekOfYear(utc);
        var year = System.Globalization.ISOWeek.GetYear(utc);
        return $"week:{year}-W{week:D2}";
    }

    public WatcherContingentSnapshot GetSnapshot(string workspaceRoot, WatcherContingentBudgets budgets, DateTime nowUtc)
    {
        var all = ReadAll(workspaceRoot);
        var day = all.GetValueOrDefault(DayKey(nowUtc)) ?? Empty(DayKey(nowUtc));
        var week = all.GetValueOrDefault(WeekKey(nowUtc)) ?? Empty(WeekKey(nowUtc));
        return new WatcherContingentSnapshot(
            budgets,
            day,
            week,
            ModelCallsExhausted: !WithinBudget(day.ModelCalls, week.ModelCalls, budgets.DailyModelCallBudget, budgets.WeeklyModelCallBudget)
                                  || !WithinTokenBudget(day.TokensUsed, week.TokensUsed, budgets),
            ProposalsExhausted: !WithinBudget(day.ProposalsCreated, week.ProposalsCreated, budgets.DailyProposalBudget, budgets.WeeklyProposalBudget));
    }

    public bool CanCallModel(string workspaceRoot, WatcherContingentBudgets budgets, DateTime nowUtc) =>
        !GetSnapshot(workspaceRoot, budgets, nowUtc).ModelCallsExhausted;

    public bool CanCreateProposal(string workspaceRoot, WatcherContingentBudgets budgets, DateTime nowUtc) =>
        !GetSnapshot(workspaceRoot, budgets, nowUtc).ProposalsExhausted;

    public void RecordModelCall(string workspaceRoot, long tokens, DateTime nowUtc) =>
        Mutate(workspaceRoot, nowUtc, u => u with { ModelCalls = u.ModelCalls + 1, TokensUsed = u.TokensUsed + tokens });

    public void RecordProposal(string workspaceRoot, DateTime nowUtc) =>
        Mutate(workspaceRoot, nowUtc, u => u with { ProposalsCreated = u.ProposalsCreated + 1 });

    public void RecordComment(string workspaceRoot, DateTime nowUtc) =>
        Mutate(workspaceRoot, nowUtc, u => u with { CommentsAppended = u.CommentsAppended + 1 });

    private static bool WithinBudget(int dayUsed, int weekUsed, int dayBudget, int weekBudget) =>
        dayUsed < dayBudget && weekUsed < weekBudget;

    private static bool WithinTokenBudget(long dayUsed, long weekUsed, WatcherContingentBudgets budgets) =>
        dayUsed < budgets.DailyTokenBudget && weekUsed < budgets.WeeklyTokenBudget;

    private static WatcherContingentUsage Empty(string key) => new() { PeriodKey = key };

    private void Mutate(string workspaceRoot, DateTime nowUtc, Func<WatcherContingentUsage, WatcherContingentUsage> update)
    {
        lock (_locks.GetOrAdd(workspaceRoot, static _ => new object()))
        {
            var all = ReadAll(workspaceRoot);
            var dayKey = DayKey(nowUtc);
            var weekKey = WeekKey(nowUtc);
            all[dayKey] = update(all.GetValueOrDefault(dayKey) ?? Empty(dayKey)) with { PeriodKey = dayKey };
            all[weekKey] = update(all.GetValueOrDefault(weekKey) ?? Empty(weekKey)) with { PeriodKey = weekKey };
            // Bounded retention: drop anything older than 60 days so the file cannot grow unbounded.
            var cutoff = nowUtc.AddDays(-60);
            var pruned = all.Where(kv => !IsStaleDayBucket(kv.Key, cutoff)).ToDictionary(kv => kv.Key, kv => kv.Value);
            WriteAll(workspaceRoot, pruned);
        }
    }

    private static bool IsStaleDayBucket(string key, DateTime cutoff) =>
        key.StartsWith("day:", StringComparison.Ordinal)
        && DateTime.TryParse(key[4..], CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
        && d < cutoff;

    private Dictionary<string, WatcherContingentUsage> ReadAll(string workspaceRoot)
    {
        var path = Path_(workspaceRoot);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, WatcherContingentUsage>>(File.ReadAllText(path), JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read watcher contingent usage at {Path}", path);
            return [];
        }
    }

    private void WriteAll(string workspaceRoot, Dictionary<string, WatcherContingentUsage> usage)
    {
        var path = Path_(workspaceRoot);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, JsonSerializer.Serialize(usage, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist watcher contingent usage at {Path}", path);
        }
    }
}
