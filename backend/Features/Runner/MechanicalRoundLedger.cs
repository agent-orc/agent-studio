using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>Task-local, append-only receipt of each fenced coding generation.</summary>
public static class MechanicalRoundLedger
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string FileName = "mechanical-round-ledger.jsonl";

    public static bool Append(
        string taskFolder,
        string attemptId,
        string outcome,
        string? resultSha,
        MechanicalRoundReceiptDto? receipt)
    {
        if (receipt is null) return true;
        var path = Path.Combine(taskFolder, "logs", FileName);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        var existing = JsonSerializer.Deserialize<MechanicalRoundLedgerEntry>(line, Json);
                        if (string.Equals(existing?.AttemptId, attemptId, StringComparison.Ordinal)) return true;
                    }
                    catch (JsonException ex)
                    {
                        SilentCatch.Note(ex, "A torn mechanical-round ledger line does not hide later receipts.");
                    }
                }
            }
            var entry = new MechanicalRoundLedgerEntry(
                attemptId, DateTime.UtcNow, outcome, resultSha, receipt);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(entry, Json));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
    }
}

public sealed record MechanicalRoundLedgerEntry(
    string AttemptId,
    DateTime RecordedAtUtc,
    string Outcome,
    string? ResultSha,
    MechanicalRoundReceiptDto Receipt);
