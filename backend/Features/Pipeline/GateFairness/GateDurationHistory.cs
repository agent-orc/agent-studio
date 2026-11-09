using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// The recent slot-hold durations of each project's remote gates (AGT-3423),
/// kept in one small file so the estimate survives a restart. A gate counts
/// from the moment it holds a slot until its run ends; queue time is excluded.
/// </summary>
public sealed class GateDurationHistory
{
    private readonly object _lock = new();
    private readonly string? _path;
    private Dictionary<string, List<double>>? _seconds;

    public GateDurationHistory(IConfiguration? configuration = null)
        : this(DefaultPath(configuration))
    {
    }

    internal GateDurationHistory(string? path) => _path = path;

    /// <summary>A history kept only in memory, for transports built without one.</summary>
    internal static GateDurationHistory InMemory() => new((string?)null);

    public GateDurationEstimate Estimate(string project)
    {
        lock (_lock)
        {
            var recent = Load().TryGetValue(project, out var seconds)
                ? seconds.Select(TimeSpan.FromSeconds).ToArray() : [];
            return GateSlotFairnessPolicy.Classify(recent);
        }
    }

    public void Record(string project, TimeSpan held)
    {
        if (held < TimeSpan.Zero) return;
        lock (_lock)
        {
            var all = Load();
            if (!all.TryGetValue(project, out var seconds)) all[project] = seconds = [];
            seconds.Add(Math.Round(held.TotalSeconds, 1));
            if (seconds.Count > GateSlotFairnessPolicy.HistoryLength)
                seconds.RemoveRange(0, seconds.Count - GateSlotFairnessPolicy.HistoryLength);
            Save(all);
        }
    }

    private Dictionary<string, List<double>> Load()
    {
        if (_seconds is not null) return _seconds;
        _seconds = new(StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path)) return _seconds;
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, List<double>>>(File.ReadAllText(_path));
            foreach (var (project, seconds) in read ?? [])
                _seconds[project] = seconds.Where(value => value >= 0).TakeLast(GateSlotFairnessPolicy.HistoryLength).ToList();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged history only costs the estimate: every project counts as long until it refills.
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "gate duration history read");
        }
        return _seconds;
    }

    private void Save(Dictionary<string, List<double>> all)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "gate duration history write");
        }
    }

    private static string? DefaultPath(IConfiguration? configuration)
    {
        var configured = configuration?["RemoteGate:DurationHistoryPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return LocalStateRoot.Combine("agentstudio", "gate-duration-history.json");
    }
}
