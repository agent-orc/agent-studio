using System.Text;
using System.Text.Json;
using AgentStudio.Runner;

namespace AgentStudio.Orchestrator;

public sealed record OrchestratorSessionRecord(
    string ContextKey,
    string EncodedKey,
    string Kind,
    string? ProjectId,
    string? TaskKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? SessionId,
    string? Model,
    DateTime? BootedAt,
    string? BootPromptPreview,
    string? BootReplyPreview,
    long CumulativeInputTokens,
    long CumulativeOutputTokens,
    long CumulativeCacheReadTokens,
    long CumulativeCacheCreationTokens,
    int Calls,
    DateTime? LastUsedAt,
    string? LastError,
    string? WorkbenchKey = null);

public sealed record OrchestratorSessionHistoryEntry(
    DateTime Ts,
    string Kind,
    string TurnId,
    string? Status,
    string? PromptPreview,
    string? ReplyPreview,
    string? Model,
    string? SessionId,
    string? Error,
    int? QueuePosition);

public sealed class OrchestratorSessionRegistry
{
    public const string SessionsFolderName = "orchestrator-sessions";
    public const string SessionFileName = "session.json";
    public const string HistoryFileName = "history.jsonl";

    private readonly IConfiguration _config;
    private readonly GlobalOrchestratorSessionStore _legacyGlobal;
    private readonly ILogger<OrchestratorSessionRegistry> _logger;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public OrchestratorSessionRegistry(
        IConfiguration config,
        GlobalOrchestratorSessionStore legacyGlobal,
        ILogger<OrchestratorSessionRegistry> logger)
    {
        _config = config;
        _legacyGlobal = legacyGlobal;
        _logger = logger;
    }

    public string? TaskRepositoryRoot => string.IsNullOrWhiteSpace(_config["TaskRepository"])
        ? null
        : _config["TaskRepository"];

    public string? SessionsRoot
    {
        get
        {
            var root = TaskRepositoryRoot;
            return root == null ? null : Path.Combine(root, ".metadata", SessionsFolderName);
        }
    }

