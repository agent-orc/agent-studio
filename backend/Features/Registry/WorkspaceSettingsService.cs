using System.Text.Json;

namespace AgentStudio.Registry;

/// <summary>
/// AGT-1812 — persistence for the per-workspace default settings that sit beside
/// a <see cref="WorkspaceRecord"/> (keyed by <see cref="WorkspaceRecord.Id"/>),
/// the workspace analog of <see cref="AgentStudio.Projects.ProjectSettingsService"/>.
///
/// <para>Stored as a single JSON file
/// (<c>&lt;TaskRepository&gt;/.metadata/workspace-settings.json</c>) so it lives
/// beside <c>workspaces.json</c> under the same metadata folder. When no
/// TaskRepository is configured it falls back to LocalAppData so the file always
/// lands on a writable disk (mirroring ProjectSettingsService). Registered as a
/// singleton; reads are lazy and every mutation persists the whole map.</para>
/// </summary>
public sealed class WorkspaceSettingsService
{
    public const string FileName = "workspace-settings.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<WorkspaceSettingsService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private Dictionary<string, WorkspaceSettings> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public WorkspaceSettingsService(ILogger<WorkspaceSettingsService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Current defaults for a workspace. Returns a fresh (all-null)
    /// <see cref="WorkspaceSettings"/> on a miss so callers never null-check.
    /// </summary>
    public WorkspaceSettings Get(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return new WorkspaceSettings();
        EnsureLoaded();
        lock (_lock)
        {
            return _cache.TryGetValue(workspaceId, out var s) ? s : new WorkspaceSettings();
        }
    }

    public Dictionary<string, WorkspaceSettings> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return new Dictionary<string, WorkspaceSettings>(_cache, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Sets the workspace-default orchestrator model (and optionally its thinking
    /// level). A blank model clears the model default; a null
    /// <paramref name="thinkingLevel"/> leaves the stored thinking level
    /// untouched, a blank one clears it. Mirrors
    /// <see cref="AgentStudio.Projects.ProjectSettingsService.SetOrchestratorModel"/>.
    /// </summary>
    public void SetOrchestratorModel(string workspaceId, string? model, string? thinkingLevel = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(workspaceId, out var s) ? s : new WorkspaceSettings();
            var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            _cache[workspaceId] = current with
            {
                OrchestratorModel = normalizedModel,
                OrchestratorThinkingLevel = thinkingLevel is null
                    ? current.OrchestratorThinkingLevel
                    : (string.IsNullOrWhiteSpace(thinkingLevel)
                        ? null
                        : ModelMetadataRegistry.ResolveThinkingLevel(CliTypes.Claude, normalizedModel, thinkingLevel)),
            };
            Persist();
        }
        _logger.LogInformation(
            "workspace-settings orchestrator model set to {Model} for workspace {Workspace}",
            string.IsNullOrWhiteSpace(model) ? "(default)" : model, workspaceId);
    }

    /// <summary>
    /// Sets or clears the workspace-default local CLI execution engine. Blank
    /// clears the default; unknown non-blank values are rejected.
    /// </summary>
    public void SetCliExecutionEngine(string workspaceId, string? executionEngine)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        var normalized = CliExecutionEngines.NormalizeOverride(executionEngine);
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache.TryGetValue(workspaceId, out var s) ? s : new WorkspaceSettings();
            _cache[workspaceId] = current with { CliExecutionEngine = normalized };
            Persist();
        }
        _logger.LogInformation(
            "workspace-settings CLI execution engine set to {ExecutionEngine} for workspace {Workspace}",
            normalized ?? "(default)", workspaceId);
    }

    /// <summary>
    /// Sets the workspace-default autonomy level (<c>0..4</c>; out-of-range values
    /// are clamped). Null clears the workspace default (projects then fall through
    /// to the platform default of balanced/2).
    /// </summary>
    public void SetAutonomyLevel(string workspaceId, int? level)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        EnsureLoaded();
        int? clamped = level is null ? null : Math.Clamp(level.Value, 0, 4);
        lock (_lock)
        {
            var current = _cache.TryGetValue(workspaceId, out var s) ? s : new WorkspaceSettings();
            _cache[workspaceId] = current with { AutonomyLevel = clamped };
            Persist();
        }
        _logger.LogInformation(
            "workspace-settings autonomy level set to {Level} for workspace {Workspace}",
            clamped, workspaceId);
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
                var doc = JsonSerializer.Deserialize<WorkspaceSettingsFile>(json, JsonOpts);
                if (doc?.Workspaces != null)
                    _cache = new Dictionary<string, WorkspaceSettings>(doc.Workspaces, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read workspace-settings.json — starting with defaults");
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
            var payload = new WorkspaceSettingsFile
            {
                Version = 1,
                Workspaces = new Dictionary<string, WorkspaceSettings>(_cache, StringComparer.OrdinalIgnoreCase),
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write workspace-settings.json at {Path}", path);
        }
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(RegistryPaths.MetadataDir(taskRepo), FileName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", FileName);
    }
}
