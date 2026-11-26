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

    public static bool IsGlobalInput(string path) => ProjectTestSelectionPolicy.IsGlobalInput(path);

    public static IReadOnlyList<string> SelectAffected(ProjectExecutionDefinition definition,
        IReadOnlyList<string> paths)
    {
        var selection = ProjectTestSelectionPolicy.Select(definition, paths);
        return Suites(definition, ProjectTestSuiteCategories.Routine)
            .Where(suite => selection.Suites.Contains(suite.Id)).Select(suite => suite.Command).ToArray();
    }

    public static IReadOnlyList<string> Build(ProjectExecutionDefinition definition,
        string kind, IReadOnlyList<string> changedPaths, string? windowSuites = null)
    {
        var full = kind == "nightly" || windowSuites == PostMergeWindowSuiteModes.Routine;
        var selection = ProjectTestSelectionPolicy.Select(definition, changedPaths, full);
        var commands = new List<string>();
        // A configured affected plan derives preparation from selected ecosystems
        // at execution time. The repository prepare script installs every ecosystem.
        if ((selection.Full || definition.TestSelection.Count == 0) && !string.IsNullOrWhiteSpace(definition.Commands.Prepare))
            commands.Add(definition.Commands.Prepare);
        if (!selection.Full) commands.AddRange(selection.Prepare);
        commands.AddRange(selection.Build.Select(index => definition.Commands.Build[index]));
        commands.AddRange(selection.Lint.Select(index => definition.Commands.Lint[index]));
        var routine = Suites(definition, ProjectTestSuiteCategories.Routine);
        if (selection.Full && kind != "nightly")
            commands.AddRange(Suites(definition, ProjectTestSuiteCategories.Base).Select(suite => suite.Command));
        if (routine.Count == 0)
            commands.Add("echo 'No routine test suites are configured' >&2; exit 2");
        else commands.AddRange(routine.Where(suite => selection.Suites.Contains(suite.Id)).Select(suite => suite.Command));
        if (selection.Full) commands.AddRange(definition.Commands.Test);
        return commands.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ProjectTestSuite> Suites(ProjectExecutionDefinition definition, string category)
        => definition.TestSuites.Where(suite => suite.Category == category).ToArray();
}
