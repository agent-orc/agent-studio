using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Runner;

namespace AgentStudio.Prompts;

/// <summary>
/// Append-only call ledger for runtime prompts. One compact JSONL row is
/// written for every successful <see cref="RuntimePromptService.Render"/> so
/// call history survives process restarts and prompt versions remain
/// distinguishable after the source file changes.
/// </summary>
public sealed class PromptCallTelemetryService
{
    private readonly ITokenPricing _pricing;
    public const string LogFileName = "prompt-calls.jsonl";
    public const int DeadPromptDays = 30;
    public const int HistoryDays = 14;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<PromptCallTelemetryService> _logger;
    private readonly object _writeLock = new();

    public PromptCallTelemetryService(ITokenPricing pricing,
        IConfiguration configuration,
        ILogger<PromptCallTelemetryService> logger)
    {
        _pricing = pricing;
        _configuration = configuration;
        _logger = logger;
    }

    public string LogPath
    {
        get
        {
            var configured = _configuration["PromptTelemetry:Path"];
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);

            var taskRepository = _configuration["TaskRepository"];
            if (!string.IsNullOrWhiteSpace(taskRepository))
                return Path.Combine(taskRepository, "logs", LogFileName);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.Combine(local, "agent-taskboard", "logs", LogFileName);
        }
    }

    public void Record(PromptCallRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(record, Json) + Environment.NewLine;
            lock (_writeLock)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // Prompt rendering is product behavior; observability must fail open.
            _logger.LogWarning(
                ex,
                "prompt-call-telemetry-write-failed prompt={Prompt}",
                record.PromptId);
        }
    }

    public IReadOnlyDictionary<string, PromptCallAnalytics> Aggregate(
        IReadOnlyCollection<string> promptNames,
        IReadOnlyDictionary<string, string?> currentVersions,
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var wanted = promptNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = ReadAll()
            .Where(record => wanted.Contains(record.PromptId))
            .ToList();

        var result = new Dictionary<string, PromptCallAnalytics>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in promptNames)
        {
            currentVersions.TryGetValue(name, out var currentVersion);
            result[name] = BuildAnalytics(
                records.Where(record =>
                    string.Equals(record.PromptId, name, StringComparison.OrdinalIgnoreCase)),
                currentVersion,
                at);
        }
        return result;
    }

    internal IReadOnlyList<PromptCallRecord> ReadAll()
    {
        if (!File.Exists(LogPath)) return [];
        var records = new List<PromptCallRecord>();
        try
        {
            foreach (var line in File.ReadLines(LogPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<PromptCallRecord>(line, Json);
                    if (record is not null
                        && !string.IsNullOrWhiteSpace(record.PromptId)
                        && !string.IsNullOrWhiteSpace(record.Version))
                        records.Add(record);
                }
                catch (JsonException ex)
                {
                    // A torn final append must not hide earlier valid history.
                    SilentCatch.Note(ex, "PromptCallTelemetryService: skipping malformed JSONL row.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "prompt-call-telemetry-read-failed path={Path}", LogPath);
        }
        return records;
    }

    private PromptCallAnalytics BuildAnalytics(
        IEnumerable<PromptCallRecord> source,
        string? currentVersion,
        DateTimeOffset now)
    {
        var records = source.OrderBy(record => record.Timestamp).ToList();
        var sevenDayCutoff = now.AddDays(-7);
        var priced = records.Select(record => (Record: record, Price: Price(record))).ToList();
        var versions = priced
            .GroupBy(item => item.Record.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildVersion(group.Key, group.ToList(), currentVersion))
            .OrderByDescending(version => version.LastCalledAt)
            .ToList();
        var days = Enumerable.Range(0, HistoryDays)
            .Select(offset => DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(offset - HistoryDays + 1)))
            .Select(day =>
            {
                var dayRecords = priced.Where(item =>
                    DateOnly.FromDateTime(item.Record.Timestamp.UtcDateTime) == day).ToList();
                return new PromptCallDay
                {
                    Date = day,
                    Calls = dayRecords.Count,
                    InputTokens = dayRecords.Sum(item => (long)item.Record.InputTokens),
                    CostUsd = dayRecords.Where(item => item.Price.ModelKnown)
                        .Sum(item => item.Price.Total),
                };
            })
            .ToList();

        var current = versions.FirstOrDefault(version =>
            string.Equals(version.Version, currentVersion, StringComparison.OrdinalIgnoreCase));
        return new PromptCallAnalytics
        {
            TotalCalls = records.Count,
            Calls7d = records.Count(record => record.Timestamp >= sevenDayCutoff),
            LastCalledAt = records.Count == 0 ? null : records[^1].Timestamp,
            InputTokens = records.Sum(record => (long)record.InputTokens),
            CostUsd = priced.Where(item => item.Price.ModelKnown).Sum(item => item.Price.Total),
            CostUsd7d = priced
                .Where(item => item.Record.Timestamp >= sevenDayCutoff && item.Price.ModelKnown)
                .Sum(item => item.Price.Total),
            UnpricedCalls = priced.Count(item => !item.Price.ModelKnown),
            UnpricedCalls7d = priced.Count(item =>
                item.Record.Timestamp >= sevenDayCutoff && !item.Price.ModelKnown),
            CurrentVersionCalls = current?.Calls ?? 0,
            IsDead = records.Count == 0 || records.All(record =>
                record.Timestamp < now.AddDays(-DeadPromptDays)),
            Daily = days,
            Versions = versions,
        };
    }

    private PromptCallVersion BuildVersion(
        string version,
        List<(PromptCallRecord Record, TokenCostEstimate Price)> records,
        string? currentVersion)
    {
        var first = records.Min(item => item.Record.Timestamp);
        var last = records.Max(item => item.Record.Timestamp);
        return new PromptCallVersion
        {
            Version = version,
            FirstCalledAt = first,
            LastCalledAt = last,
            Calls = records.Count,
            InputTokens = records.Sum(item => (long)item.Record.InputTokens),
            CostUsd = records.Where(item => item.Price.ModelKnown).Sum(item => item.Price.Total),
            UnpricedCalls = records.Count(item => !item.Price.ModelKnown),
            Models = records
                .Select(item => item.Record.Model)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsCurrent = string.Equals(version, currentVersion, StringComparison.OrdinalIgnoreCase),
        };
    }

    private TokenCostEstimate Price(PromptCallRecord record) =>
        _pricing.Estimate(
            record.Model,
            record.InputTokens,
            outputTokens: 0,
            cacheReadTokens: 0,
            cacheCreationTokens: 0,
            recordedAt: record.Timestamp.UtcDateTime);
}

public sealed record PromptCallContext(
    string? Project = null,
    string? Step = null,
    string? Model = null);

public sealed class PromptCallRecord
{
    public DateTimeOffset Timestamp { get; set; }
    public string PromptId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "file";
    public int InputTokens { get; set; }
    public bool TokensEstimated { get; set; } = true;
    public string? Model { get; set; }
    public string? Project { get; set; }
    public string? Step { get; set; }
}

public sealed class PromptCallAnalytics
{
    public int TotalCalls { get; set; }
    public int Calls7d { get; set; }
    public DateTimeOffset? LastCalledAt { get; set; }
    public long InputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public decimal CostUsd7d { get; set; }
    public int UnpricedCalls { get; set; }
    public int UnpricedCalls7d { get; set; }
    public int CurrentVersionCalls { get; set; }
    public bool IsDead { get; set; }
    public List<PromptCallDay> Daily { get; set; } = [];
    public List<PromptCallVersion> Versions { get; set; } = [];
}

public sealed class PromptCallDay
{
    public DateOnly Date { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public decimal CostUsd { get; set; }
}

public sealed class PromptCallVersion
{
    public string Version { get; set; } = "";
    public DateTimeOffset FirstCalledAt { get; set; }
    public DateTimeOffset LastCalledAt { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int UnpricedCalls { get; set; }
    public bool IsCurrent { get; set; }
    public List<string> Models { get; set; } = [];
}
