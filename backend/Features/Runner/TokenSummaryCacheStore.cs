using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Aggregate (workspace-wide) token rollup persisted to disk so the
/// status-bar usage modal renders an immediately-readable number on
/// app start, before any per-project poll has completed.
///
/// <para>
/// Mirrors the file-cache pattern used by
/// <see cref="AgentStudio.Cli.QuotaCacheStore"/>: stored
/// at <c>&lt;TaskRepository&gt;/.runtime/token-aggregate-cache.json</c>
/// (or <c>AppContext.BaseDirectory/runtime/</c> when no TaskRepository
/// is configured), atomic write via .tmp + rename, tolerant to
/// corruption (logs and returns null on parse failure).
/// </para>
/// </summary>
public sealed record TokenSummaryAggregate(
    int Projects,
    int OrchestratorEntries,
    int OrchestratorLlmCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool AllModelsPriced,
    IReadOnlyList<TokenSummaryByModel> ByModel,
    IReadOnlyList<TokenSummaryByProject> ByProject,
    string FetchedAt,
    string? FirstActivity,
    string? LastActivity,
    string Disclaimer,
    int UnknownModelCount = 0,
    int UnpricedModelCount = 0);

public sealed record TokenSummaryByProject(
    string Project,
    int OrchestratorLlmCalls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd);

public sealed class TokenSummaryCacheStore
{
    private readonly ILogger<TokenSummaryCacheStore> _logger;
    private readonly string _path;
    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TokenSummaryCacheStore(IConfiguration config, ILogger<TokenSummaryCacheStore> logger)
    {
        _logger = logger;
        var taskRepo = config["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
        try { Directory.CreateDirectory(baseDir); } catch (Exception __ex) { SilentCatch.Note(__ex, "TokenSummaryCacheStore: best-effort"); /* best-effort */ }
        _path = Path.Combine(baseDir, "token-aggregate-cache.json");
    }

    public TokenSummaryAggregate? Read()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var raw = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<TokenSummaryAggregate>(raw, ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read token-aggregate cache at {Path}", _path);
            return null;
        }
    }

    public void Write(TokenSummaryAggregate aggregate)
    {
        try
        {
            var json = JsonSerializer.Serialize(aggregate, WriteOpts);
            lock (_writeLock)
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist token-aggregate cache to {Path}", _path);
        }
    }
}
