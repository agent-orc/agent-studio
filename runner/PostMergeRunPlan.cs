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

    /// <summary>
    /// Repository areas and the routine suites they select. A test project
    /// selects its area's suite; a shared .NET library selects every .NET
    /// routine suite that compiles against it.
    /// </summary>
    private static readonly (string Prefix, string[] Suites)[] Areas =
    [
        ("backend/", ["backend-routine"]),
        ("backend.Tests/", ["backend-routine"]),
        ("runner/", ["runner-routine"]),
        ("runner.Tests/", ["runner-routine"]),
        ("task-server/", ["task-server-routine"]),
        ("task-server.Tests/", ["task-server-routine"]),
        ("frontend/", ["frontend-unit"]),
        ("contracts/", DotnetRoutine),
        ("orchestration-decisions/", DotnetRoutine),
        ("cli-hosting/", DotnetRoutine),
        ("retention/", DotnetRoutine),
        ("testsupport/", DotnetRoutine),
    ];

    private static string[] DotnetRoutine => ["backend-routine", "runner-routine", "task-server-routine"];

    /// <summary>
    /// The routine suites the changed paths select. A path outside every
    /// area selects no routine suite when the project defines a
    /// <see cref="ProjectTestSuiteCategories.Base"/> suite, which runs in every
    /// window (AGT-3966). Without a base suite such a path still selects every
    /// routine suite, so a window never runs no test at all.
    /// </summary>
    public static IReadOnlyList<string> SelectAffected(ProjectExecutionDefinition definition,
        IReadOnlyList<string> paths)
    {
        var routine = Suites(definition, ProjectTestSuiteCategories.Routine);
        var hasBase = Suites(definition, ProjectTestSuiteCategories.Base).Count > 0;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            var area = Areas.FirstOrDefault(item =>
                normalized.StartsWith(item.Prefix, StringComparison.OrdinalIgnoreCase));
            var configured = area.Suites?.Where(id => routine.Any(suite => suite.Id == id)).ToArray() ?? [];
            if (configured.Length == 0)
            {
                if (hasBase) continue;
                return routine.Select(suite => suite.Command).ToArray();
            }
            selected.UnionWith(configured);
        }
        return routine.Where(suite => selected.Contains(suite.Id)).Select(suite => suite.Command).ToArray();
    }

    /// <param name="windowSuites">
    /// A <see cref="PostMergeWindowSuiteModes"/> value from the project's testing
    /// strategy (AGT-3965). <see cref="PostMergeWindowSuiteModes.Routine"/> runs
    /// the base suite and every routine suite in a window; anything else runs
    /// the base suite and the affected selection (AGT-3966). The nightly run
    /// executes every routine suite.
    /// </param>
    public static IReadOnlyList<string> Build(ProjectExecutionDefinition definition,
        string kind, IReadOnlyList<string> changedPaths, string? windowSuites = null)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.Commands.Prepare)) commands.Add(definition.Commands.Prepare);
        commands.AddRange(definition.Commands.Build.Where(command => !string.IsNullOrWhiteSpace(command)));
        var routine = Suites(definition, ProjectTestSuiteCategories.Routine).Select(suite => suite.Command).ToArray();
        var @base = Suites(definition, ProjectTestSuiteCategories.Base).Select(suite => suite.Command).ToArray();
        IEnumerable<string> tests = kind == "nightly" ? routine
            : windowSuites == PostMergeWindowSuiteModes.Routine ? [.. @base, .. routine]
            : [.. @base, .. SelectAffected(definition, changedPaths)];
        var selected = tests.Distinct(StringComparer.Ordinal).ToArray();
        if (kind == "nightly" ? routine.Length == 0 : routine.Length == 0 && @base.Length == 0)
            commands.Add("echo 'No routine test suites are configured' >&2; exit 2");
        else commands.AddRange(selected);
        return commands;
    }

    private static IReadOnlyList<ProjectTestSuite> Suites(ProjectExecutionDefinition definition, string category)
        => definition.TestSuites.Where(suite => suite.Category == category).ToArray();
}
