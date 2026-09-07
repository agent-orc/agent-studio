using System.Text.Json;

namespace AgentStudio.Docs;

/// <summary>
/// The workspace-wide default model + level for wiki-grading maintenance runs
/// (AGT-2051). Deliberately its OWN configuration class, separate from the
/// project pipeline models: a maintenance grade is a cross-cutting janitorial
/// pass, not part of any one task's delivery pipeline, so it does not reuse
/// <see cref="AgentStudio.Projects.ProjectSettings"/>. It lives in the
/// consolidated CLI-management area alongside <c>cli-model-routing.json</c> and
/// <c>cli-quota-caps.json</c>, since that is where operators reason about which
/// model runs which class of work.
///
/// <para>The default is the newest available Sonnet rather than the
/// cheap Haiku the automatic drift post-step uses, because a maintenance grade
/// is a low-frequency, high-value judgement. Operators can raise it to Opus or
/// lower it. The value is the default pre-filled at the trigger; the operator
/// can still deviate per run.</para>
/// </summary>
public sealed class WikiMaintenanceModelService
{
    public const string FileName = "wiki-maintenance-model.json";

    /// <summary>Platform strong default when no workspace value is stored.</summary>
    public static string DefaultModel =>
        ModelFamilyResolver.Resolve(ModelFamilies.ClaudeSonnet);
    public const string DefaultCli = "claude";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<WikiMaintenanceModelService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private WikiMaintenanceModelConfig? _cache;
    private bool _loaded;

    public WikiMaintenanceModelService(ILogger<WikiMaintenanceModelService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>Current maintenance model default (platform strong default on a miss).</summary>
    public WikiMaintenanceModelConfig Get()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _cache ?? Fallback();
        }
    }

    /// <summary>
    /// Sets the maintenance model default. A blank model resets to the platform
    /// strong default; a blank thinking level clears it; a null thinking level
    /// leaves it unchanged.
    /// </summary>
    public WikiMaintenanceModelConfig Set(string? cliType, string? model, string? thinkingLevel)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var current = _cache ?? Fallback();
            var cli = string.IsNullOrWhiteSpace(cliType) ? current.CliType : cliType.Trim();
            var mdl = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            var level = thinkingLevel is null
                ? current.ThinkingLevel
                : (string.IsNullOrWhiteSpace(thinkingLevel) ? null : thinkingLevel.Trim());
            _cache = new WikiMaintenanceModelConfig { CliType = cli, Model = mdl, ThinkingLevel = level };
            Persist(_cache);
            _logger.LogInformation(
                "wiki maintenance model set to {Cli}/{Model} (level={Level})", cli, mdl, level ?? "(default)");
            return _cache;
        }
    }

    private static WikiMaintenanceModelConfig Fallback() =>
        new() { CliType = DefaultCli, Model = DefaultModel, ThinkingLevel = null };

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
                _cache = JsonSerializer.Deserialize<WikiMaintenanceModelConfig>(File.ReadAllText(path), JsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read {File} - using the strong default", FileName);
            }
        }
    }

    private void Persist(WikiMaintenanceModelConfig config)
    {
        var path = ResolveStorePath();
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
            File.Move(tmp, path, overwrite: true);
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

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", FileName);
    }
}

/// <summary>
/// Persisted shape of the wiki maintenance-model default: which CLI + model +
/// optional thinking level a grading run uses when the operator does not
/// override it at the trigger.
/// </summary>
public sealed record WikiMaintenanceModelConfig
{
    public string CliType { get; init; } = WikiMaintenanceModelService.DefaultCli;
    public string Model { get; init; } = WikiMaintenanceModelService.DefaultModel;
    public string? ThinkingLevel { get; init; }
}
