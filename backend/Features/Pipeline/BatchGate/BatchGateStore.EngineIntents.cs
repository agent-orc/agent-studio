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
/// The applier writes and the tick takes under one lock, and the tick claims
/// the file by an atomic rename, so a plan written while the tick takes the
/// older one stays for the next tick and is never deleted unread.
/// </summary>
public sealed partial class BatchGateStore
{
    /// <summary>
    /// How long a tick receipt is kept. The canary reads a receipt at most one
    /// apply deadline (at most a day) after the plan; older ones are pruned.
    /// </summary>
    internal static readonly TimeSpan EngineIntentReceiptRetention = TimeSpan.FromDays(7);

    private static readonly object EngineIntentGate = new();

    public void WriteEngineIntent(BatchGateEngineIntent intent)
    {
        lock (EngineIntentGate)
        {
            if (ReadEngineIntent(intent.TaskId) is { } older && older.RunId != intent.RunId)
                RecordEngineIntentReceipt(new(older.RunId, older.TaskId, "superseded",
                    $"Engine plan {intent.RunId} replaced it before the tick took it.", intent.AtUtc));
            var folder = Path.Combine(_root, "engine-intents");
            Directory.CreateDirectory(folder);
            Replace(Path.Combine(folder, SafeName(intent.TaskId) + ".json"), intent);
        }
    }

    public BatchGateEngineIntent? ReadEngineIntent(string taskId)
    {
        var path = Path.Combine(_root, "engine-intents", SafeName(taskId) + ".json");
        return File.Exists(path) ? Read<BatchGateEngineIntent>(path) : null;
    }

    /// <summary>
    /// Removes and returns the scope's intent; the caller records what it did
    /// with it. The rename claims exactly the file it reads.
    /// </summary>
    public BatchGateEngineIntent? TakeEngineIntent(string taskId)
    {
        var path = Path.Combine(_root, "engine-intents", SafeName(taskId) + ".json");
        lock (EngineIntentGate)
        {
            if (!File.Exists(path)) return null;
            var claimed = path + "." + Guid.NewGuid().ToString("N") + ".claimed";
            File.Move(path, claimed);
            try { return Read<BatchGateEngineIntent>(claimed); }
            finally { File.Delete(claimed); }
        }
    }

    /// <summary>
    /// Records the tick's action once; a later record for the same plan is
    /// ignored. Prunes receipts older than <see cref="EngineIntentReceiptRetention"/>.
    /// </summary>
    public void RecordEngineIntentReceipt(BatchGateEngineIntentReceipt receipt)
    {
        var folder = Path.Combine(_root, "engine-intent-receipts");
        lock (EngineIntentGate)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeName(receipt.RunId) + ".json");
            if (!File.Exists(path)) Replace(path, receipt);
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Where(file => file != path))
            {
                try
                {
                    if (receipt.AtUtc - Read<BatchGateEngineIntentReceipt>(file).AtUtc > EngineIntentReceiptRetention)
                        File.Delete(file);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
                {
                    // An unreadable receipt stays for an operator; it never blocks the tick's record.
                    SilentCatch.Note(exception, "BatchGateStore: engine intent receipt not pruned");
                }
            }
        }
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
