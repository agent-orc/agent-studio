using AgentStudio.Runner;

namespace AgentStudio.Orchestrator;

public sealed record OrchestratorTurnRequest(
    string Prompt,
    string? Model = null,
    string? WorkingDirectory = null,
    string? CliType = null,
    string? ThinkingLevel = null);

public sealed record OrchestratorTurnResponse(
    string ContextKey,
    string TurnId,
    string Status,
    int? QueuePosition,
    int ActiveCount,
    int ActiveLimit);

public sealed record OrchestratorParkResponse(
    string ContextKey,
    int ParkedQueuedTurns,
    bool CancelledActiveTurn);

public sealed record OrchestratorContextRuntimeStatus(
    string ContextKey,
    string Status,
    int QueuePosition);

internal sealed class OrchestratorTurnWorkItem
{
    public required string ContextKey { get; init; }
    public required string TurnId { get; init; }
    public required string Prompt { get; init; }
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? WorkingDirectory { get; init; }
}

public sealed class OrchestratorTurnService
{
    public const string StatusActive = "active";
    public const string StatusQueued = "queued";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";
    public const string StatusParked = "parked";

    private readonly OrchestratorSessionRegistry _registry;
    private readonly OrchestratorRunner _runner;
    private readonly IConfiguration _config;
    private readonly ILogger<OrchestratorTurnService> _logger;
    private readonly OrchestratorContextDigestService? _contextDigests;
    private readonly OrchestratorWorkbenchPromptContextComposer? _workbenchPromptContext;
    private readonly StartupExecutionAdmission? _executionAdmission;
    private readonly object _gate = new();
    private readonly Queue<OrchestratorTurnWorkItem> _queued = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _parked = new(StringComparer.Ordinal);

    public OrchestratorTurnService(
        OrchestratorSessionRegistry registry,
        OrchestratorRunner runner,
        IConfiguration config,
        ILogger<OrchestratorTurnService> logger,
        OrchestratorContextDigestService? contextDigests = null,
        StartupExecutionAdmission? executionAdmission = null,
        OrchestratorWorkbenchPromptContextComposer? workbenchPromptContext = null)
    {
        _registry = registry;
        _runner = runner;
        _config = config;
        _logger = logger;
        _contextDigests = contextDigests;
        _executionAdmission = executionAdmission;
        _workbenchPromptContext = workbenchPromptContext;
    }

