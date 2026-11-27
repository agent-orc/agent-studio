namespace AgentStudio.Pipeline;

/// <summary>Durable recovery budget, retained independently of evidence retention.</summary>
public sealed record BatchPublicationRecovery(int Attempts, string Reason,
    DateTimeOffset AtUtc, bool Settled);

public sealed partial class BatchGateStore
{
    public BatchPublicationRecovery? ReadPublicationRecovery(string batchId)
    {
        var folder = Path.Combine(BatchDirectory(batchId), "publication-recovery");
        var path = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault() : null;
        return path is null ? null : Read<BatchPublicationRecovery>(path);
    }

    public void RecordPublicationRecovery(string batchId, BatchPublicationRecovery recovery)
    {
        var folder = Path.Combine(BatchDirectory(batchId), "publication-recovery");
        Directory.CreateDirectory(folder);
        WriteOnce(Path.Combine(folder, $"{recovery.Attempts:D4}.json"), recovery);
    }
}
