using System.Text.Json;
using AgentStudio.Shared;

namespace AgentStudio.Pipeline;

public sealed record BatchGateOwnership(
    string ReviewAttemptId, BatchGateSubject Subject,
    string? BatchId = null, string? BatchRunId = null,
    string? FallbackTestedSha = null, string? FallbackEvidencePath = null,
    bool FallbackIntegrated = false, bool FallbackGateActive = false,
    int FallbackGateStarts = 0, string? ContainedInSha = null);

/// <summary>Card-local admission marker. Its presence makes lane release fail closed.</summary>
public static class BatchGateOwnershipStore
{
    private const string Name = "batch-gate-ownership.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BatchGateOwnership? Read(string taskFolder)
    {
        var path = Path.Combine(TaskPaths.LogsDir(taskFolder), Name);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<BatchGateOwnership>(File.ReadAllBytes(path), Json)
              ?? throw new InvalidDataException("batch-gate-evidence-missing")
            : null;
    }

    public static void Write(string taskFolder, BatchGateOwnership ownership)
    {
        var folder = TaskPaths.LogsDir(taskFolder);
        Directory.CreateDirectory(folder);
        using var guard = Guard(folder);
        var path = Path.Combine(folder, Name);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, ownership, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    // A marker belongs to one review generation. Superseding that review must
    // not impose its gate on a later per-task delivery.
    public static void ClearIfReviewAttempt(string taskFolder, string reviewAttemptId)
    {
        var folder = TaskPaths.LogsDir(taskFolder);
        if (!Directory.Exists(folder)) return;
        using var guard = Guard(folder);
        var path = Path.Combine(folder, Name);
        if (Read(taskFolder)?.ReviewAttemptId == reviewAttemptId)
            File.Delete(path);
    }

    private static FileStream Guard(string folder)
        => new(Path.Combine(folder, "batch-gate-ownership.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static BatchGateOwnership RecordContainment(string taskFolder,
        BatchGateOwnership ownership, string containedInSha, TimeProvider time,
        bool fallbackIntegrated = false)
    {
        var name = $"batch-contained-{ownership.ReviewAttemptId}.json";
        var folder = TaskPaths.LogsDir(taskFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new
            {
                ownership.ReviewAttemptId,
                ownership.Subject.TaskKey,
                ownership.Subject.ResultSha,
                ownership.Subject.IntegrationBranch,
                ContainedInSha = containedInSha,
                RecordedAtUtc = time.GetUtcNow(),
            });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        var contained = ownership with
        {
            FallbackEvidencePath = name,
            FallbackIntegrated = fallbackIntegrated,
            ContainedInSha = containedInSha,
        };
        Write(taskFolder, contained);
        return contained;
    }

    public static bool IsCurrentGeneration(BatchGateOwnership ownership,
        AttemptAuthorityProjection projection)
        => projection.CurrentRunAttempt?.AttemptId == ownership.Subject.RunAttempt
           && projection.CurrentReviewAttempt?.AttemptId == ownership.ReviewAttemptId
           && projection.CurrentReviewAttempt.Outcome is
               (ReviewTerminalOutcome.Pass or ReviewTerminalOutcome.IntegrationBranchDefect)
           && projection.CurrentRunAttempt.LastFence == ownership.Subject.FencingToken
           && projection.CurrentRunAttempt.AuthorityEpoch == ownership.Subject.DeliveryEpoch
           && string.Equals(projection.CurrentRunAttempt.ResultEnvelope?.ResultSha,
               ownership.Subject.ResultSha, StringComparison.OrdinalIgnoreCase)
           && projection.CurrentRunAttempt.ResultEnvelope?.ImmutableRemoteRef
               == ownership.Subject.ResultRef;

    public static string? ReleaseFailure(BatchGateOwnership ownership,
        AttemptAuthorityProjection projection, BatchGateStore store,
        string taskFolder)
    {
        if (projection.CurrentReviewAttempt is { } currentReview
            && currentReview.AttemptId != ownership.ReviewAttemptId)
            return null;
        var current = ownership.Subject with
        {
            CurrentGeneration = IsCurrentGeneration(ownership, projection),
        };
        // AGT-3103: a member the integration branch already contained left
        // the queue without a gate; its evidence is the containment record.
        if (ownership.ContainedInSha is not null)
            return ownership.FallbackIntegrated && current.CurrentGeneration
                && ownership.FallbackEvidencePath is { } containment
                && Path.GetFileName(containment) == containment
                && File.Exists(Path.Combine(TaskPaths.LogsDir(taskFolder), containment))
                ? null : "batch-gate-evidence-missing";
        if (ownership.FallbackTestedSha is not null)
            return ownership.FallbackIntegrated && current.CurrentGeneration
                && string.Equals(ownership.FallbackTestedSha,
                    current.ResultSha, StringComparison.OrdinalIgnoreCase)
                && ownership.FallbackEvidencePath is { } path
                && Path.GetFileName(path) == path
                && File.Exists(Path.Combine(TaskPaths.LogsDir(taskFolder), path))
                ? null : "batch-gate-evidence-missing";
        if (ownership.BatchId is null || ownership.BatchRunId is null)
            return "batch-gate-evidence-missing";
        return store.CheckRelease(current,
            ownership.BatchId, ownership.BatchRunId).FailureCode;
    }

}
