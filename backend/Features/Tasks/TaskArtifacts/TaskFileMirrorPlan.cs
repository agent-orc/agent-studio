using AgentStudio.Retention;

namespace AgentStudio.Tasks;

/// <summary>One file of a task folder: path relative to the folder, forward-slashed.</summary>
public sealed record TaskFolderFile(string Path, string Sha256, long SizeBytes, string FullPath);

/// <summary>What one task needs sent: changed or new files, and paths that left the folder.</summary>
public sealed record TaskFileMirrorTaskDelta(
    string Project,
    string TaskKey,
    IReadOnlyList<TaskFolderFile> Changed,
    IReadOnlyList<string> Removed);

/// <summary>
/// Pure rules of the task-file mirror (AGT-3320). A task folder file is
/// mirrored when it is durable task state, evidence or a result: runtime files
/// and the heavy CLI and review logs stay out, exactly as they stay out of the
/// evidence commits, and so does a file above the size cap.
/// </summary>
public static class TaskFileMirrorPolicy
{
    public const long DefaultMaximumFileBytes = 16L * 1024 * 1024;
    private const int MaximumPathLength = 400;
    private static readonly ArtifactClassifier Classifier = new();

    public static bool Include(string relativePath, long sizeBytes, long maximumFileBytes)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > MaximumPathLength) return false;
        if (sizeBytes > maximumFileBytes) return false;
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.StartsWith(".git", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (relativePath.Contains(':')) return false;
        var classification = Classifier.Classify(relativePath);
        return classification.ArtifactClass switch
        {
            ArtifactClass.Runtime => false,
            ArtifactClass.HeavyWorkingData => classification.Family == "results",
            _ => true,
        };
    }

    /// <summary>
    /// Delta of one task against the hashes the Task Server last accepted.
    /// A task never accepted sends every file; nothing is removed for it.
    /// </summary>
    public static TaskFileMirrorTaskDelta Plan(
        string project,
        string taskKey,
        IReadOnlyDictionary<string, string>? accepted,
        IReadOnlyList<TaskFolderFile> current)
    {
        var changed = current
            .Where(file => accepted is null || !accepted.TryGetValue(file.Path, out var sha)
                                            || !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var paths = current.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var removed = accepted is null
            ? []
            : accepted.Keys.Where(path => !paths.Contains(path)).Order(StringComparer.Ordinal).ToList();
        return new TaskFileMirrorTaskDelta(project, taskKey, changed, removed);
    }
}
