using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

internal static class TaskProjectionCards
{
    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.Ordinal);

    internal static TaskInfo[] Select(IReadOnlyCollection<TaskInfo> tasks, ILogger? logger = null)
    {
        return tasks.GroupBy(task => task.TaskKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var candidates = group.ToArray();
                if (candidates.Length == 1) return candidates[0];
                var ordered = candidates.OrderBy(task => task.State == TaskStates.Archive ? 1 : 0)
                    .ThenByDescending(task => File.GetLastWriteTimeUtc(Path.Combine(task.FolderPath, "task.json")))
                    .ThenBy(task => task.FolderPath, StringComparer.Ordinal)
                    .ToArray();
                if (logger is not null && ordered.Length > 1
                    && Warned.TryAdd($"{ordered[0].WatchPath}::{ordered[0].Id}", 0))
                    logger.LogWarning("duplicate-task-id taskKey={TaskKey} count={Count} selected={FolderPath}",
                        group.Key, ordered.Length, ordered[0].FolderPath);
                return ordered[0];
            }).ToArray();
    }

    internal static IReadOnlyList<DuplicateTaskId> FindDuplicateIds(IReadOnlyCollection<TaskInfo> tasks)
        => tasks.GroupBy(task => task.WatchPath, FileSystemPathComparer.Instance)
            .SelectMany(project => project.GroupBy(task => task.Id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new DuplicateTaskId(project.Key, group.Key,
                    group.Select(task => task.FolderPath).Order(StringComparer.Ordinal).ToArray())))
            .OrderBy(row => row.ProjectPath, StringComparer.Ordinal)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();

    internal static void WarnDuplicateIds(IReadOnlyCollection<TaskInfo> tasks, ILogger logger)
    {
        foreach (var duplicate in FindDuplicateIds(tasks))
            if (Warned.TryAdd($"{duplicate.ProjectPath}::{duplicate.Id}", 0))
                logger.LogWarning("duplicate-task-id project={ProjectPath} id={Id} folders={Folders}",
                    duplicate.ProjectPath, duplicate.Id, duplicate.Folders);
    }
}

public sealed record DuplicateTaskId(string ProjectPath, string Id, IReadOnlyList<string> Folders);
