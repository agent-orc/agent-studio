using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record PostMergeWindowDecision(bool Close, string Reason);

/// <summary>Pure T3 admission and command selection for one fetched develop tip.</summary>
public static class PostMergeRunPlan
{
    public const int DefaultMaxMerges = 6;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    public static PostMergeWindowDecision Window(int merges, DateTimeOffset? oldest,
        DateTimeOffset now, int maxMerges, TimeSpan maxAge)
    {
        if (merges >= maxMerges) return new(true, $"{merges} merges reached the {maxMerges}-merge limit");
        if (oldest is not null && now - oldest >= maxAge)
            return new(true, $"oldest merge reached the {maxAge.TotalMinutes:0}-minute limit");
        return new(false, oldest is null ? "no pending merges" : "window remains open");
    }

    public static IReadOnlyList<string> SelectAffected(ProjectExecutionDefinition definition,
        IReadOnlyList<string> paths)
    {
        var routine = definition.TestSuites.Where(suite => suite.Category == "routine").ToArray();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            var id = normalized.StartsWith("backend/", StringComparison.OrdinalIgnoreCase) ? "backend-routine"
                : normalized.StartsWith("runner/", StringComparison.OrdinalIgnoreCase) ? "runner-routine"
                : normalized.StartsWith("task-server/", StringComparison.OrdinalIgnoreCase) ? "task-server-routine"
                : normalized.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase) ? "frontend-unit" : null;
            if (id is null || !routine.Any(suite => suite.Id == id)) return routine.Select(suite => suite.Command).ToArray();
            selected.Add(id);
        }
        return routine.Where(suite => selected.Contains(suite.Id)).Select(suite => suite.Command).ToArray();
    }

    public static IReadOnlyList<string> Build(ProjectExecutionDefinition definition,
        string kind, IReadOnlyList<string> changedPaths)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.Commands.Prepare)) commands.Add(definition.Commands.Prepare);
        commands.AddRange(definition.Commands.Build.Where(command => !string.IsNullOrWhiteSpace(command)));
        if (!definition.TestSuites.Any(suite => suite.Category == "routine"))
            commands.Add("echo 'No routine test suites are configured' >&2; exit 2");
        else if (kind == "nightly")
            commands.AddRange(definition.TestSuites.Where(suite => suite.Category == "routine")
                .Select(suite => suite.Command));
        else commands.AddRange(SelectAffected(definition, changedPaths));
        return commands;
    }
}
