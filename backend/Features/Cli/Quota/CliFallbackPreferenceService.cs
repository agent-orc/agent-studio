using System.Text.Json;

namespace AgentStudio.Cli;

public sealed record CliFallbackPreference(
    string CliType,
    bool Active,
    DateTime? EnabledAt,
    DateTime? ExpiresAt);

/// <summary>
/// Runtime operator preference to conserve one provider before its hard cap.
/// The preference is persisted and lazily expires at the selected quota reset.
/// </summary>
public sealed class CliFallbackPreferenceService
{
    private const string FileName = "cli-fallback-preferences.json";
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private Dictionary<string, CliFallbackPreference>? _states;

    public CliFallbackPreferenceService(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CliFallbackPreference Get(string cliType)
    {
        var cli = Normalize(cliType);
        lock (_lock)
        {
            EnsureLoaded();
            if (!_states!.TryGetValue(cli, out var state))
                return new CliFallbackPreference(cli, false, null, null);
            if (state.Active && state.ExpiresAt is { } expiry && expiry <= UtcNow())
            {
                state = state with { Active = false };
                _states[cli] = state;
                Persist();
            }
            return state;
        }
    }

    public IReadOnlyDictionary<string, CliFallbackPreference> GetAll()
        => CliTypes.All.ToDictionary(cli => cli, Get, StringComparer.OrdinalIgnoreCase);

    public CliFallbackPreference Set(string cliType, bool active, DateTime? resetAt)
    {
        var cli = Normalize(cliType);
        var now = UtcNow();
        if (active && (resetAt is null || resetAt <= now))
            throw new ArgumentException("An active fallback preference requires a future quota reset.", nameof(resetAt));
        var state = new CliFallbackPreference(
            cli,
            active,
            active ? now : null,
            active ? resetAt : null);
        lock (_lock)
        {
            EnsureLoaded();
            _states![cli] = state;
            Persist();
        }
        return state;
    }

    private void EnsureLoaded()
    {
        if (_states is not null) return;
        try
        {
            _states = File.Exists(PathName())
                ? JsonSerializer.Deserialize<Dictionary<string, CliFallbackPreference>>(
                    File.ReadAllText(PathName()), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _states = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Persist()
    {
        var path = PathName();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_states, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string PathName()
    {
        var root = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-taskboard");
        return Path.Combine(root, FileName);
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string Normalize(string cliType)
        => string.IsNullOrWhiteSpace(cliType) ? CliTypes.Claude : cliType.Trim().ToLowerInvariant();
}
