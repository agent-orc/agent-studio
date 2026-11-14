namespace AgentStudio.Pipeline;

/// <summary>A durable, exact-run publication transition. A repeated step must name the same candidate.</summary>
public sealed record BatchPublicationStep(string BatchId, string BatchRunId, string CandidateSha,
    string Step, string? TaskKey, DateTimeOffset AtUtc);

public sealed partial class BatchGateStore
{
    public BatchPublicationStep? ReadPublicationStep(string batchId, string step, string? taskKey = null)
    {
        var path = PublicationStepPath(batchId, step, taskKey);
        return File.Exists(path) ? Read<BatchPublicationStep>(path) : null;
    }

    public void RecordPublicationStep(BatchPublicationStep entry)
    {
        var run = ReadRun(entry.BatchId, entry.BatchRunId);
        if (run.CandidateSha != entry.CandidateSha
            || entry.Step is not ("publishing" or "push-intent" or "remote-verified"
                or "receipt" or "published" or "attributed" or "released")
            || (entry.Step is "attributed" or "released") != (entry.TaskKey is not null)
            || entry.TaskKey is { } key && !ReadManifest(entry.BatchId).Members.Any(member => member.TaskKey == key))
            throw new InvalidDataException("Publication transition does not match its tested run.");
        var path = PublicationStepPath(entry.BatchId, entry.Step, entry.TaskKey);
        if (File.Exists(path))
        {
            var existing = Read<BatchPublicationStep>(path);
            if (existing.BatchId != entry.BatchId || existing.BatchRunId != entry.BatchRunId
                || existing.CandidateSha != entry.CandidateSha
                || existing.TaskKey != entry.TaskKey || existing.Step != entry.Step)
                throw new InvalidDataException("Publication transition belongs to a different SHA.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { WriteOnce(path, entry); }
        catch (IOException) when (File.Exists(path))
        {
            var existing = Read<BatchPublicationStep>(path);
            if (existing.BatchId != entry.BatchId || existing.BatchRunId != entry.BatchRunId
                || existing.CandidateSha != entry.CandidateSha
                || existing.TaskKey != entry.TaskKey || existing.Step != entry.Step)
                throw new InvalidDataException("Publication transition belongs to a different SHA.");
        }
    }

    private string PublicationStepPath(string batchId, string step, string? taskKey)
        => Path.Combine(BatchDirectory(batchId), "publication-steps",
            taskKey is null ? step + ".json" : step + "-" + SafeName(taskKey) + ".json");
}
