using System.Text.Json;

namespace AgentStudio.Git;

/// <summary>
/// Durable sidecar for <see cref="WithheldCommitCandidateRecord"/>, written next
/// to <c>task.json</c> in the job folder. Same shape as the parked-blocker
/// marker: the folder moves with the file, so the record survives the lane move
/// into <c>5e-escalated</c>, a backend restart, and the scanner cache without
/// inventing another store.
/// </summary>
public static class WithheldCommitCandidateStore
{
    public const string FileName = "withheld-commit-candidates.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Persists the record, or clears a stale one when <paramref name="record"/>
    /// is null. Best-effort: the commit boundary has already made its decision
    /// when this runs, so a failed marker write must never undo it.
    /// </summary>
    public static void Persist(string? jobFolder, WithheldCommitCandidateRecord? record, ILogger? logger = null)
    {
        if (record is null || record.Count == 0) Clear(jobFolder, logger);
        else Write(jobFolder, record, logger);
    }

    public static void Write(string? jobFolder, WithheldCommitCandidateRecord record, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, FileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist withheld commit candidates in {Folder}", jobFolder);
        }
    }

    public static WithheldCommitCandidateRecord? TryRead(string? jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WithheldCommitCandidateRecord>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read withheld commit candidates in {Folder}", jobFolder);
            return null;
        }
    }

    public static void Clear(string? jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear withheld commit candidates in {Folder}", jobFolder);
        }
    }
}
