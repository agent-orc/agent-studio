using System.Collections.Concurrent;
using AgentStudio.Git;
using AgentStudio.Pipeline;

namespace AgentStudio.Projects;

public sealed record ConflictCount(string Name, int Count);
public sealed record ConflictPair(string CardA, string CardB, int Count);
public sealed record IntegratedCard(string CardKey, string Target, DateTimeOffset AtUtc);
public sealed record ConflictRate(DateTimeOffset Day, int Conflicts, int IntegratedCards, double? Per100IntegratedCards);
public sealed record IntegrationConflictSummary(int Conflicts, int IntegratedCards, double? Per100IntegratedCards,
    IReadOnlyList<ConflictCount> TopFiles, IReadOnlyList<ConflictPair> CardPairs, IReadOnlyList<ConflictRate> Daily);
public sealed record ProjectIntegrationConflicts(string Project, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<IntegrationConflictEntry> Records, IntegrationConflictSummary Summary);

public static class ProjectIntegrationConflictSummary
{
    public static ProjectIntegrationConflicts Build(string project, DateTimeOffset from, DateTimeOffset to,
        IEnumerable<IntegrationConflictEntry> records, IEnumerable<IntegratedCard> integrations, string? target = null)
    {
        var rows = records.Where(row => row.Project.Equals(project, StringComparison.OrdinalIgnoreCase)
            && row.DetectedAtUtc >= from && row.DetectedAtUtc < to
            && (target is null || row.IntegrationTarget == target)).DistinctBy(row => row.Id)
            .OrderByDescending(row => row.DetectedAtUtc).ToArray();
        // Count each card once per target at its first recorded successful integration.
        var cards = integrations.Where(row => target is null || row.Target == target)
            .GroupBy(row => (row.CardKey.ToUpperInvariant(), row.Target))
            .Select(group => group.MinBy(row => row.AtUtc)!)
            .Where(row => row.AtUtc >= from && row.AtUtc < to).ToArray();
        var files = rows.SelectMany(row => row.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal))
            .GroupBy(path => path, StringComparer.Ordinal)
            .Select(group => new ConflictCount(group.Key, group.Count()))
            .OrderByDescending(row => row.Count).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        var pairs = rows.SelectMany(row => row.Files.SelectMany(file => file.CardKeys)
                .Where(key => !key.Equals(row.CardKey, StringComparison.OrdinalIgnoreCase))
                .Select(key => new[] { row.CardKey.ToUpperInvariant(), key.ToUpperInvariant() }
                    .Order(StringComparer.Ordinal).ToArray())
                .Select(pair => (A: pair[0], B: pair[1])).Distinct())
            .GroupBy(pair => pair).Select(group => new ConflictPair(group.Key.A, group.Key.B, group.Count()))
            .OrderByDescending(row => row.Count).ThenBy(row => row.CardA, StringComparer.Ordinal)
            .ThenBy(row => row.CardB, StringComparer.Ordinal).ToArray();
        var daily = new List<ConflictRate>();
        for (var day = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero); day < to; day = day.AddDays(1))
        {
            var count = rows.Count(row => row.DetectedAtUtc.UtcDateTime.Date == day.Date);
            var integrated = cards.Count(row => row.AtUtc.UtcDateTime.Date == day.Date);
            daily.Add(new ConflictRate(day, count, integrated, Rate(count, integrated)));
        }
        return new ProjectIntegrationConflicts(project, from, to, rows,
            new IntegrationConflictSummary(rows.Length, cards.Length, Rate(rows.Length, cards.Length), files, pairs, daily));
    }

    private static double? Rate(int conflicts, int cards) => cards == 0 ? null : 100d * conflicts / cards;
}

public static class ProjectIntegrationConflictEndpoints
{
    public static void MapProjectIntegrationConflictEndpoints(this WebApplication app)
    {
        var receipts = new IntegrationReceiptCache();
        app.MapGet("/api/projects/{name}/integration-conflicts", (string name, DateTimeOffset? from,
            DateTimeOffset? to, string? target, TaskScannerService scanner, TimelineLog timeline, BatchGateStore batches) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-30);
            if (start >= end || end - start > TimeSpan.FromDays(366))
                return Results.BadRequest(new { error = "Use a time range greater than zero and at most 366 days." });
            var cards = scanner.ScanAutomationJobsWithArchive(entry.Path)
                .SelectMany(task => receipts.Read(task, timeline, batches)).ToArray();
            return Results.Ok(ProjectIntegrationConflictSummary.Build(entry.Name, start, end,
                IntegrationConflictLedger.Default.Read(entry.Name, start, end, target), cards, target));
        });
    }
    internal static IEnumerable<IntegratedCard> ReadIntegrations(TaskInfo task, IReadOnlyList<TimelineEvent> timeline,
        IReadOnlyList<IntegrationLaneEvent> lane, IReadOnlyDictionary<string, string> batchTargets)
    {
        var key = task.Key ?? task.TaskKey ?? task.Id;
        foreach (var row in timeline.Where(row => row.Kind == TimelineEventKinds.IntegrationSucceeded))
            if (row.Details?.GetValueOrDefault("integrationBranch") is { Length: > 0 } target)
                yield return new IntegratedCard(key, target, new DateTimeOffset(row.Ts.ToUniversalTime()));
        foreach (var row in lane.Where(row => row.Kind == "published"))
            if (row.Reason?.StartsWith("batch:", StringComparison.Ordinal) == true
                && batchTargets.TryGetValue(row.Reason[6..], out var target))
                yield return new IntegratedCard(key, target, row.AtUtc);
    }

}

/// <summary>Reuse receipt projections until either source file changes. Keep all-time
/// first integrations so range queries never count a later reacceptance twice.</summary>
internal sealed class IntegrationReceiptCache
{
    private readonly ConcurrentDictionary<string, (FileStamp Timeline, FileStamp Lane, IntegratedCard[] Cards)> _entries = new();
    internal readonly record struct FileStamp(long Length, DateTime Modified)
    {
        internal static FileStamp Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc) : default;
        }
    }

    internal IReadOnlyList<IntegratedCard> Read(TaskInfo task, TimelineLog timeline, BatchGateStore batches)
    {
        var timelineStamp = FileStamp.Read(TaskPaths.TimelineLog(task.FolderPath));
        var laneStamp = FileStamp.Read(Path.Combine(task.FolderPath, IntegrationLaneEvents.FileName));
        if (_entries.TryGetValue(task.FolderPath, out var cached)
            && cached.Timeline == timelineStamp && cached.Lane == laneStamp) return cached.Cards;
        var lane = IntegrationLaneEvents.Read(task.FolderPath);
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        var complete = true;
        foreach (var id in lane.Where(row => row.Kind == "published"
                     && row.Reason?.StartsWith("batch:", StringComparison.Ordinal) == true)
                     .Select(row => row.Reason![6..]).Distinct(StringComparer.Ordinal))
        {
            try { targets[id] = batches.ReadManifest(id).Scope.IntegrationBranch; }
            catch (Exception ex) when (ex is IOException or BatchGateRecordUnreadableException)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Integration receipt target is unavailable; retry on the next read.");
                complete = false;
            }
        }
        var cards = ProjectIntegrationConflictEndpoints.ReadIntegrations(task,
            timeline.ReadAll(task.FolderPath), lane, targets).ToArray();
        // A late manifest must be retried even when receipt files have not changed.
        if (complete) _entries[task.FolderPath] = (timelineStamp, laneStamp, cards);
        return cards;
    }
}
