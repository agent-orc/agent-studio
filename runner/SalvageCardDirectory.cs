using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Resolves the lifecycle facts the salvage retention policy needs per card.</summary>
internal interface ISalvageCardDirectory
{
    Task<IReadOnlyDictionary<string, SalvageCardFacts>> ResolveAsync(
        IReadOnlyCollection<SalvageCardReference> cards,
        CancellationToken ct);
}

/// <summary>A card named by a salvage entry; the project is null when the name carries none.</summary>
internal sealed record SalvageCardReference(string? ProjectId, string CardKey);

/// <summary>
/// Task Server lookup. The project named by the entry is tried first, then every
/// registered project with the card's key prefix. A failed lookup yields
/// <see cref="SalvageCardLifecycle.Unknown"/>; the policy keeps such cards.
/// </summary>
internal sealed class TaskServerSalvageCardDirectory(
    Func<CancellationToken, Task<IReadOnlyList<ProjectDto>>> listProjects,
    Func<string, string, CancellationToken, Task<TaskDto?>> getTask) : ISalvageCardDirectory
{
    public TaskServerSalvageCardDirectory(TaskServerClient client)
        : this(client.ListProjectsAsync, client.GetTaskAsync)
    {
    }

    public async Task<IReadOnlyDictionary<string, SalvageCardFacts>> ResolveAsync(
        IReadOnlyCollection<SalvageCardReference> cards,
        CancellationToken ct)
    {
        IReadOnlyList<ProjectDto>? projects;
        try
        {
            projects = await listProjects(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            projects = null;
        }

        var result = new Dictionary<string, SalvageCardFacts>(StringComparer.Ordinal);
        foreach (var group in cards.GroupBy(card => SalvageRetentionPolicy.NormalizeCardKey(card.CardKey)))
        {
            ct.ThrowIfCancellationRequested();
            var cardKey = group.Key;
            var candidates = CandidateProjects(group.Select(card => card.ProjectId), cardKey, projects);
            var failed = projects is null && candidates.Count == 0;
            TaskDto? task = null;
            foreach (var projectId in candidates)
            {
                try
                {
                    task = await getTask(projectId, cardKey, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed = true;
                    continue;
                }
                if (task is not null) break;
            }
            result[cardKey] = task is not null
                ? FromTask(cardKey, task)
                : new SalvageCardFacts(
                    cardKey,
                    failed ? SalvageCardLifecycle.Unknown : SalvageCardLifecycle.Missing,
                    null);
        }
        return result;
    }

    /// <summary>
    /// Completed and archived cards are terminal. Terminal-since is the later of
    /// the last update and the archive time, so a late edit only extends retention.
    /// </summary>
    internal static SalvageCardFacts FromTask(string cardKey, TaskDto task)
    {
        var terminal = task.State is "6-completed" or "7-archive"
                       || string.Equals(task.ArchiveState, "archived", StringComparison.OrdinalIgnoreCase);
        if (!terminal)
            return new SalvageCardFacts(cardKey, SalvageCardLifecycle.Open, null);
        var since = ToUtc(task.UpdatedAt);
        if (task.ArchivedAt is { } archived && ToUtc(archived) > since)
            since = ToUtc(archived);
        return new SalvageCardFacts(cardKey, SalvageCardLifecycle.Terminal, since);
    }

    internal static IReadOnlyList<string> CandidateProjects(
        IEnumerable<string?> named,
        string cardKey,
        IReadOnlyList<ProjectDto>? projects)
    {
        var candidates = new List<string>();
        foreach (var projectId in named.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()))
        {
            var registered = projects?.FirstOrDefault(project =>
                string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
            // Without the project list the named project is still worth a direct lookup.
            var candidate = registered?.ProjectId ?? (projects is null ? projectId : null);
            if (candidate is not null && !candidates.Contains(candidate, StringComparer.Ordinal))
                candidates.Add(candidate);
        }
        var dash = cardKey.LastIndexOf('-');
        if (dash > 0 && projects is not null)
        {
            var prefix = cardKey[..dash];
            foreach (var project in projects.Where(project =>
                         string.Equals(project.TaskKeyPrefix, prefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (!candidates.Contains(project.ProjectId, StringComparer.Ordinal))
                    candidates.Add(project.ProjectId);
            }
        }
        return candidates;
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
