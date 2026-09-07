using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record ModelRoutingPolicyState
{
    public bool EconomyMode { get; init; }

    /// <summary>
    /// Whether run admission may apply a <c>safeAuto</c> model migration to a
    /// non-explicit model on its own. Defaults to on: the whole point of the
    /// migration catalog is that a superseded default does not survive a new
    /// release. Turning it off leaves the proposals visible and makes every
    /// application a deliberate click. Explicit pins are unaffected either way.
    /// </summary>
    public bool AutoApplyModelMigrations { get; init; } = true;
}

public sealed record SetModelRoutingEconomyModeRequest
{
    public bool EconomyMode { get; init; }
}

public sealed record SetAutoApplyModelMigrationsRequest
{
    public bool AutoApply { get; init; }
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

    public bool AutoApplyModelMigrations => Get().AutoApplyModelMigrations;

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
        => Mutate(state => state with { EconomyMode = enabled });

    public ModelRoutingPolicyState SetAutoApplyModelMigrations(bool enabled)
        => Mutate(state => state with { AutoApplyModelMigrations = enabled });

    /// <summary>
    /// Applies one switch and persists the whole state. Going through a single
    /// mutator keeps the other switches intact; writing a fresh record per
    /// setter would silently reset them to their defaults.
    /// </summary>
    private ModelRoutingPolicyState Mutate(Func<ModelRoutingPolicyState, ModelRoutingPolicyState> change)
    {
        lock (_lock)
        {
            var previous = Get();
            _state = change(previous);
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
            return _state;
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
