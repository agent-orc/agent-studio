using System.Text.Json;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

/// <summary>An applied engine batch plan that the batch tick of its scope has not taken yet.</summary>
public sealed record BatchGateEngineIntent(string RunId, string TaskId, BatchPlanDecision Decision,
    DateTimeOffset AtUtc);

/// <summary>
/// What the batch tick did with an engine batch plan: <c>applied</c>,
/// <c>expired</c>, <c>rejected</c> by the authority floors, or
/// <c>superseded</c> by a later plan of the same scope.
/// </summary>
public sealed record BatchGateEngineIntentReceipt(string RunId, string TaskId, string Outcome, string? Reason,
    DateTimeOffset AtUtc);

/// <summary>
/// AGT-3218: engine batch plans are durable. Until then the applier handed the
/// plan to an in-memory map, so a restart before the next tick dropped it and
/// nothing showed that it had ever arrived. The intent file survives a
/// restart; the receipt records the tick's action for the applier canary.
/// </summary>
public sealed partial class BatchGateStore
{
    public void WriteEngineIntent(BatchGateEngineIntent intent)
    {
        if (ReadEngineIntent(intent.TaskId) is { } older && older.RunId != intent.RunId
            && ReadEngineIntentReceipt(older.RunId) is null)
            RecordEngineIntentReceipt(new(older.RunId, older.TaskId, "superseded",
                $"Engine plan {intent.RunId} replaced it before the tick took it.", intent.AtUtc));
        var folder = Path.Combine(_root, "engine-intents");
        Directory.CreateDirectory(folder);
        Replace(Path.Combine(folder, SafeName(intent.TaskId) + ".json"), intent);
    }

    public BatchGateEngineIntent? ReadEngineIntent(string taskId)
    {
        var path = Path.Combine(_root, "engine-intents", SafeName(taskId) + ".json");
        return File.Exists(path) ? Read<BatchGateEngineIntent>(path) : null;
    }

    /// <summary>Removes and returns the scope's intent; the caller records what it did with it.</summary>
    public BatchGateEngineIntent? TakeEngineIntent(string taskId)
    {
        var path = Path.Combine(_root, "engine-intents", SafeName(taskId) + ".json");
        if (!File.Exists(path)) return null;
        var intent = Read<BatchGateEngineIntent>(path);
        File.Delete(path);
        return intent;
    }

    /// <summary>Records the tick's action once; a later record for the same plan is ignored.</summary>
    public void RecordEngineIntentReceipt(BatchGateEngineIntentReceipt receipt)
    {
        var folder = Path.Combine(_root, "engine-intent-receipts");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(receipt.RunId) + ".json");
        if (!File.Exists(path)) Replace(path, receipt);
    }

    public BatchGateEngineIntentReceipt? ReadEngineIntentReceipt(string runId)
    {
        var path = Path.Combine(_root, "engine-intent-receipts", SafeName(runId) + ".json");
        return File.Exists(path) ? Read<BatchGateEngineIntentReceipt>(path) : null;
    }

    private static void Replace<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
