using AgentStudio.Pipeline;

namespace AgentStudio.Docs;

/// <summary>Primary Dossier membership, migration, and the card progress view.</summary>
public sealed class DossierCardsService(TaskScannerService scanner)
{
    public DossierCardList List(string projectName, WorkbenchListItem dossier)
    {
        return Project(projectName, dossier, scanner.ScanAllJobsWithArchive());
    }

    public static DossierCardList Project(string projectName, WorkbenchListItem dossier,
        IReadOnlyList<TaskInfo> tasks)
    {
        var assigned = tasks
            .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.Dossier?.Path, dossier.EntryPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.CreatedAt)
            .ThenBy(task => task.Key ?? task.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cards = assigned
            .Select(task => new DossierCard(
                task.Key ?? task.Id, task.Id, task.Title, task.State, task.Phase ?? task.State,
                task.Dossier?.Environments ?? [],
                task.Dossier?.Section, task.Dossier?.Decision, task.Dossier?.ImplementationIndex))
            .ToArray();
        var implemented = Enumerable.Range(0, dossier.ImplementationTasks.Count + dossier.ImplementationTaskKeys.Length)
            .Count(index =>
            {
                var slice = assigned.Where(task => task.Dossier?.ImplementationIndex == index).ToArray();
                var implementation = slice.Where(task => task.Kind != TaskKinds.Decision).ToArray();
                var counted = implementation.Length > 0 ? implementation : slice;
                return counted.Length > 0 && counted.All(task =>
                    task.State is TaskStates.Completed or TaskStates.Archive);
            });
        return new DossierCardList(projectName, dossier.Key, dossier.EntryPath,
            dossier.ImplementationTasks.Count + dossier.ImplementationTaskKeys.Length, implemented, cards);
    }

    /// <summary>Called on writable catalogue discovery; existing explicit membership always wins.</summary>
    public static void Backfill(string projectName, IReadOnlyList<WorkbenchListItem> dossiers,
        TaskScannerService scanner, TaskMutationService mutations)
    {
        var tasks = scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var plans = DossierBackfillPolicy.Plan(tasks, dossiers,
            task => SpawnedTaskLedger.Read(task.FolderPath));
        foreach (var (task, dossier) in plans)
            mutations.SetJobDossier(task.Id, dossier, task.WatchPath);
    }
}

public sealed record DossierCard(string Key, string Id, string Title, string Lane, string State,
    string[] Environments, string? Section, string? Decision, int? ImplementationIndex);

public sealed record DossierCardList(string ProjectName, string? DossierKey, string DossierPath,
    int ImplementationSlices, int CompletedSlices, IReadOnlyList<DossierCard> Cards);

/// <summary>Only a unique, project-local claim can establish primary membership.</summary>
public static class DossierBackfillPolicy
{
    public static IReadOnlyList<(TaskInfo Task, TaskDossier Dossier)> Plan(
        IReadOnlyList<TaskInfo> tasks, IReadOnlyList<WorkbenchListItem> dossiers,
        Func<TaskInfo, IReadOnlyList<SpawnedTaskRecord>> spawns)
    {
        var claims = new Dictionary<string, List<TaskDossier>>(StringComparer.OrdinalIgnoreCase);
        var byKey = tasks.Where(task => !string.IsNullOrWhiteSpace(task.Key))
            .GroupBy(task => task.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        void Claim(TaskInfo task, TaskDossier dossier)
        {
            if (task.Dossier != null) return;
            if (!claims.TryGetValue(task.TaskKey, out var list)) claims[task.TaskKey] = list = [];
            list.Add(dossier);
        }

        foreach (var item in dossiers.Where(item => item.Valid
            && AgentStudio.Tasks.ConceptDossierContract.IsDossierPath(item.EntryPath)))
        {
            var primary = new TaskDossier { Path = item.EntryPath };
            foreach (var key in item.SourceTaskKeys.Concat(item.RelatedTaskKeys)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                if (byKey.TryGetValue(key, out var task)) Claim(task, primary);

            foreach (var (key, index) in item.ImplementationTaskKeys.Select((key, index) => (key, index)))
                if (byKey.TryGetValue(key, out var task))
                    Claim(task, primary with { ImplementationIndex = index });

            foreach (var task in tasks.Where(task => item.Key != null
                         && task.References.Workbenches.Contains(item.Key, StringComparer.OrdinalIgnoreCase)))
                Claim(task, primary);

            foreach (var sourceKey in item.SourceTaskKeys)
            {
                if (!byKey.TryGetValue(sourceKey, out var source)) continue;
                foreach (var spawn in spawns(source))
                {
                    var prefix = $"concept-promotion:{item.EntryPath}:";
                    if (spawn.Reason?.StartsWith(prefix, StringComparison.Ordinal) != true
                        || !int.TryParse(spawn.Reason[prefix.Length..], out var index)
                        || index < 0 || index >= item.ImplementationTasks.Count) continue;
                    var matches = tasks.Where(task =>
                        (spawn.TargetJobId != null && task.Id == spawn.TargetJobId)
                        || (spawn.TargetKey != null && task.Key == spawn.TargetKey)).Take(2).ToArray();
                    if (matches.Length != 1) continue;
                    var implementation = item.ImplementationTasks[index];
                    Claim(matches[0], TaskDossierProvenance.FromImplementation(item.EntryPath, implementation, index));
                }
            }

            foreach (var (implementation, index) in item.ImplementationTasks.Select((task, index) => (task, index)))
            {
                var matches = tasks.Where(task => string.Equals(task.Title.Trim(), implementation.Title.Trim(),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) continue;
                Claim(matches[0], TaskDossierProvenance.FromImplementation(item.EntryPath, implementation, index));
            }
        }

        return tasks.Where(task => task.Dossier == null
                && claims.TryGetValue(task.TaskKey, out var list)
                && list.Select(candidate => candidate.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                && list.Where(candidate => candidate.ImplementationIndex.HasValue)
                    .Select(candidate => candidate.ImplementationIndex).Distinct().Count() <= 1)
            .Select(task =>
            {
                var selected = claims[task.TaskKey]
                    .OrderByDescending(candidate => candidate.ImplementationIndex.HasValue)
                    .First();
                return (task, selected);
            }).ToArray();
    }
}
