using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

public sealed record ModelSelectionRoute(string? CliType, string? Model, string? ThinkingLevel);
public sealed record ModelSelectionEvidence(string? Excerpt, string? ClassifierKind);
public sealed record ModelSelectionQuota(string? Provider, string? Window, double? UsedPct, double? CapPct,
    double? ProjectedPct, DateTimeOffset? ResetAt, double? SnapshotAgeSeconds);
public sealed record ModelSelectionEquivalence(string? CapabilityClass, string? CatalogueVersion, string? Source);
public sealed record ModelSelectionPrice(double? From, double? To);

public sealed record ModelSelectionEvent
{
    public string Id { get; init; } = ModelSelectionLedger.NewId();
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Kind { get; init; } = "switched";
    public string Consumer { get; init; } = "";
    public string? Host { get; init; }
    public string? Project { get; init; }
    public string? PipelineType { get; init; }
    public string? StepId { get; init; }
    public string? TaskKey { get; init; }
    public string? AttemptId { get; init; }
    public int? Round { get; init; }
    [JsonPropertyName("from")] public ModelSelectionRoute? From { get; init; }
    [JsonPropertyName("to")] public ModelSelectionRoute? To { get; init; }
    public string Rule { get; init; } = "";
    public string Reason { get; init; } = "";
    public ModelSelectionEvidence? Evidence { get; init; }
    public ModelSelectionQuota? Quota { get; init; }
    public ModelSelectionEquivalence? Equivalence { get; init; }
    public DateTimeOffset? ReturnToPrimaryAt { get; init; }
    public string? ClosesId { get; init; }
    public ModelSelectionPrice? PricePerMTok { get; init; }
    public int Count { get; init; } = 1;
    public IReadOnlyList<string>? CollapsedIds { get; init; }
}

public sealed record ModelSelectionDailySummary(DateOnly Day, string Provider, string Rule, string Consumer, int Count);
public sealed record ModelSelectionEventPage(IReadOnlyList<ModelSelectionEvent> Events, string? NextCursor);

/// <summary>One synchronized writer for workspace model selection receipts.</summary>
public sealed class ModelSelectionLedger
{
    public static string NewId() => ModelSelectionEventId.New();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly Regex Secret = new(@"(?i)(?:sk-[a-z0-9_-]+|ghp_[a-z0-9_]+|AIza[a-z0-9_-]+|(?:api[_-]?key|token|authorization|password|secret)[""']?\s*[:=]\s*[""']?\S+|bearer\s+\S+)", RegexOptions.Compiled);
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly List<ModelSelectionEvent> _events = [];
    private readonly Dictionary<string, ModelSelectionEvent> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public ModelSelectionLedger(IConfiguration config, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        var root = config["TaskRepository"];
        _directory = Path.Combine(string.IsNullOrWhiteSpace(root) ? Path.Combine(AppContext.BaseDirectory, "runtime") : Path.Combine(root, ".runtime"), "model-selection");
        Load();
    }

    public string DirectoryPath => _directory;

