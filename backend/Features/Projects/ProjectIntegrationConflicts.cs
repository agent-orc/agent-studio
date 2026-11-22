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
        app.MapGet("/api/projects/{name}/integration-conflicts", (string name, DateTimeOffset? from,
            DateTimeOffset? to, string? target, TaskScannerService scanner, TimelineLog timeline, BatchGateStore batches) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-30);
            if (start >= end || end - start > TimeSpan.FromDays(366))
                return Results.BadRequest(new { error = "Use a time range greater than zero and at most 366 days." });
            var batchTargets = batches.ListManifests().Where(manifest =>
                    manifest.Scope.Project.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(manifest => manifest.BatchId, manifest => manifest.Scope.IntegrationBranch);
            var cards = scanner.ScanAllAutomationJobsWithArchive()
                .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, entry.Path))
                .SelectMany(task => ReadIntegrations(task, timeline.ReadAll(task.FolderPath),
                    IntegrationLaneEvents.Read(task.FolderPath), batchTargets)).ToArray();
            return Results.Ok(ProjectIntegrationConflictSummary.Build(entry.Name, start, end,
                IntegrationConflictLedger.Default.Read(entry.Name), cards, target));
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
