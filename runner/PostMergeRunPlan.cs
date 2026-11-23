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
    /// True for a repository-wide build or test input: it changes what every
    /// area builds or which commands the suites run, so it selects every
    /// routine suite. Mirrors the per-card global inputs
    /// (<c>GeneratedTestProjectMap.GlobalInput</c>) plus the solution and the
    /// central package and NuGet settings.
    /// </summary>
    public static bool IsGlobalInput(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || (!normalized.Contains('/') && (normalized.Equals("global.json", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            || normalized.StartsWith(".agent-studio/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The routine suites the changed paths select (AGT-3966). The area map
    /// describes a project only when it configures at least one routine suite
    /// the map names; for any other project every changed path selects every
    /// routine suite, as before. For a described project:
    /// <list type="bullet">
    /// <item>a path in an area selects that area's configured suites;</item>
    /// <item>a path in an area whose suites the project does not configure, or
    /// a global build input (<see cref="IsGlobalInput"/>), selects every routine
    /// suite;</item>
    /// <item>a path outside every area (docs, scripts, the changelog) selects
    /// no routine suite when the project defines a
    /// <see cref="ProjectTestSuiteCategories.Base"/> suite, which runs in every
    /// window. Without a base suite it selects every routine suite, so a window
    /// never runs no test at all.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> SelectAffected(ProjectExecutionDefinition definition,
        IReadOnlyList<string> paths)
    {
        var routine = Suites(definition, ProjectTestSuiteCategories.Routine);
        var every = routine.Select(suite => suite.Command).ToArray();
        var hasBase = Suites(definition, ProjectTestSuiteCategories.Base).Count > 0;
        var described = routine.Any(suite => Areas.Any(area => area.Suites.Contains(suite.Id, StringComparer.Ordinal)));
        if (!described) return paths.Count == 0 ? [] : every;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            if (IsGlobalInput(normalized)) return every;
            var area = Areas.FirstOrDefault(item =>
                normalized.StartsWith(item.Prefix, StringComparison.OrdinalIgnoreCase));
            if (area.Suites is null)
            {
                if (hasBase) continue;
                return every;
            }
            var configured = area.Suites.Where(id => routine.Any(suite => suite.Id == id)).ToArray();
            if (configured.Length == 0) return every;
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
