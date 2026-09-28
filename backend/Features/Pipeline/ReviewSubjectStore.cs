using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Canonical, fenced handoff from a completed coding run to completion review.
/// Remote review must read this record instead of reconstructing a subject from
/// local session events that do not exist on the Task Server.
/// </summary>
public sealed record ReviewSubjectRecord
{
    public int Version { get; init; } = 3;
    public string TaskKey { get; init; } = "";
    /// <summary>
    /// RunAttempt that settled the immutable delivery represented by this
    /// sidecar. Acceptance must match it against the authority store's current
    /// settled attempt before trusting any ref from this record.
    /// </summary>
    public string RunAttemptId { get; init; } = "";
    public string Project { get; init; } = "";
    public string Repository { get; init; } = "";
    public string ResultSha { get; init; } = "";
    /// <summary>
    /// Immutable checkout base captured when the source RunAttempt was leased.
    /// It remains the attribution boundary even when the integration branch
    /// already contains <see cref="ResultSha"/> by completion time.
    /// </summary>
    public string? BaseSha { get; init; }
    public string AttemptChainId { get; init; } = "";
    public string Executor { get; init; } = "";
    public string LeaseId { get; init; } = "";
    public long FencingToken { get; init; }
    /// <summary>
    /// Immutable delivery ref from the source RunAttempt's ResultEnvelope.
    /// This is distinct from the legacy <see cref="ResultRef"/>, which may have
    /// named a mutable salvage branch.
    /// </summary>
    public string? ImmutableResultRef { get; init; }
    public string? ResultRef { get; init; }
    public string? IntegrationBranch { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}

public static class ReviewSubjectStore
{
    public const string FileName = "review-subject.json";

    private static readonly Regex FullSha = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);

    public static bool IsValidResultSha(string? value)
        => !string.IsNullOrWhiteSpace(value) && FullSha.IsMatch(value);

    public static void Write(string taskFolder, ReviewSubjectRecord subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(subject);
        if (!IsValidResultSha(subject.ResultSha))
            throw new ArgumentException("ResultSha must be a full Git commit SHA.", nameof(subject));
        if (string.IsNullOrWhiteSpace(subject.RunAttemptId))
            throw new ArgumentException("RunAttemptId is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(subject.AttemptChainId))
            throw new ArgumentException("AttemptChainId is required.", nameof(subject));

        var path = PathFor(taskFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(subject, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "ReviewSubjectStore: temporary file cleanup");
                // Best effort cleanup of a file that was never authoritative.
            }
        }
    }

    /// <summary>
    /// Removes the canonical subject before a transition that opens a new run
    /// generation. The most recently invalidated subject remains as diagnostic
    /// evidence but can no longer be consumed by review or integration.
    /// </summary>
    public static void InvalidateForNewAttempt(string taskFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return;

        File.Move(path, path + ".invalidated", overwrite: true);
    }

    /// <summary>
    /// Verifies that a folder-scoped subject belongs to the task in that folder
    /// and to its current settled RunAttempt. Call this before acceptance or
    /// integration trusts any ref carried by the sidecar.
    /// </summary>
    public static bool TryValidateCurrentAttempt(
        string taskFolder,
        ReviewSubjectRecord subject,
        AttemptAuthorityService authority,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(authority);

        var taskKey = ReadTaskKey(taskFolder);
        if (string.IsNullOrWhiteSpace(taskKey))
        {
            error = "The accepted task has no stable key for review-subject validation.";
            return false;
        }
        if (!string.Equals(subject.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Review subject belongs to '{subject.TaskKey}', but the accepted task is '{taskKey}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(subject.RunAttemptId))
        {
            error = $"Review subject for '{taskKey}' has no RunAttemptId and cannot be accepted.";
            return false;
        }

        var current = authority.GetTaskProjection(taskKey).CurrentRunAttempt;
        if (current is null)
        {
            error = $"Review subject for '{taskKey}' has no current RunAttempt in the authority store.";
            return false;
        }
        if (!string.Equals(subject.RunAttemptId, current.AttemptId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Review subject RunAttempt '{subject.RunAttemptId}' is stale; current RunAttempt is '{current.AttemptId}'.";
            return false;
        }
        if (current.State != AttemptLifecycleState.Completed)
        {
            error = $"Review subject RunAttempt '{subject.RunAttemptId}' is not the current settled delivery.";
            return false;
        }
        if (!string.Equals(subject.ResultSha, current.ResultSha, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Review subject ResultSha does not match current RunAttempt '{current.AttemptId}'.";
            return false;
        }
        if (current.ResultEnvelope?.ImmutableRemoteRef is { } expectedRef
            && !string.Equals(subject.ImmutableResultRef, expectedRef, StringComparison.Ordinal))
        {
            error = $"Review subject result ref does not match current RunAttempt '{current.AttemptId}'.";
            return false;
        }

        error = null;
        return true;
    }

    public static ReviewSubjectRecord? Read(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try
        {
            var subject = JsonSerializer.Deserialize<ReviewSubjectRecord>(File.ReadAllText(path), Json);
            return subject is not null
                   && IsValidResultSha(subject.ResultSha)
                   && !string.IsNullOrWhiteSpace(subject.AttemptChainId)
                ? subject
                : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ReviewSubjectStore: malformed or unreadable subject");
            return null;
        }
    }

    private static string? ReadTaskKey(string taskFolder)
    {
        // Flat task storage makes the folder name the stable task key:
        // <root>/tasks/<bucket>/<KEY>. Resolve that authority before opening
        // task.json so repository-embedded task stores remain valid while a
        // concurrent metadata rewrite temporarily makes the JSON unreadable.
        // The bucket check prevents an arbitrary legacy slug folder from being
        // mistaken for a stable key.
        var flatStorageKey = ReadFlatStorageTaskKey(taskFolder);
        if (!string.IsNullOrWhiteSpace(flatStorageKey)) return flatStorageKey;

        try
        {
            var path = Path.Combine(taskFolder, "task.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            foreach (var propertyName in new[] { "key", "taskKey", "id" })
            {
                var property = root.EnumerateObject().FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
                var value = property.Value;
                if (value.ValueKind != JsonValueKind.Undefined
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ReviewSubjectStore: task key read failed");
        }
        return null;
    }

    private static string? ReadFlatStorageTaskKey(string taskFolder)
    {
        try
        {
            var taskDirectory = new DirectoryInfo(Path.GetFullPath(taskFolder));
            var bucketDirectory = taskDirectory.Parent;
            var tasksDirectory = bucketDirectory?.Parent;
            if (bucketDirectory is null
                || tasksDirectory is null
                || !string.Equals(
                    tasksDirectory.Name,
                    TaskStorageLayout.JobsDirName,
                    StringComparison.OrdinalIgnoreCase)
                || !TaskStorageLayout.TryParseKeyNumber(taskDirectory.Name, out var keyNumber)
                || !string.Equals(
                    bucketDirectory.Name,
                    TaskStorageLayout.Bucket(keyNumber),
                    StringComparison.Ordinal))
            {
                return null;
            }

            return taskDirectory.Name;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ReviewSubjectStore: flat-storage task key resolution failed");
            return null;
        }
    }
}
