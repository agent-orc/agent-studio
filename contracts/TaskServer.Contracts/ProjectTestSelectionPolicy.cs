namespace AgentStudio.TaskServer.Contracts;

/// <summary>One repository-owned impact row. Build and lint use zero-based command indices.</summary>
public sealed record ProjectTestSelectionArea(IReadOnlyList<string> Paths,
    IReadOnlyList<string> Suites, IReadOnlyList<int> Build, IReadOnlyList<int> Lint)
{
    public IReadOnlyList<string> Prepare { get; init; } = [];
}

public sealed record ProjectTestSelectionResult(bool Full, IReadOnlyList<string> Suites,
    IReadOnlyList<string> OmittedSuites, IReadOnlyList<int> Build, IReadOnlyList<int> Lint,
    IReadOnlyList<string> Reasons)
{
    public IReadOnlyList<string> Prepare { get; init; } = [];
}

/// <summary>The shared, fail-closed selection rule for card, batch and merge-window gates.</summary>
public static class ProjectTestSelectionPolicy
{
    public const string Selector = "project-test-selection";

    public static IReadOnlyList<string> Validate(ProjectExecutionDefinition definition)
    {
        var errors = new List<string>();
        foreach (var row in definition.TestSelection)
        {
            if (row.Paths.Count == 0 || row.Paths.Any(path => string.IsNullOrWhiteSpace(path)
                    || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Contains("..")))
                errors.Add("Each selection row needs safe repository-relative paths.");
            if (row.Suites.Count == 0 || row.Suites.Any(id => id != "*"
                    && !definition.TestSuites.Any(suite => suite.Id == id
                        && suite.Category == ProjectTestSuiteCategories.Routine
                        && definition.Commands.Test.Contains(suite.Command))))
                errors.Add("Selection suites must name declared routine test commands, or * for the full inventory.");
            if (row.Build.Any(index => index < 0 || index >= definition.Commands.Build.Count)
                || row.Lint.Any(index => index < 0 || index >= definition.Commands.Lint.Count))
                errors.Add("Selection build and lint indices must address declared commands.");
        }
        return errors;
    }

    public static bool IsGlobalInput(string path)
    {
        var normalized = Normalize(path);
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return normalized.StartsWith(".agent-studio/", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".proj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(string path, string prefix)
        => prefix.EndsWith('/')
            ? Normalize(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            : Normalize(path).Equals(prefix, StringComparison.OrdinalIgnoreCase);

    public static ProjectTestSelectionResult Select(ProjectExecutionDefinition definition,
        IReadOnlyList<string>? paths, bool full = false)
    {
        var routine = definition.TestSuites.Where(suite => suite.Category == ProjectTestSuiteCategories.Routine).ToArray();
        ProjectTestSelectionResult All(string reason) => new(true, routine.Select(suite => suite.Id).ToArray(), [],
            Enumerable.Range(0, definition.Commands.Build.Count).ToArray(),
            Enumerable.Range(0, definition.Commands.Lint.Count).ToArray(), [reason]);
        if (full) return All("Full suite requested by the gate or testing strategy.");
        if (paths is not { Count: > 0 }) return All("Changed paths are unavailable or empty.");
        if (definition.TestSelection.Count == 0 || Validate(definition).Count > 0)
            return All("The project has no valid test selection map.");
        // An inventory addition without an impact assignment must never disappear.
        var local = definition.TestSelection.Where(row => !row.Suites.Contains("*")).ToArray();
        if (definition.Commands.Test.Any(command => !routine.Any(suite => suite.Command == command
                && local.Any(row => row.Suites.Contains(suite.Id))))
            || Enumerable.Range(0, definition.Commands.Build.Count).Any(index => !local.Any(row => row.Build.Contains(index)))
            || Enumerable.Range(0, definition.Commands.Lint.Count).Any(index => !local.Any(row => row.Lint.Contains(index))))
            return All("The command inventory has an unassigned build, test or lint command.");
        var rows = new List<ProjectTestSelectionArea>();
        var reasons = new List<string>();
        foreach (var path in paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (IsGlobalInput(path)) return All($"Global build or project input changed: {path}.");
            // Overlapping rows union their dependencies; a shared row can never be hidden by a narrower one.
            var matches = definition.TestSelection.Where(row => row.Paths.Any(prefix => Matches(path, prefix))).ToArray();
            if (matches.Length == 0) return All($"Unmapped changed path: {path}.");
            if (matches.Any(row => row.Suites.Contains("*"))) return All($"Shared input changed: {path}.");
            rows.AddRange(matches);
            reasons.Add($"{path} selects {string.Join(", ", matches.SelectMany(row => row.Suites).Distinct())}.");
        }
        var ids = rows.SelectMany(row => row.Suites).ToHashSet(StringComparer.Ordinal);
        return new(false, routine.Where(suite => ids.Contains(suite.Id)).Select(suite => suite.Id).ToArray(),
            routine.Where(suite => !ids.Contains(suite.Id)).Select(suite => suite.Id).ToArray(),
            rows.SelectMany(row => row.Build).Distinct().Order().ToArray(),
            rows.SelectMany(row => row.Lint).Distinct().Order().ToArray(),
            [.. reasons, "Other suites are omitted because no changed path selects them."])
        {
            Prepare = rows.SelectMany(row => row.Prepare).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