    public bool Append(ModelSelectionEvent entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Kind)
            || string.IsNullOrWhiteSpace(entry.Consumer)) return false;
        lock (_sync)
        {
            if (_ids.Contains(entry.Id)) return false;
            var sanitized = entry with
            {
                At = entry.At.ToUniversalTime(),
                Evidence = entry.Evidence is null ? null : entry.Evidence with { Excerpt = Redact(entry.Evidence.Excerpt) },
            };
            Directory.CreateDirectory(_directory);
            if (sanitized.Kind == "waiting")
            {
                var prior = _events.LastOrDefault(e => e.Kind == "waiting" && e.TaskKey == sanitized.TaskKey
                    && e.Consumer == sanitized.Consumer && e.Rule == sanitized.Rule && e.Reason == sanitized.Reason
                    && e.Project == sanitized.Project && e.Host == sanitized.Host
                    && e.PipelineType == sanitized.PipelineType && e.StepId == sanitized.StepId
                    && e.AttemptId == sanitized.AttemptId && e.Round == sanitized.Round
                    && e.From == sanitized.From && e.To == sanitized.To && e.At.UtcDateTime.Date == sanitized.At.UtcDateTime.Date);
                if (prior is not null && !string.IsNullOrWhiteSpace(prior.TaskKey))
                {
                    var combined = prior with { Count = prior.Count + 1,
                        CollapsedIds = (prior.CollapsedIds ?? []).Append(sanitized.Id).ToArray() };
                    RewriteDay(DateOnly.FromDateTime(prior.At.UtcDateTime), prior.Id, combined);
                    _events[_events.IndexOf(prior)] = combined;
                    _ids.Add(sanitized.Id);
                    WriteSummary(DateOnly.FromDateTime(prior.At.UtcDateTime));
                    return true;
                }
            }
            var line = JsonSerializer.Serialize(sanitized, Json) + "\n";
            using (var stream = new FileStream(PathFor(DateOnly.FromDateTime(sanitized.At.UtcDateTime)), FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes);
                stream.Flush(true);
            }
            _events.Add(sanitized);
            _ids.Add(sanitized.Id);
            Project(sanitized);
            WriteSummary(DateOnly.FromDateTime(sanitized.At.UtcDateTime));
            Prune();
            return true;
        }
    }

    public ModelSelectionEventPage Query(string? provider = null, string? project = null, string? consumer = null,
        string? rule = null, string? from = null, string? to = null, string? cursor = null, int limit = 100,
        string? taskKey = null, string? eventId = null, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        lock (_sync)
        {
            var ordered = _events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).ToList();
            if (!string.IsNullOrEmpty(cursor))
            {
                var index = ordered.FindIndex(e => e.Id == cursor);
                if (index < 0) return new([], null);
                ordered = ordered.Skip(index + 1).ToList();
            }
            var matches = ordered.Where(e => Match(provider, e.From?.CliType) || Match(provider, e.To?.CliType))
                .Where(e => Match(project, e.Project)).Where(e => Match(consumer, e.Consumer))
                .Where(e => Match(rule, e.Rule)).Where(e => Match(from, e.From?.Model))
                .Where(e => Match(to, e.To?.Model)).Where(e => Match(taskKey, e.TaskKey))
                .Where(e => Match(eventId, e.Id))
                .Where(e => (since is null || e.At >= since) && (until is null || e.At <= until))
                .Take(Math.Clamp(limit, 1, 200) + 1).ToList();
            var page = matches.Take(Math.Clamp(limit, 1, 200)).ToList();
            return new(page, matches.Count > page.Count ? page.Last().Id : null);
        }
    }

    public IReadOnlyList<ModelSelectionEvent> Active()
    {
        lock (_sync) return _active.Values
            .Where(e => e.Kind != "health-marked" || e.ReturnToPrimaryAt is null || e.ReturnToPrimaryAt > _time.GetUtcNow())
            .OrderByDescending(e => e.At).ToArray();
    }

    public ModelSelectionEvent? ReturnToPrimary(string consumer, string? project, string? taskKey,
        ModelSelectionRoute primary, string reason)
    {
        lock (_sync)
        {
            var prior = _active.Values.Where(e => e.Kind is "switched" or "operator-switch"
                    && e.Consumer == consumer && e.Project == project && e.TaskKey == taskKey
                    && string.Equals(e.From?.CliType, primary.CliType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.From?.Model, primary.Model, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.At).FirstOrDefault();
            if (prior is null) return null;
            var entry = new ModelSelectionEvent
            {
                Kind = "returned", Consumer = consumer, Host = prior.Host, Project = project,
                PipelineType = prior.PipelineType, StepId = prior.StepId, TaskKey = taskKey,
                From = prior.To, To = primary, Rule = "primary-restored", Reason = reason,
                ClosesId = prior.Id,
            };
            return Append(entry) ? entry : null;
        }
    }

    public static string? Redact(string? value)
    {
        if (value is null) return null;
        var clean = Secret.Replace(value, "[redacted]");
        return clean.Length > 300 ? clean[..300] : clean;
    }

    private static bool Match(string? filter, string? value) => string.IsNullOrWhiteSpace(filter)
        || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);
    private string PathFor(DateOnly day) => Path.Combine(_directory, $"{day:yyyy-MM-dd}.jsonl");

    private void Load()
    {
        if (!Directory.Exists(_directory)) return;
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        foreach (var file in Directory.GetFiles(_directory, "*.jsonl").OrderBy(f => f))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var day) || day < today.AddDays(-90)) continue;
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ModelSelectionEvent>(line, Json);
                    if (entry is null || !_ids.Add(entry.Id)) continue;
                    foreach (var collapsedId in entry.CollapsedIds ?? []) _ids.Add(collapsedId);
                    _events.Add(entry);
                    if (day >= today.AddDays(-14)) Project(entry);
                }
                catch (JsonException ex) { SilentCatch.Note(ex, "ModelSelectionLedger: malformed stored event"); }
            }
        }
        Prune();
    }

    private void Project(ModelSelectionEvent entry)
    {
        if (entry.Kind is "switched" or "operator-switch")
        {
            foreach (var earlier in _active.Values.Where(e => e.Kind is "switched" or "operator-switch"
                && e.Consumer == entry.Consumer && e.Project == entry.Project && e.TaskKey == entry.TaskKey).ToArray())
                _active.Remove(earlier.Id);
            _active[entry.Id] = entry;
        }
        if (entry.Kind == "health-marked") _active[entry.Id] = entry;
        if (entry.Kind is "returned" or "health-cleared")
        {
            if (entry.ClosesId is not null) _active.Remove(entry.ClosesId);
            if (entry.Kind == "health-cleared")
                foreach (var earlier in _active.Values.Where(e => e.Kind == "health-marked"
                    && e.Host == entry.Host && e.Rule == entry.Rule
                    && e.From?.CliType == entry.From?.CliType
                    && e.From?.Model == entry.From?.Model).ToArray())
                    _active.Remove(earlier.Id);
        }
    }

    private void RewriteDay(DateOnly day, string id, ModelSelectionEvent replacement)
    {
        var path = PathFor(day);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temp, File.ReadLines(path).Select(line =>
            {
                try { return JsonSerializer.Deserialize<ModelSelectionEvent>(line, Json)?.Id == id ? JsonSerializer.Serialize(replacement, Json) : line; }
                catch (JsonException ex) { SilentCatch.Note(ex, "ModelSelectionLedger: malformed wait entry"); return line; }
            }), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void Prune()
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        foreach (var file in Directory.GetFiles(_directory, "*.jsonl"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", out var day) || day >= today.AddDays(-90)) continue;
            var summary = File.ReadLines(file).Select(line =>
            {
                try { return JsonSerializer.Deserialize<ModelSelectionEvent>(line, Json); }
                catch (JsonException ex) { SilentCatch.Note(ex, "ModelSelectionLedger: malformed retention entry"); return null; }
            }).Where(e => e is not null).Cast<ModelSelectionEvent>()
                .GroupBy(e => new { Provider = e.To?.CliType ?? e.From?.CliType ?? "unknown", e.Rule, e.Consumer })
                .Select(g => new ModelSelectionDailySummary(day, g.Key.Provider, g.Key.Rule, g.Key.Consumer, g.Sum(e => e.Count))).ToArray();
            var summaryPath = Path.Combine(_directory, $"{day:yyyy-MM-dd}.summary.json");
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, Json));
            File.Delete(file);
        }
        foreach (var file in Directory.GetFiles(_directory, "*.summary.json"))
            if (DateOnly.TryParseExact(Path.GetFileName(file)[..10], "yyyy-MM-dd", out var day) && day < today.AddDays(-400)) File.Delete(file);
        _events.RemoveAll(e => DateOnly.FromDateTime(e.At.UtcDateTime) < today.AddDays(-90));
    }

    private void WriteSummary(DateOnly day)
    {
        var rows = _events.Where(e => DateOnly.FromDateTime(e.At.UtcDateTime) == day)
            .GroupBy(e => new { Provider = e.To?.CliType ?? e.From?.CliType ?? "unknown", e.Rule, e.Consumer })
            .Select(g => new ModelSelectionDailySummary(day, g.Key.Provider, g.Key.Rule, g.Key.Consumer, g.Sum(e => e.Count))).ToArray();
        var path = Path.Combine(_directory, $"{day:yyyy-MM-dd}.summary.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(rows, Json), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
