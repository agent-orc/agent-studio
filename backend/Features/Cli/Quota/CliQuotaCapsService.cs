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
public sealed class CliQuotaCapsService
{
    public const int DefaultCapPct = 95;
    private const string FileName = "cli-quota-caps.json";

    private readonly ILogger<CliQuotaCapsService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, int>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CliQuotaCapsService(ILogger<CliQuotaCapsService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
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
    public CapEvaluation Evaluate(QuotaSnapshot? snapshot)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.CliType))
            return CapEvaluation.NotBlocked;

        // Conservative admission for unconfirmed snapshots (AGT-2064). A snapshot
        // flagged suspicious - a downward glitch a confirmation probe has not yet
        // agreed with, or one a live usage-limit error contradicted - is treated
        // as over-cap regardless of how green its numbers look: we would rather
        // hold a launch than fire onto a CLI that may really be at its limit. The
        // hold clears the moment a re-probe produces a trusted snapshot.
        if (snapshot.Suspicious)
        {
            var worstSuspect = snapshot.Windows?
                .Where(w => w.UsedPct is not null)
                .OrderByDescending(w => w.UsedPct)
                .FirstOrDefault();
            return new CapEvaluation(
                Blocked: true,
                CliType: snapshot.CliType,
                WindowLabel: worstSuspect?.Label ?? "quota",
                CapPct: worstSuspect?.Label is { } wl ? GetCap(snapshot.CliType, wl) : DefaultCapPct,
                UsedPct: worstSuspect?.UsedPct ?? 100d,
                Suspicious: true,
                SuspiciousReason: snapshot.SuspiciousReason);
        }

        if (snapshot.Windows == null || snapshot.Windows.Count == 0)
            return CapEvaluation.NotBlocked;

        CapEvaluation? worst = null;
        foreach (var w in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(w.Label) || w.UsedPct is null) continue;
            var cap = GetCap(snapshot.CliType, w.Label);
            if (w.UsedPct.Value < cap) continue;
            var ev = new CapEvaluation(
                Blocked: true,
                CliType: snapshot.CliType,
                WindowLabel: w.Label,
                CapPct: cap,
                UsedPct: w.UsedPct.Value);
            if (worst is null || ev.UsedPct - ev.CapPct > worst.UsedPct - worst.CapPct)
                worst = ev;
        }
        return worst ?? CapEvaluation.NotBlocked;
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
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

/// <summary>
/// Result of evaluating a quota snapshot against the configured caps. When
/// <see cref="Blocked"/> is false, the other fields are not meaningful and
/// callers should not log a "blocked" reason.
/// </summary>
public sealed record CapEvaluation(
    bool Blocked,
    string? CliType = null,
    string? WindowLabel = null,
    int CapPct = 0,
    double UsedPct = 0d,
    bool Suspicious = false,
    string? SuspiciousReason = null,
    // AGT-2055: set when the block is a forward-looking projection ("this
    // window will cross the cap before it resets") rather than an already-
    // breached cap. Callers use this to pre-empt the wall instead of reacting
    // to it. <see cref="ResetAt"/> carries the offending window's reset time
    // so a "next reset HH:mm" hint can be surfaced at the waiting slot.
    bool Projected = false,
    DateTime? ResetAt = null)
{
    public static readonly CapEvaluation NotBlocked = new(false);

    public string DescribeReason()
    {
        if (!Blocked) return "ok";
        if (Suspicious)
            return $"{CliType} quota snapshot unconfirmed ({SuspiciousReason ?? "suspicious glitch"}); holding launch until a re-probe confirms";
        return Projected
            ? $"{CliType} {WindowLabel} projected to reach the {CapPct}% cap before reset (now {UsedPct:0.#}%)"
            : $"{CliType} {WindowLabel} at {UsedPct:0.#}% (cap {CapPct}%)";
    }
}