    public IReadOnlyList<OrchestratorSessionRecord> List()
    {
        lock (_gate)
        {
            EnsureGlobalMigratedOrCreated();
            var root = SessionsRoot;
            if (root == null || !Directory.Exists(root))
                return Array.Empty<OrchestratorSessionRecord>();

            var records = new List<OrchestratorSessionRecord>();
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var encoded = Path.GetFileName(dir);
                if (!OrchestratorContextKey.TryDecode(encoded, out _))
                    continue;
                var record = ReadRecord(Path.Combine(dir, SessionFileName));
                if (record != null)
                    records.Add(record);
            }

            return records
                .OrderBy(r => KindOrder(r.Kind))
                .ThenBy(r => r.ContextKey, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Canonical rail ordering: global, then project, then workbench (Dossier), then task.</summary>
    private static int KindOrder(string kind) => kind switch
    {
        OrchestratorContextKey.GlobalKind => 0,
        OrchestratorContextKey.ProjectKind => 1,
        OrchestratorContextKey.WorkbenchKind => 2,
        _ => 3,
    };

    public OrchestratorSessionRecord GetOrCreate(string rawContextKey)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
            throw new ArgumentException("Invalid orchestrator context key.", nameof(rawContextKey));

        lock (_gate)
        {
            if (key.IsGlobal)
                EnsureGlobalMigratedOrCreated();

            var path = SessionFilePath(key);
            var existing = ReadRecord(path);
            if (existing != null)
                return existing;

            var now = DateTime.UtcNow;
            var created = EmptyRecord(key, now);
            WriteRecord(created);
            EnsureHistoryFile(key);
            _logger.LogInformation(
                "orchestrator-session-created contextKey={ContextKey} encodedKey={EncodedKey} kind={Kind}",
                created.ContextKey, created.EncodedKey, created.Kind);
            return created;
        }
    }

    public OrchestratorSessionRecord Update(string rawContextKey, Func<OrchestratorSessionRecord, OrchestratorSessionRecord> update)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
            throw new ArgumentException("Invalid orchestrator context key.", nameof(rawContextKey));

        lock (_gate)
        {
            var current = GetOrCreate(rawContextKey);
            var next = update(current);
            if (!string.Equals(next.ContextKey, current.ContextKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Cannot change an orchestrator session context key during update.");
            WriteRecord(next);
            EnsureHistoryFile(key);
            return next;
        }
    }

    public void AppendHistory(string rawContextKey, OrchestratorSessionHistoryEntry entry)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
            throw new ArgumentException("Invalid orchestrator context key.", nameof(rawContextKey));

        lock (_gate)
        {
            _ = GetOrCreate(rawContextKey);
            var path = HistoryFilePath(key);
            var line = JsonSerializer.Serialize(entry, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
        }
    }

    public string SessionDirectory(OrchestratorContextKey key)
    {
        var root = SessionsRoot ?? throw new InvalidOperationException("TaskRepository is not configured.");
        return Path.Combine(root, key.Encode());
    }

    public string SessionFilePath(OrchestratorContextKey key) => Path.Combine(SessionDirectory(key), SessionFileName);

    public string HistoryFilePath(OrchestratorContextKey key) => Path.Combine(SessionDirectory(key), HistoryFileName);

    private void EnsureGlobalMigratedOrCreated()
    {
        var root = SessionsRoot;
        if (root == null)
            return;

        var key = OrchestratorContextKey.Global;
        var path = SessionFilePath(key);
        if (File.Exists(path))
        {
            EnsureHistoryFile(key);
            return;
        }

        var legacy = _legacyGlobal.Read();
        var now = DateTime.UtcNow;
        var record = legacy == null
            ? EmptyRecord(key, now)
            : new OrchestratorSessionRecord(
                ContextKey: key.Value,
                EncodedKey: key.Encode(),
                Kind: key.Kind,
                ProjectId: null,
                TaskKey: null,
                CreatedAt: legacy.BootedAt,
                UpdatedAt: legacy.LastUsedAt,
                SessionId: legacy.SessionId,
                Model: legacy.Model,
                BootedAt: legacy.BootedAt,
                BootPromptPreview: legacy.BootPromptPreview,
                BootReplyPreview: legacy.BootReplyPreview,
                CumulativeInputTokens: legacy.CumulativeInputTokens,
                CumulativeOutputTokens: legacy.CumulativeOutputTokens,
                CumulativeCacheReadTokens: legacy.CumulativeCacheReadTokens,
                CumulativeCacheCreationTokens: legacy.CumulativeCacheCreationTokens,
                Calls: legacy.Calls,
                LastUsedAt: legacy.LastUsedAt,
                LastError: legacy.LastError);

        WriteRecord(record);
        EnsureHistoryFile(key);
        _logger.LogInformation(
            "orchestrator-session-global-migrated contextKey={ContextKey} hasLegacy={HasLegacy}",
            key.Value, legacy != null);
    }

    private static OrchestratorSessionRecord EmptyRecord(OrchestratorContextKey key, DateTime now) =>
        new(
            ContextKey: key.Value,
            EncodedKey: key.Encode(),
            Kind: key.Kind,
            ProjectId: key.ProjectId,
            TaskKey: key.TaskKey,
            CreatedAt: now,
            WorkbenchKey: key.WorkbenchKey,
            UpdatedAt: now,
            SessionId: null,
            Model: null,
            BootedAt: null,
            BootPromptPreview: null,
            BootReplyPreview: null,
            CumulativeInputTokens: 0,
            CumulativeOutputTokens: 0,
            CumulativeCacheReadTokens: 0,
            CumulativeCacheCreationTokens: 0,
            Calls: 0,
            LastUsedAt: null,
            LastError: null);

    private OrchestratorSessionRecord? ReadRecord(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<OrchestratorSessionRecord>(File.ReadAllText(path), ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read orchestrator session registry record at {Path}", path);
            return null;
        }
    }

    private void WriteRecord(OrchestratorSessionRecord record)
    {
        if (!OrchestratorContextKey.TryParse(record.ContextKey, out var key))
            throw new InvalidOperationException($"Cannot persist invalid context key '{record.ContextKey}'.");

        var dir = SessionDirectory(key);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, SessionFileName + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, WriteOpts), Encoding.UTF8);
        File.Move(tmp, Path.Combine(dir, SessionFileName), overwrite: true);
    }

    private void EnsureHistoryFile(OrchestratorContextKey key)
    {
        var dir = SessionDirectory(key);
        Directory.CreateDirectory(dir);
        var history = Path.Combine(dir, HistoryFileName);
        if (!File.Exists(history))
            File.WriteAllText(history, "", Encoding.UTF8);
    }
}
