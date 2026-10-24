using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Orchestrator;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

public interface IAiOperatorDecisionQueue
{
    void Enqueue(TaskInfo task, string occurrenceId, DecisionChoiceV1 choice);
    void RecoverPending();
}

public interface IAiOperatorTurnDispatcher
{
    OrchestratorTurnResponse Enqueue(string contextKey, OrchestratorTurnRequest request, string turnId);
}

public sealed class AiOperatorTurnDispatcher(OrchestratorTurnService turns) : IAiOperatorTurnDispatcher
{
    public OrchestratorTurnResponse Enqueue(string contextKey, OrchestratorTurnRequest request, string turnId)
        => turns.Enqueue(contextKey, request, turnId);
}

/// <summary>Persists each held decision before requesting an AI Operator turn.</summary>
public sealed class AiOperatorDecisionQueue(
    IAiOperatorTurnDispatcher turns,
    OrchestratorSessionRegistry sessions,
    IConfiguration configuration,
    ILogger<AiOperatorDecisionQueue> logger,
    TimeProvider time,
    TimelineLog timeline,
    OrchestratorLog operatorLog,
    TaskScannerService scanner) : IAiOperatorDecisionQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private readonly string? _directory = string.IsNullOrWhiteSpace(configuration["TaskRepository"])
        ? null : Path.Combine(configuration["TaskRepository"]!, ".orchestration-ai-asks");

    /// <summary>True when the AI Operator holds an ask for the decision occurrence.</summary>
    public static bool HasCustody(IConfiguration configuration, string occurrenceId)
        => !string.IsNullOrWhiteSpace(configuration["TaskRepository"])
            && Path.GetFileName(occurrenceId) == occurrenceId
            && File.Exists(Path.Combine(configuration["TaskRepository"]!, ".orchestration-ai-asks",
                occurrenceId + ".json"));

    public void Enqueue(TaskInfo task, string occurrenceId, DecisionChoiceV1 choice)
    {
        if (_directory is null)
            throw new InvalidOperationException("AI Operator ask custody requires TaskRepository.");
        if (Path.GetFileName(occurrenceId) != occurrenceId)
            throw new ArgumentException("Invalid decision occurrence id.", nameof(occurrenceId));
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, occurrenceId + ".json");
            if (!File.Exists(path))
                Save(path, new PendingAsk(occurrenceId,
                    $"task:{task.ProjectName}/{task.Key ?? task.Id}", choice.Action, choice.Target,
                    null, null, "pending", task.Id, task.FolderPath, task.WatchPath,
                    choice.Outcome));
            try { Dispatch(path); }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "ai-operator-ask-dispatch-pending occurrence={OccurrenceId}", occurrenceId);
            }
        }
    }

    public void RecoverPending()
    {
        if (_directory is null || !Directory.Exists(_directory)) return;
        lock (_gate)
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "orch_*.json"))
            {
                try { Dispatch(path); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "ai-operator-ask-recovery-pending path={Path}", path);
                }
            }
        }
    }

    private void Dispatch(string path)
    {
        var ask = JsonSerializer.Deserialize<PendingAsk>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The AI Operator ask record is empty.");
        if (ask.Attempts == 0 && ask.TurnId is not null)
            ask = ask with { Attempts = 1 };
        if (ask.Status is "completed" or "escalated") return;
        var latest = LatestHistory(ask);
        if (latest?.Status == OrchestratorTurnService.StatusCompleted
            && latest.TurnId == ask.TurnId)
        {
            Save(path, ask with { TurnId = latest.TurnId, Status = "completed" });
            return;
        }
        if (latest?.Status is OrchestratorTurnService.StatusFailed or OrchestratorTurnService.StatusParked)
        {
            if (latest.TurnId != ask.TurnId) return;
            var reason = $"AI Operator turn {latest.TurnId} {latest.Status}: "
                + (string.IsNullOrWhiteSpace(latest.Error) ? "no error detail was recorded" : latest.Error);
            if (ask.Attempts >= 2)
            {
                Escalate(path, ask, reason);
                return;
            }
            if (ask.Status != "retry-pending")
            {
                ask = ask with { TurnId = latest.TurnId, Status = "retry-pending",
                    FailureReason = reason, RetryAt = time.GetUtcNow().UtcDateTime + RetryBackoff };
                Save(path, ask);
            }
            if (time.GetUtcNow().UtcDateTime < ask.RetryAt) return;
        }
        else if (ask.BootId == _bootId && ask.Status != "dispatch-pending") return;
        // Save the identity before any turn can start. Recovery reuses it if dispatch
        // succeeds but its history write fails before Enqueue returns.
        var turnId = latest?.Status is OrchestratorTurnService.StatusFailed
            or OrchestratorTurnService.StatusParked
            ? Guid.NewGuid().ToString("N")
            : ask.TurnId ?? Guid.NewGuid().ToString("N");
        ask = ask with { BootId = _bootId, TurnId = turnId,
            Status = "dispatch-pending", Attempts = ask.Attempts + (turnId == ask.TurnId ? 0 : 1),
            RetryAt = null };
        Save(path, ask);
        var response = turns.Enqueue(ask.ContextKey, new OrchestratorTurnRequest(
            $"Decision occurrence {ask.OccurrenceId} needs an AI Operator answer. "
            + $"The decision step chose {ask.Action} ({ask.Target}). "
            + $"Reason: {ask.Reason ?? "model requested an ask"}. "
            + "Review the receipt and card, then decide the next permitted action. "
            + "Ask the human operator for any A2 or A3 mutation. "
            + "Do not mutate the card without the required authority."), turnId);
        Save(path, ask with { Status = response.Status });
    }

    private void Escalate(string path, PendingAsk ask, string reason)
    {
        var summary = $"AI Operator ask {ask.OccurrenceId} needs human attention after two turns. {reason}";
        var folder = !string.IsNullOrWhiteSpace(ask.TaskId) && !string.IsNullOrWhiteSpace(ask.WatchPath)
            ? scanner.FindJob(ask.TaskId, ask.WatchPath)?.FolderPath ?? ask.TaskFolder
            : ask.TaskFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            if (!timeline.Append(folder, new TimelineEvent
                {
                    Ts = time.GetUtcNow().UtcDateTime,
                    Kind = TimelineEventKinds.OrchestratorEscalated,
                    Actor = TimelineActors.System,
                    Summary = summary,
                    RunId = ask.OccurrenceId,
                }))
                throw new IOException("The AI Operator escalation could not be written to the card.");
        }
        if (!string.IsNullOrWhiteSpace(ask.WatchPath))
            operatorLog.Append(ask.WatchPath, new OrchestratorLogEntry
            {
                Ts = time.GetUtcNow().UtcDateTime,
                Kind = OrchestratorLogKinds.Alert,
                Topic = "ai-operator-ask:" + ask.OccurrenceId,
                JobId = ask.TaskId,
                Summary = summary,
                Reasoning = ask.Reason,
            });
        Save(path, ask with { Status = "escalated", FailureReason = reason });
    }

    private OrchestratorSessionHistoryEntry? LatestHistory(PendingAsk ask)
    {
        if (ask.TurnId is null || !OrchestratorContextKey.TryParse(ask.ContextKey, out var key))
            return null;
        var path = sessions.HistoryFilePath(key);
        if (!File.Exists(path)) return null;
        OrchestratorSessionHistoryEntry? latest = null;
        // Session history is written as indented JSON objects. Read complete
        // values so both existing indented entries and compact entries work.
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB
            && bytes[2] == 0xBF ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions
        {
            AllowMultipleValues = true,
        });
        try
        {
            while (reader.Read())
            {
                using var value = JsonDocument.ParseValue(ref reader);
                var entry = value.Deserialize<OrchestratorSessionHistoryEntry>(Json);
                if (entry is null || entry.Kind != "turn" || entry.TurnId != ask.TurnId)
                    continue;
                if (entry.Status is OrchestratorTurnService.StatusCompleted
                             or OrchestratorTurnService.StatusFailed
                             or OrchestratorTurnService.StatusParked
                    || latest?.Status is not (OrchestratorTurnService.StatusCompleted
                        or OrchestratorTurnService.StatusFailed
                        or OrchestratorTurnService.StatusParked))
                    latest = entry;
            }
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "AI Operator history has an incomplete trailing entry at {Path}", path);
        }
        return latest;
    }

    private static void Save(string path, PendingAsk ask)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, ask, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record PendingAsk(string OccurrenceId, string ContextKey, string Action,
        string Target, string? BootId, string? TurnId, string Status,
        string? TaskId = null, string? TaskFolder = null, string? WatchPath = null,
        string? Reason = null, int Attempts = 0, DateTime? RetryAt = null,
        string? FailureReason = null);
}
