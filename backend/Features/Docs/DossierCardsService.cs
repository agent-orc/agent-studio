using AgentStudio.Pipeline;

namespace AgentStudio.Docs;

/// <summary>Primary Dossier membership, migration, and the card progress view.</summary>
public sealed class DossierCardsService(TaskScannerService scanner)
{
    public DossierCardList List(string projectName, WorkbenchListItem dossier)
    {
        return Project(projectName, dossier, scanner.ScanAllJobsWithArchiveForBackgroundIndex(),
            task => SpawnedTaskLedger.Read(task.FolderPath));
    }

    public static DossierCardList Project(string projectName, WorkbenchListItem dossier,
        IReadOnlyList<TaskInfo> tasks, Func<TaskInfo, IReadOnlyList<SpawnedTaskRecord>>? spawns = null)
    {
        var members = new DossierMembership(tasks, spawns ?? (_ => [])).Members(projectName, dossier);
        var cards = members
            .Select(member => new DossierCard(
                member.Task.Key ?? member.Task.Id, member.Task.Id, member.Task.Title, member.Task.State,
                member.Task.Phase ?? member.Task.State,
                member.Task.Dossier?.Environments ?? [],
                member.Task.Dossier?.Section, member.Task.Dossier?.Decision, member.ImplementationIndex))
            .ToArray();
        var declaredSlices = dossier.ImplementationTasks.Count + dossier.ImplementationTaskKeys.Length;
        var spawnedSlices = (dossier.Decision?.SpawnedTaskKeys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => members.FirstOrDefault(member => string.Equals(member.Task.Key, key, StringComparison.OrdinalIgnoreCase)))
            .Where(member => member is not null && member.ImplementationIndex is null)
            .Select(member => member!.Task)
            .ToArray();
        var implemented = Enumerable.Range(0, declaredSlices)
            .Count(index =>
            {
                var slice = members.Where(member => member.ImplementationIndex == index)
                    .Select(member => member.Task).ToArray();
                var implementation = slice.Where(task => task.Kind != TaskKinds.Decision).ToArray();
                var counted = implementation.Length > 0 ? implementation : slice;
                return counted.Length > 0 && counted.All(task =>
                    task.State is TaskStates.Completed or TaskStates.Archive);
            }) + spawnedSlices.Count(task => task.State is TaskStates.Completed or TaskStates.Archive);
        return new DossierCardList(projectName, dossier.Key, dossier.EntryPath,
            declaredSlices + spawnedSlices.Length, implemented, cards);
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

/// <summary>
/// The cards a Dossier owns: every card whose primary <c>task.dossier</c>
/// points at it, plus promoted slices that only the source concept's promotion
/// ledger names (the relation write failed or has not been backfilled yet). A
/// primary association to another Dossier always wins over the ledger. The
/// card list and the documentation projection both read this set (AGT-3837).
/// </summary>
public sealed class DossierMembership
{
    private readonly ILookup<string, TaskInfo> _byDossier;
    private readonly ILookup<string, TaskInfo> _byKey;
    private readonly ILookup<string, TaskInfo> _byId;
    private readonly Func<TaskInfo, IReadOnlyList<SpawnedTaskRecord>> _spawns;

    public DossierMembership(IReadOnlyList<TaskInfo> tasks,
        Func<TaskInfo, IReadOnlyList<SpawnedTaskRecord>> spawns)
    {
        _byDossier = tasks.Where(task => task.Dossier is { Path.Length: > 0 })
            .ToLookup(task => DossierKey(task.ProjectName, task.Dossier!.Path), StringComparer.OrdinalIgnoreCase);
        _byKey = tasks.Where(task => !string.IsNullOrWhiteSpace(task.Key))
            .ToLookup(task => task.Key!, StringComparer.OrdinalIgnoreCase);
        _byId = tasks.ToLookup(task => task.Id, StringComparer.Ordinal);
        _spawns = spawns;
    }

    public IReadOnlyList<DossierMember> Members(string projectName, WorkbenchListItem dossier)
    {
        var members = _byDossier[DossierKey(projectName, dossier.EntryPath)]
            .Select(task => new DossierMember(task, task.Dossier!.ImplementationIndex))
            .ToList();
        var seen = new HashSet<TaskInfo>(members.Select(member => member.Task), ReferenceEqualityComparer.Instance);
        var prefix = $"concept-promotion:{dossier.EntryPath}:";
        foreach (var sourceKey in dossier.SourceTaskKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sources = _byKey[sourceKey.Trim()]
                .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (sources.Length != 1) continue;
            foreach (var spawn in _spawns(sources[0]))
            {
                if (spawn.Reason?.StartsWith(prefix, StringComparison.Ordinal) != true) continue;
                var matches = (spawn.TargetJobId != null ? _byId[spawn.TargetJobId] : [])
                    .Concat(spawn.TargetKey != null ? _byKey[spawn.TargetKey] : [])
                    .Distinct(ReferenceEqualityComparer.Instance).Cast<TaskInfo>()
                    .Take(2).ToArray();
                if (matches.Length != 1 || matches[0].Dossier is { Path.Length: > 0 } || !seen.Add(matches[0]))
                    continue;
                members.Add(new DossierMember(matches[0],
                    int.TryParse(spawn.Reason[prefix.Length..], out var index) && index >= 0 ? index : null));
            }
        }
        return members
            .OrderBy(member => member.Task.CreatedAt)
            .ThenBy(member => member.Task.Key ?? member.Task.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string DossierKey(string projectName, string path) => $"{projectName}\u001f{path}";
}

public sealed record DossierMember(TaskInfo Task, int? ImplementationIndex);

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

            foreach (var task in tasks.Where(task => TaskModes.IsConcept(task.Mode)
                         && !string.IsNullOrWhiteSpace(task.FolderPath)
                         && string.Equals(AgentStudio.Tasks.ConceptDossierContract.Read(task.FolderPath).RepoRelativePath,
                             item.EntryPath, StringComparison.OrdinalIgnoreCase)))
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
                    Claim(matches[0], TaskDossierProvenance.FromImplementation(
                        item.EntryPath, implementation, index, item.ImplementationTasks.Count));
                }
            }

            foreach (var (implementation, index) in item.ImplementationTasks.Select((task, index) => (task, index)))
            {
                var matches = tasks.Where(task => string.Equals(task.Title.Trim(), implementation.Title.Trim(),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) continue;
                Claim(matches[0], TaskDossierProvenance.FromImplementation(
                    item.EntryPath, implementation, index, item.ImplementationTasks.Count));
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
