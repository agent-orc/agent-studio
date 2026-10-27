using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// <see cref="Version"/> names the Task Server models the answer was built
/// from (project and generation); the board validator folds it, because the
/// workspace generations it otherwise folds do not move on a Task Server-only change.
/// </summary>
public sealed record TaskServerBoardAnswer(
    IReadOnlyList<TaskInfo> Tasks, bool Synced, IReadOnlyList<string> Degraded, string Version);

/// <summary>
/// Pure composition for <c>Board:Source = task-server</c>: lanes, membership,
/// title and order come from the Task Server read model. The workspace index
/// only enriches a card it also holds (runtime, review and Git signals the
/// Task Server does not own yet); a card the workspace lacks is shown from
/// its card core alone.
/// </summary>
public static class TaskServerBoardComposition
{
    public static IReadOnlyList<TaskInfo> Compose(
        IEnumerable<(string Project, BoardReadModelResponse Model)> models,
        IEnumerable<TaskInfo> workspaceLive,
        Func<string, string?> watchPathFor)
    {
        var workspace = new Dictionary<(string Project, string Key), TaskInfo>();
        foreach (var task in workspaceLive)
            workspace.TryAdd((task.ProjectName.ToUpperInvariant(), BoardCardCore.From(task).TaskKey), task);

        var tasks = new List<TaskInfo>();
        foreach (var (project, model) in models)
        foreach (var card in model.Lanes.SelectMany(lane => lane.Cards))
        {
            var order = (int)Math.Clamp(card.Rank, int.MinValue, int.MaxValue);
            if (workspace.TryGetValue((project.ToUpperInvariant(), card.TaskKey), out var known))
            {
                tasks.Add(known with { State = card.State, Title = card.Title, Order = order });
                continue;
            }
            var watchPath = watchPathFor(project) ?? "";
            tasks.Add(new TaskInfo
            {
                Id = card.TaskKey,
                Key = card.TaskKey,
                TaskKey = $"{watchPath}::{card.TaskKey}",
                Title = card.Title,
                State = card.State,
                Order = order,
                ProjectName = project,
                WatchPath = watchPath,
                CreatedAt = card.UpdatedAt,
                LastActivity = card.UpdatedAt,
                EnteredLaneAt = card.UpdatedAt,
            });
        }
        return tasks;
    }
}

/// <summary>
/// The task-server board read: waits briefly for the mirror to carry the
/// pinned workspace generation (writes still land in the workspace first),
/// then reads every project's model with a conditional request. An
/// unreachable Task Server degrades to the last model read, never to the
/// workspace, so the answer keeps one authority; rollback is the setting.
/// </summary>
public sealed class TaskServerBoardSource(
    BoardShadowWorker worker,
    TaskServerBoardReader reader,
    TaskScannerService scanner,
    ProjectRegistry projects,
    ILogger<TaskServerBoardSource> logger)
{
    internal static readonly TimeSpan SyncWait = TimeSpan.FromSeconds(2);

    public async Task<TaskServerBoardAnswer> ReadAsync(TaskIndexPublication? publication, CancellationToken ct)
    {
        var synced = await worker.EnsureSyncedAsync(publication?.Generation ?? 0, SyncWait, ct);
        var workspaceLive = scanner.ScanAllJobs();
        // The projects the mirror carries: every project with a workspace card,
        // archive included, the same set the shadow comparison covers.
        var names = (publication is null ? workspaceLive : publication.Live.Concat(publication.Archive))
            .Select(task => task.ProjectName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var degraded = new List<string>();
        var reads = await Task.WhenAll(names.Select(async name =>
        {
            try
            {
                return (Project: name, Model: await reader.ReadAsync(name, ct), Failed: false);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Task Server board read failed for {Project}; serving the last model", name);
                return (Project: name, Model: reader.Cached(name), Failed: true);
            }
        }));
        degraded.AddRange(reads.Where(read => read.Failed).Select(read => $"{read.Project}:unavailable"));
        degraded.AddRange(reads.Where(read => !read.Failed && read.Model is null).Select(read => $"{read.Project}:missing"));

        var tasks = TaskServerBoardComposition.Compose(
            reads.Where(read => read.Model is not null).Select(read => (read.Project, read.Model!)),
            workspaceLive,
            name => projects.FindByIdOrDisplayName(name)?.StorageLocation);
        var version = string.Join(';', reads
            .Where(read => read.Model is not null)
            .Select(read => $"{read.Project}:{read.Model!.ProjectId}:{read.Model.Generation}"));
        return new TaskServerBoardAnswer(tasks, synced, degraded, version);
    }
}
