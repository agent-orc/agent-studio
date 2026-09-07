namespace AgentStudio.Git;

public sealed record ArchivedResultRefPruneReport(
    int ArchivedCards,
    int Candidates,
    int Deleted,
    IReadOnlyList<string> DeletedRefs);

/// <summary>
/// Removes only local remote-tracking copies of indexed result and quarantine
/// refs for archived cards. It never contacts origin and never derives a broad
/// namespace from a task key.
/// </summary>
public sealed class ArchivedResultRefPruner(
    GitService git,
    TaskScannerService tasks,
    AttemptAuthorityService attempts,
    ILogger<ArchivedResultRefPruner> logger)
{
    public ArchivedResultRefPruneReport RunOnce(CancellationToken cancellationToken = default)
    {
        var archived = tasks.ScanArchivedJobs().Where(task => !task.Fixture).ToList();
        var attemptRefs = attempts.GetIndexedDeliveryRefs(
            archived.Select(task => task.TaskKey), includeArchived: true);
        var deleted = new List<string>();
        var candidates = 0;
        foreach (var project in archived.GroupBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = git.ResolveProjectRepoRoot(project.Key);
            if (string.IsNullOrWhiteSpace(root)) continue;

            var projectKeys = project.Select(task => task.TaskKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projectCandidates = CandidateRefs(
                project,
                attemptRefs.Where(reference => projectKeys.Contains(reference.TaskKey)));
            candidates += projectCandidates.Count;
            if (projectCandidates.Count == 0) continue;
            var existing = git.ListRefs(root, "refs/remotes/origin/agent-studio/results")
                .Concat(git.ListRefs(root, "refs/remotes/origin/agent-studio/quarantine"))
                .Select(reference => reference.FullName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var fullRef in projectCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existing.Contains(fullRef)) continue;
                var result = git.DeleteRef(root, fullRef);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Archived result ref prune kept {Ref} in {Repository}: {Error}",
                        fullRef, root, result.Error);
                    continue;
                }
                deleted.Add(fullRef);
            }
        }

        logger.LogInformation(
            "git archived-result-ref-prune archivedCards={ArchivedCards} candidates={Candidates} deleted={Deleted} scope=local-only",
            archived.Count, candidates, deleted.Count);
        return new(archived.Count, candidates, deleted.Count, deleted);
    }

    internal static IReadOnlyList<string> CandidateRefs(IEnumerable<TaskInfo> archivedTasks)
        => CandidateRefs(archivedTasks, []);

    internal static IReadOnlyList<string> CandidateRefs(
        IEnumerable<TaskInfo> archivedTasks,
        IEnumerable<AttemptIndexedDeliveryRef> attemptRefs)
    {
        var archived = archivedTasks
            .Where(task => string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal))
            .ToList();
        var archivedKeys = archived.Select(task => task.TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return archived
            .Select(ReviewSubjectFor)
            .Where(subject => subject is not null)
            .SelectMany(subject => new[] { subject!.ImmutableResultRef, subject.ResultRef })
            .Concat(attemptRefs
                .Where(reference => archivedKeys.Contains(reference.TaskKey))
                .Select(reference => reference.Ref))
            .Select(ToLocalRemoteTrackingRef)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static ReviewSubjectRecord? ReviewSubjectFor(TaskInfo task)
        => string.IsNullOrWhiteSpace(task.FolderPath) ? null : ReviewSubjectStore.Read(task.FolderPath);

    internal static string? ToLocalRemoteTrackingRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var branch = value.Trim();
        foreach (var prefix in new[] { "refs/remotes/origin/", "refs/heads/", "origin/" })
        {
            if (branch.StartsWith(prefix, StringComparison.Ordinal))
            {
                branch = branch[prefix.Length..];
                break;
            }
        }

        if (!branch.StartsWith("agent-studio/results/", StringComparison.Ordinal)
            && !branch.StartsWith("agent-studio/quarantine/", StringComparison.Ordinal))
            return null;
        if (branch.Contains("..", StringComparison.Ordinal) || branch.Contains('\\')) return null;
        return "refs/remotes/origin/" + branch;
    }
}