    public OrchestratorTurnResponse Enqueue(string rawContextKey, OrchestratorTurnRequest request)
    {
        _executionAdmission?.Demand(ExecutionAdmissionPath.Chat);
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
            throw new ArgumentException("Invalid orchestrator context key.", nameof(rawContextKey));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.CliType) && !CliTypes.IsValid(request.CliType))
            throw new ArgumentException("CLI type is invalid.", nameof(request));

        var item = new OrchestratorTurnWorkItem
        {
            ContextKey = key.Value,
            TurnId = Guid.NewGuid().ToString("N"),
            Prompt = request.Prompt,
            Model = request.Model,
            CliType = request.CliType,
            ThinkingLevel = request.ThinkingLevel,
            WorkingDirectory = request.WorkingDirectory
        };

        _registry.GetOrCreate(key.Value);

        lock (_gate)
        {
            _parked.Remove(key.Value);
            var limit = ActiveLimit;
            if (_active.Count < limit)
            {
                StartLocked(item);
                _registry.AppendHistory(key.Value, History(item, StatusActive, queuePosition: null));
                return new OrchestratorTurnResponse(key.Value, item.TurnId, StatusActive, null, _active.Count, limit);
            }

            _queued.Enqueue(item);
            var position = QueuePositionLocked(item.TurnId);
            _registry.AppendHistory(key.Value, History(item, StatusQueued, queuePosition: position));
            return new OrchestratorTurnResponse(key.Value, item.TurnId, StatusQueued, position, _active.Count, limit);
        }
    }

    public OrchestratorParkResponse Park(string rawContextKey)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var key))
            throw new ArgumentException("Invalid orchestrator context key.", nameof(rawContextKey));

        var parkedQueued = 0;
        var cancelledActive = false;
        lock (_gate)
        {
            _parked.Add(key.Value);
            var keep = new Queue<OrchestratorTurnWorkItem>();
            while (_queued.TryDequeue(out var item))
            {
                if (string.Equals(item.ContextKey, key.Value, StringComparison.Ordinal))
                {
                    parkedQueued++;
                    _registry.AppendHistory(key.Value, History(item, StatusParked, queuePosition: null));
                }
                else
                {
                    keep.Enqueue(item);
                }
            }
            while (keep.TryDequeue(out var item))
                _queued.Enqueue(item);

            foreach (var pair in _active.Where(p => p.Key.StartsWith(key.Value + "|", StringComparison.Ordinal)).ToList())
            {
                cancelledActive = true;
                pair.Value.Cancel();
            }
        }

        _registry.AppendHistory(key.Value, new OrchestratorSessionHistoryEntry(
            DateTime.UtcNow, "park", Guid.NewGuid().ToString("N"), StatusParked,
            null, null, null, null, null, null));

        return new OrchestratorParkResponse(key.Value, parkedQueued, cancelledActive);
    }

    public IReadOnlyList<OrchestratorContextRuntimeStatus> SnapshotStatuses()
    {
        lock (_gate)
        {
            var active = _active.Keys
                .Select(key => key[..key.LastIndexOf('|')])
                .Distinct(StringComparer.Ordinal)
                .Select(key => new OrchestratorContextRuntimeStatus(key, StatusActive, 0));
            var queued = _queued
                .Select((item, index) => new OrchestratorContextRuntimeStatus(item.ContextKey, StatusQueued, index + 1));
            var parked = _parked
                .Where(key => !_active.Keys.Any(activeKey => activeKey.StartsWith(key + "|", StringComparison.Ordinal)))
                .Select(key => new OrchestratorContextRuntimeStatus(key, StatusParked, 0));
            return active.Concat(queued).Concat(parked).ToList();
        }
    }

    private int ActiveLimit => Math.Max(1, _config.GetValue("Orchestrator:SessionTurns:ActiveLimit", 4));

    private void StartLocked(OrchestratorTurnWorkItem item)
    {
        var cts = new CancellationTokenSource();
        _active[ActiveKey(item)] = cts;
        _ = Task.Run(() => RunOneAsync(item, cts.Token));
    }

    private async Task RunOneAsync(OrchestratorTurnWorkItem item, CancellationToken ct)
    {
        try
        {
            var before = _registry.GetOrCreate(item.ContextKey);
            var workingDirectory = ResolveWorkingDirectory(item);
            var model = string.IsNullOrWhiteSpace(item.Model) ? before.Model : item.Model;
            var cliType = string.IsNullOrWhiteSpace(item.CliType)
                ? CliTypes.Claude
                : item.CliType.Trim().ToLowerInvariant();
            var prompt = await BuildPromptAsync(item, ct).ConfigureAwait(false);
            OrchestratorDecisionResult result;

            if (cliType == CliTypes.Claude && !string.IsNullOrWhiteSpace(before.SessionId))
            {
                var rejected = false;
                result = await _runner.ResumeWithFallbackAsync(
                    before.SessionId!,
                    prompt,
                    fallbackPromptBuilder: () => prompt,
                    onSessionRejected: () => rejected = true,
                    model,
                    workingDirectory,
                    ct).ConfigureAwait(false);
                if (rejected)
                {
                    _registry.Update(item.ContextKey, r => r with
                    {
                        SessionId = null,
                        UpdatedAt = DateTime.UtcNow,
                        LastError = "resume rejected"
                    });
                }
            }
            else if (cliType != CliTypes.Claude)
            {
                result = await _runner.DecideWithCliAsync(
                    cliType,
                    prompt,
                    model,
                    item.ThinkingLevel,
                    workingDirectory,
                    ct).ConfigureAwait(false);
            }
            else
            {
                result = await _runner.DecideAsync(prompt, model, workingDirectory, ct).ConfigureAwait(false);
            }

            PersistResult(item, result);
        }
        catch (OperationCanceledException)
        {
            _registry.AppendHistory(item.ContextKey, History(item, StatusParked, error: "cancelled", queuePosition: null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "orchestrator-session-turn failed contextKey={ContextKey} turnId={TurnId}", item.ContextKey, item.TurnId);
            _registry.Update(item.ContextKey, r => r with
            {
                UpdatedAt = DateTime.UtcNow,
                Calls = r.Calls + 1,
                LastUsedAt = DateTime.UtcNow,
                LastError = ex.Message
            });
            _registry.AppendHistory(item.ContextKey, History(item, StatusFailed, error: ex.Message, queuePosition: null));
        }
        finally
        {
            lock (_gate)
            {
                if (_active.Remove(ActiveKey(item), out var cts))
                    cts.Dispose();
                StartQueuedUntilFullLocked();
            }
        }
    }

    private void PersistResult(OrchestratorTurnWorkItem item, OrchestratorDecisionResult result)
    {
        var now = DateTime.UtcNow;
        _registry.Update(item.ContextKey, r => r with
        {
            UpdatedAt = now,
            SessionId = !string.IsNullOrWhiteSpace(item.CliType)
                        && !string.Equals(item.CliType, "claude", StringComparison.OrdinalIgnoreCase)
                ? null
                : string.IsNullOrWhiteSpace(result.CapturedSessionId) ? r.SessionId : result.CapturedSessionId,
            Model = string.IsNullOrWhiteSpace(result.Model) ? r.Model : result.Model,
            CumulativeInputTokens = r.CumulativeInputTokens + (result.TokenUsage?.InputTokens ?? 0),
            CumulativeOutputTokens = r.CumulativeOutputTokens + (result.TokenUsage?.OutputTokens ?? 0),
            CumulativeCacheReadTokens = r.CumulativeCacheReadTokens + (result.TokenUsage?.CacheReadTokens ?? 0),
            CumulativeCacheCreationTokens = r.CumulativeCacheCreationTokens + (result.TokenUsage?.CacheCreationTokens ?? 0),
            Calls = r.Calls + 1,
            LastUsedAt = now,
            LastError = result.Success ? null : result.ErrorMessage
        });

        _registry.AppendHistory(item.ContextKey, History(
            item,
            result.Success ? StatusCompleted : StatusFailed,
            replyPreview: Preview(result.ReplyText),
            model: result.Model,
            sessionId: result.CapturedSessionId,
            error: result.ErrorMessage,
            queuePosition: null));
    }

    private void StartQueuedUntilFullLocked()
    {
        var limit = ActiveLimit;
        while (_active.Count < limit && _queued.TryDequeue(out var next))
        {
            StartLocked(next);
            _registry.AppendHistory(next.ContextKey, History(next, StatusActive, queuePosition: null));
        }
    }

    private int QueuePositionLocked(string turnId)
    {
        var i = 1;
        foreach (var queued in _queued)
        {
            if (queued.TurnId == turnId)
                return i;
            i++;
        }
        return i;
    }

    private string ResolveWorkingDirectory(OrchestratorTurnWorkItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
            return item.WorkingDirectory!;
        if (OrchestratorContextKey.TryParse(item.ContextKey, out var context)
            && context.Kind == OrchestratorContextKey.WorkbenchKind)
        {
            var dossierRoot = _workbenchPromptContext?
                .Compose(context.ProjectId!, context.WorkbenchKey)
                ?.RepositoryRoot;
            if (!string.IsNullOrWhiteSpace(dossierRoot) && Directory.Exists(dossierRoot))
                return dossierRoot;
        }
        var root = _registry.TaskRepositoryRoot;
        return string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root!;
    }

    private async Task<string> BuildPromptAsync(OrchestratorTurnWorkItem item, CancellationToken ct)
    {
        // A Dossier (workbench) session is deliberately isolated: it never
        // reads the board/task digest, only its own descriptor + entrypoint
        // excerpt. This branch never falls through to _contextDigests below.
        if (OrchestratorContextKey.TryParse(item.ContextKey, out var key)
            && key.Kind == OrchestratorContextKey.WorkbenchKind)
        {
            try
            {
                var workbench = _workbenchPromptContext?.Compose(key.ProjectId!, key.WorkbenchKey);
                if (workbench != null)
                    return workbench.PromptBlock + "\n\n=== USER MESSAGE ===\n" + item.Prompt;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "orchestrator_workbench_context_injection_failed contextKey={ContextKey} turnId={TurnId}",
                    item.ContextKey,
                    item.TurnId);
            }
            return item.Prompt;
        }

        if (_contextDigests == null) return item.Prompt;
        try
        {
            var digest = await _contextDigests.BuildAsync(item.ContextKey, ct: ct).ConfigureAwait(false);
            return digest.Digest + "\n\n=== USER MESSAGE ===\n" + item.Prompt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Context is read-only enrichment. A temporarily unavailable source
            // must not strand a queued user turn, so dispatch the original prompt
            // and leave a structured warning for diagnosis.
            _logger.LogWarning(
                ex,
                "orchestrator_context_digest_injection_failed contextKey={ContextKey} turnId={TurnId}",
                item.ContextKey,
                item.TurnId);
            return item.Prompt;
        }
    }

    private static string ActiveKey(OrchestratorTurnWorkItem item) => item.ContextKey + "|" + item.TurnId;

    private static OrchestratorSessionHistoryEntry History(
        OrchestratorTurnWorkItem item,
        string status,
        string? replyPreview = null,
        string? model = null,
        string? sessionId = null,
        string? error = null,
        int? queuePosition = null) =>
        new(
            DateTime.UtcNow,
            "turn",
            item.TurnId,
            status,
            Preview(item.Prompt),
            replyPreview,
            model,
            sessionId,
            error,
            queuePosition,
            item.CliType,
            item.ThinkingLevel);

    private static string Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var collapsed = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length <= 600 ? collapsed : collapsed[..600] + "...";
    }
}
