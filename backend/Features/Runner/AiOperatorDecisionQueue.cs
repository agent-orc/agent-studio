using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Orchestrator;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public interface IAiOperatorDecisionQueue
{
    void Enqueue(TaskInfo task, string occurrenceId, DecisionChoiceV1 choice);
    void RecoverPending() { }
}

/// <summary>Persists each held decision before requesting an AI Operator turn.</summary>
public sealed class AiOperatorDecisionQueue(
    OrchestratorTurnService turns,
    OrchestratorSessionRegistry sessions,
    IConfiguration configuration,
    ILogger<AiOperatorDecisionQueue> logger) : IAiOperatorDecisionQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private readonly string? _directory = string.IsNullOrWhiteSpace(configuration["TaskRepository"])
        ? null : Path.Combine(configuration["TaskRepository"]!, ".orchestration-ai-asks");

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
                    null, null, "pending"));
            Dispatch(path);
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
        if (ask.Status == "completed") return;
        var latest = LatestHistory(ask);
        if (latest?.Status == OrchestratorTurnService.StatusCompleted)
        {
            Save(path, ask with { TurnId = latest.TurnId, Status = "completed" });
            return;
        }
        if (ask.BootId == _bootId && latest?.Status is not
            (OrchestratorTurnService.StatusFailed or OrchestratorTurnService.StatusParked))
            return;
        if (latest?.Status is OrchestratorTurnService.StatusFailed or OrchestratorTurnService.StatusParked)
        {
            Save(path, ask with { TurnId = latest.TurnId, Status = latest.Status! });
            return;
        }
        var response = turns.Enqueue(ask.ContextKey, new OrchestratorTurnRequest(
            $"Decision occurrence {ask.OccurrenceId} needs an AI Operator answer. "
            + $"The decision step chose {ask.Action} ({ask.Target}). "
            + "Review the receipt and card, then decide the next permitted action. "
            + "Ask the human operator for any A2 or A3 mutation. "
            + "Do not mutate the card without the required authority."));
        Save(path, ask with { BootId = _bootId, TurnId = response.TurnId, Status = response.Status });
    }

    private OrchestratorSessionHistoryEntry? LatestHistory(PendingAsk ask)
    {
        if (!OrchestratorContextKey.TryParse(ask.ContextKey, out var key)) return null;
        var path = sessions.HistoryFilePath(key);
        if (!File.Exists(path)) return null;
        OrchestratorSessionHistoryEntry? latest = null;
        string? latestTurn = null;
        foreach (var line in File.ReadLines(path))
        {
            OrchestratorSessionHistoryEntry? entry;
            try { entry = JsonSerializer.Deserialize<OrchestratorSessionHistoryEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (entry is null || entry.Kind != "turn" || entry.PromptPreview is null
                || !entry.PromptPreview.StartsWith($"Decision occurrence {ask.OccurrenceId} ",
                    StringComparison.Ordinal)) continue;
            if (latestTurn != entry.TurnId)
            {
                latestTurn = entry.TurnId;
                latest = entry;
            }
            else if (entry.Status is OrchestratorTurnService.StatusCompleted
                     or OrchestratorTurnService.StatusFailed
                     or OrchestratorTurnService.StatusParked
                     || latest?.Status is not (OrchestratorTurnService.StatusCompleted
                         or OrchestratorTurnService.StatusFailed
                         or OrchestratorTurnService.StatusParked))
                latest = entry;
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
        string Target, string? BootId, string? TurnId, string Status);
}
