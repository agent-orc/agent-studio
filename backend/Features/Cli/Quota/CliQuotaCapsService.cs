using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>
/// User-configurable per-CLI per-window usage caps. The user wants the runner
/// to leave a buffer in their CLI subscription (e.g. keep 3-4% of a 5-hour
/// Claude session free, never run weekly to 100%) so manual ad-hoc work outside
/// the orchestrator still has headroom.
///
/// Caps are keyed by <c>(cliType, windowLabel)</c> where <c>windowLabel</c>
/// matches the <see cref="QuotaWindow.Label"/> emitted by the per-CLI probe.
/// The default cap is 95% when an entry is missing, which matches the most
/// common "leave a small buffer" intent without forcing the user to configure
/// every CLI before it can run.
///
/// Storage: a single JSON map next to <c>project-settings.json</c>, with the
/// same LocalAppData fallback. Caps are global (not per-project) because the
/// underlying quota is per-CLI-subscription, not per-project.
/// </summary>
public sealed class CliQuotaCapsService : IQuotaCapTable
{
    public const int DefaultCapPct = QuotaCapDefaults.CapPct;
    private const string FileName = "cli-quota-caps.json";

    private readonly ILogger<CliQuotaCapsService> _logger;
    private readonly IConfiguration _config;
    private readonly ModelSelectionPolicyStore? _policy;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, int>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaCapsService(ILogger<CliQuotaCapsService> logger, IConfiguration config,
        ModelSelectionPolicyStore? policy = null)
    {
        _logger = logger;
        _config = config;
        _policy = policy;
    }

    public Dictionary<string, Dictionary<string, int>> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _cache.ToDictionary(
                e => e.Key,
                e => new Dictionary<string, int>(e.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public int GetCap(string cliType, string windowLabel)
    {
        if (string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(windowLabel))
            return DefaultCapPct;
        EnsureLoaded();
        lock (_lock)
        {
            if (_cache.TryGetValue(cliType, out var byWindow) &&
                byWindow.TryGetValue(windowLabel, out var pct))
                return pct;
            return DefaultCapPct;
        }
    }

    public void SetCap(string cliType, string windowLabel, int capPct)
    {
        if (string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(windowLabel))
            throw new ArgumentException("cliType and windowLabel are required");
        var clamped = Math.Clamp(capPct, 1, 100);
        EnsureLoaded();
        lock (_lock)
        {
            if (!_cache.TryGetValue(cliType, out var byWindow))
            {
                byWindow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _cache[cliType] = byWindow;
            }
            byWindow[windowLabel] = clamped;
            Persist();
        }
        _logger.LogInformation(
            "Quota cap set: {Cli} / {Window} = {Pct}%", cliType, windowLabel, clamped);
    }

    /// <summary>
    /// Evaluate whether the supplied <paramref name="snapshot"/> currently
    /// exceeds any configured cap for its CLI. Returns the most-overshooting
    /// window so the runner can produce a useful "blocked because…" message.
    /// </summary>
    public CapEvaluation Evaluate(QuotaSnapshot? snapshot) => SelectionQuotaMath.EvaluateCap(snapshot, this);

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            if (_policy is not null)
            {
                _cache = _policy.Get().Caps.ToDictionary(pair => pair.Key,
                    pair => new Dictionary<string, int>(pair.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                return;
            }
            var path = ResolveStorePath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc != null)
                {
                    _cache = new Dictionary<string, Dictionary<string, int>>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var (cli, windows) in doc)
                    {
                        _cache[cli] = new Dictionary<string, int>(
                            windows ?? new(), StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read {File} - starting with defaults", FileName);
            }
        }
    }

    private void Persist()
    {
        if (_policy is not null)
        {
            foreach (var (cli, windows) in _cache)
            foreach (var (window, cap) in windows) _policy.SetCap(cli, window, cap);
            return;
        }
        var path = ResolveStorePath();
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(_cache,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write {File} at {Path}", FileName, path);
        }
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(taskRepo, FileName);

        return LocalStateRoot.Combine("agent-taskboard", FileName);
    }
}

// CapEvaluation lives in the contracts package (ModelSelection/QuotaDecisionContracts.cs).
