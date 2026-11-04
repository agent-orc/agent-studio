using AgentStudio.TaskServer.Contracts;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Write seam for the selection ledger. The selection service records every
/// result that is not a plain primary launch here; the daily JSONL store with
/// retention and the read API arrive with MS-2 behind the same seam.
/// </summary>
public interface IModelSelectionLedger
{
    void Record(SelectionLedgerEntry entry);
    SelectionLedgerEntry? GetActive(SelectionRequest request) => null;
    IReadOnlyList<SelectionLedgerEntry> Active() => [];
}

/// <summary>
/// Interim ledger: one structured <c>model_selection_event</c> log line per
/// event. Claim and pickup loops ask again every tick, so an event identical
/// to the previous one for the same card and consumer is written once.
/// </summary>
public sealed class LoggingModelSelectionLedger : IModelSelectionLedger
{
    private readonly ILogger<LoggingModelSelectionLedger> logger;
    private const int MaxTrackedScopes = 2048;
    private readonly Dictionary<string, string> _lastByScope = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SelectionLedgerEntry> _active = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly string? _directory;
    private readonly TimeProvider _time;
    private DateOnly? _lastPruned;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LoggingModelSelectionLedger(ILogger<LoggingModelSelectionLedger> logger,
        IConfiguration? config = null, TimeProvider? timeProvider = null)
    {
        this.logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        var root = config?["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) return;
        _directory = Path.Combine(root, ".runtime", "model-selection");
        if (!Directory.Exists(_directory)) return;
        var today = _time.GetUtcNow().UtcDateTime.Date;
        foreach (var day in Enumerable.Range(0, 14).Reverse())
        {
            var path = Path.Combine(_directory, today.AddDays(-day).ToString("yyyy-MM-dd") + ".jsonl");
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<SelectionLedgerEntry>(line, Json);
                    if (entry is not null) Project(entry);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "model_selection_ledger_line_skipped path={Path}", path);
                }
            }
        }
        Prune(today);
    }

    public SelectionLedgerEntry? GetActive(SelectionRequest request)
    {
        lock (_lock)
            return _active.GetValueOrDefault(Scope(request.Consumer, request.Project, request.TaskKey, request.StepId));
    }

    public IReadOnlyList<SelectionLedgerEntry> Active()
    {
        lock (_lock) return _active.Values.OrderByDescending(entry => entry.At).ToArray();
    }

    private static string Scope(string consumer, string? project, string? task, string? step)
        => $"{consumer}|{project}|{task}|{step}";

    private void Project(SelectionLedgerEntry entry)
    {
        var scope = Scope(entry.Consumer, entry.Project, entry.TaskKey, entry.StepId);
        if (entry.Kind == SelectionLedgerKinds.Switched) _active[scope] = entry;
        else if (entry.Kind == SelectionLedgerKinds.Returned && entry.ClosesId is { } id
                 && _active.TryGetValue(scope, out var open) && open.Id == id)
            _active.Remove(scope);
    }

    public void Record(SelectionLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var scope = Scope(entry.Consumer, entry.Project, entry.TaskKey, entry.StepId);
        var signature = $"{entry.Kind}|{entry.Rule}|{entry.From}|{entry.To}|{entry.Reason}";
        lock (_lock)
        {
            if (_lastByScope.TryGetValue(scope, out var last) && last == signature) return;
            if (_lastByScope.Count >= MaxTrackedScopes) _lastByScope.Clear();
            _lastByScope[scope] = signature;
            if (_directory is not null)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(Path.Combine(_directory, entry.At.ToString("yyyy-MM-dd") + ".jsonl"),
                        JsonSerializer.Serialize(entry, Json) + "\n");
                    Summarize(entry);
                    Prune(_time.GetUtcNow().UtcDateTime.Date);
                }
                catch (IOException ex) { logger.LogWarning(ex, "model_selection_ledger_write_failed"); }
                catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "model_selection_ledger_write_failed"); }
            }
            Project(entry);
        }
        logger.LogInformation(
            "model_selection_event id={Id} kind={Kind} consumer={Consumer} path={Path} project={Project} task={TaskKey} host={Host} from={FromCli}/{FromModel}/{FromThinking} to={ToCli}/{ToModel}/{ToThinking} rule={Rule} reason={Reason}",
            entry.Id, entry.Kind, entry.Consumer, entry.ExecutionPath, entry.Project ?? "-", entry.TaskKey ?? "-",
            entry.Host ?? "-", entry.From.CliType, entry.From.Model ?? "<default>", entry.From.ThinkingLevel ?? "-",
            entry.To.CliType, entry.To.Model ?? "<default>", entry.To.ThinkingLevel ?? "-", entry.Rule, entry.Reason);
    }

    private void Summarize(SelectionLedgerEntry entry)
    {
        if (_directory is null || string.IsNullOrWhiteSpace(entry.From.CliType)) return;
        var summaryDir = Path.Combine(_directory, "summary");
        Directory.CreateDirectory(summaryDir);
        var path = Path.Combine(summaryDir, entry.At.ToString("yyyy-MM-dd") + ".json");
        var counts = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path), Json) ?? []
            : new Dictionary<string, int>(StringComparer.Ordinal);
        var key = $"{entry.From.CliType}|{entry.Rule}|{entry.Consumer}";
        counts[key] = counts.GetValueOrDefault(key) + 1;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(counts, Json));
        File.Move(temporary, path, true);
    }

    private void Prune(DateTime today)
    {
        if (_directory is null || !Directory.Exists(_directory)) return;
        var day = DateOnly.FromDateTime(today);
        if (_lastPruned == day) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.jsonl"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", out var date)
                && date < day.AddDays(-90)) File.Delete(path);
        var summaryDir = Path.Combine(_directory, "summary");
        if (Directory.Exists(summaryDir))
            foreach (var path in Directory.EnumerateFiles(summaryDir, "*.json"))
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", out var date)
                    && date < day.AddDays(-400)) File.Delete(path);
        _lastPruned = day;
    }
}
