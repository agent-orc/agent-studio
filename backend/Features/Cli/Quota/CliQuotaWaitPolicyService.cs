using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>
/// Global default for CodingAgentRunner's opt-in wait-on-quota branch. A
/// project may override either field through <see cref="ProjectSettings"/>.
/// Resolution is always project override, then global default, then the
/// platform constants declared here.
/// </summary>
public sealed class CliQuotaWaitPolicyService
{
    public const bool DefaultEnabled = false;
    public const int DefaultThresholdMinutes = 30;
    public const int MinThresholdMinutes = 1;
    public const int MaxThresholdMinutes = 240;
    private const string FileName = "cli-quota-wait-policy.json";

    private readonly ILogger<CliQuotaWaitPolicyService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private CliQuotaWaitPolicy? _cached;

    public CliQuotaWaitPolicyService(ILogger<CliQuotaWaitPolicyService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public CliQuotaWaitPolicy GetGlobal()
    {
        lock (_lock)
        {
            _cached ??= Load();
            return _cached;
        }
    }

    public CliQuotaWaitPolicy SetGlobal(bool enabled, int thresholdMinutes)
    {
        var next = new CliQuotaWaitPolicy(enabled, Clamp(thresholdMinutes));
        lock (_lock)
        {
            _cached = next;
            Persist(next);
        }
        _logger.LogInformation(
            "Global wait-on-quota policy set: enabled={Enabled} thresholdMinutes={Threshold}",
            next.Enabled, next.ThresholdMinutes);
        return next;
    }

    public ResolvedCliQuotaWaitPolicy Resolve(ProjectSettings? project)
    {
        var global = GetGlobal();
        var enabled = project?.WaitOnQuotaEnabled ?? global.Enabled;
        var threshold = Clamp(project?.WaitOnQuotaThresholdMinutes ?? global.ThresholdMinutes);
        var source = project?.WaitOnQuotaEnabled is not null || project?.WaitOnQuotaThresholdMinutes is not null
            ? "project"
            : "global";
        return new ResolvedCliQuotaWaitPolicy(
            enabled, threshold, source,
            project?.WaitOnQuotaEnabled,
            project?.WaitOnQuotaThresholdMinutes,
            global.Enabled,
            global.ThresholdMinutes);
    }

    public static int Clamp(int value) => Math.Clamp(value, MinThresholdMinutes, MaxThresholdMinutes);

    private CliQuotaWaitPolicy Load()
    {
        var path = ResolveStorePath();
        if (path == null || !File.Exists(path))
            return new CliQuotaWaitPolicy(DefaultEnabled, DefaultThresholdMinutes);
        try
        {
            var value = JsonSerializer.Deserialize<CliQuotaWaitPolicy>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value is null
                ? new CliQuotaWaitPolicy(DefaultEnabled, DefaultThresholdMinutes)
                : value with { ThresholdMinutes = Clamp(value.ThresholdMinutes) };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {File}; using disabled wait-on-quota default", FileName);
            return new CliQuotaWaitPolicy(DefaultEnabled, DefaultThresholdMinutes);
        }
    }

    private void Persist(CliQuotaWaitPolicy policy)
    {
        var path = ResolveStorePath();
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write {File} at {Path}", FileName, path);
        }
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo)) return Path.Combine(taskRepo, FileName);
        return LocalStateRoot.Combine("agent-taskboard", FileName);
    }
}

public sealed record CliQuotaWaitPolicy(bool Enabled, int ThresholdMinutes);

// ResolvedCliQuotaWaitPolicy lives in the contracts package (ModelSelection/QuotaDecisionContracts.cs).
