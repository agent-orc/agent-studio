using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record ModelRoutingPolicyState
{
    public bool EconomyMode { get; init; }
    /// <summary>
    /// Whether safe Token Economy model migrations may be applied at run
    /// admission. The switch is default-on; older state files that predate the
    /// field therefore retain the automatic policy without a migration.
    /// </summary>
    public bool AutoModelMigrationsEnabled { get; init; } = true;
}

public sealed record SetModelRoutingEconomyModeRequest
{
    public bool EconomyMode { get; init; }
}

public sealed record SetAutoModelMigrationsRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// One workspace-wide quota reaction switch. This mutable operational state is
/// stored beside the task repository; the actual routing policy remains the
/// immutable, repository-versioned registry.
/// </summary>
public sealed class ModelRoutingPolicyStateStore : IModelRoutingModeProvider
{
    private const string FileName = "model-routing-state.json";
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelRoutingPolicyStateStore> _logger;
    private readonly object _lock = new();
    private ModelRoutingPolicyState? _state;

    public ModelRoutingPolicyStateStore(
        IConfiguration configuration,
        ILogger<ModelRoutingPolicyStateStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool EconomyMode => Get().EconomyMode;
    public bool AutoModelMigrationsEnabled => Get().AutoModelMigrationsEnabled;

    public ModelRoutingPolicyState Get()
    {
        lock (_lock)
        {
            if (_state != null) return _state;
            var path = ResolvePath();
            if (!File.Exists(path)) return _state = new();
            try
            {
                _state = JsonSerializer.Deserialize<ModelRoutingPolicyState>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read model routing state from {Path}", path);
                _state = new();
            }
            return _state;
        }
    }

    public ModelRoutingPolicyState SetEconomyMode(bool enabled)
    {
        lock (_lock)
        {
            var previous = Get();
            _state = previous with { EconomyMode = enabled };
            Persist(previous);
            return _state;
        }
    }

    public ModelRoutingPolicyState SetAutoModelMigrations(bool enabled)
    {
        lock (_lock)
        {
            var previous = Get();
            _state = previous with { AutoModelMigrationsEnabled = enabled };
            Persist(previous);
            return _state;
        }
    }

    private void Persist(ModelRoutingPolicyState? previous)
    {
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _state = previous;
            _logger.LogError(ex, "Failed to write model routing state to {Path}", path);
            throw;
        }
    }

    private string ResolvePath()
    {
        var root = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agent-taskboard");
        return Path.Combine(root, FileName);
    }
}
