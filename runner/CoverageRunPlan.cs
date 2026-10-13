using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// One step of the scheduled coverage run (AGT-3074). Every step runs through
/// <c>nice -n 19 sh -c</c> in the coverage checkout, one at a time.
/// </summary>
public sealed record CoverageRunStep(string Id, string Kind, string Command, TimeSpan Timeout)
{
    public IReadOnlyList<string> Arguments => [.. CoverageRunPlanner.LowPriorityPrefix, "sh", "-c", Command];

    public string Display => string.Join(' ', CoverageRunPlanner.LowPriorityPrefix)
                             + " sh -c " + QuoteForShell(Command);

    private static string QuoteForShell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record CoverageRunPlan(
    IReadOnlyList<CoverageRunStep> Steps,
    IReadOnlyDictionary<string, string> Environment)
{
    public IReadOnlyList<CoverageRunStep> Suites => [.. Steps.Where(step => step.Kind == CoverageRunPlanner.SuiteKind)];
}

/// <summary>
/// Turns the repository execution definition into the ordered coverage run:
/// the declared prepare and build commands, then every <c>release</c>-category
/// test suite in declaration order. Review gates never see these suites; they
/// carry the coverage collector that made gates 1.7 to 2.5 times slower
/// (test-acceleration Dossier, lever B11).
/// </summary>
public static class CoverageRunPlanner
{
    public const string ReleaseCategory = "release";
    public const string PrepareKind = "prepare";
    public const string BuildKind = "build";
    public const string SuiteKind = "suite";

    public static readonly IReadOnlyList<string> LowPriorityPrefix = ["nice", "-n", "19"];

    /// <summary>Time-box for each prepare and build command.</summary>
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(45);

    /// <summary>Floor for a suite time-box; a suite otherwise gets twice its expected duration.</summary>
    public static readonly TimeSpan MinimumSuiteTimeout = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<ProjectTestSuite> SelectReleaseSuites(ProjectExecutionDefinition definition)
        => [.. definition.TestSuites.Where(suite => string.Equals(suite.Category, ReleaseCategory, StringComparison.Ordinal))];

    public static CoverageRunPlan Build(ProjectExecutionDefinition definition)
    {
        var steps = new List<CoverageRunStep>();
        if (!string.IsNullOrWhiteSpace(definition.Commands.Prepare))
            steps.Add(new(PrepareKind, PrepareKind, definition.Commands.Prepare, SetupTimeout));
        var builds = definition.Commands.Build.Where(command => !string.IsNullOrWhiteSpace(command)).ToList();
        for (var index = 0; index < builds.Count; index++)
            steps.Add(new($"build-{(index + 1).ToString(CultureInfo.InvariantCulture)}", BuildKind, builds[index], SetupTimeout));
        foreach (var suite in SelectReleaseSuites(definition))
            steps.Add(new(suite.Id, SuiteKind, suite.Command, SuiteTimeout(suite)));
        return new(steps, definition.Environment);
    }

    public static TimeSpan SuiteTimeout(ProjectTestSuite suite)
    {
        var doubled = TimeSpan.FromSeconds(2.0 * Math.Max(0, suite.ExpectedDurationSeconds));
        return doubled > MinimumSuiteTimeout ? doubled : MinimumSuiteTimeout;
    }
}

public enum CoverageRunVerdict
{
    Run,
    SkipBusy,
    SkipUnchanged,
}

public sealed record CoverageRunAdmissionDecision(CoverageRunVerdict Verdict, string Reason);

/// <summary>Host load as the coverage run sees it: normalized load and active work slots.</summary>
public sealed record CoverageHostLoad(double? LoadPerCore, int ActiveSlots);

/// <summary>
/// Pure admission rules for the scheduled coverage run. The run yields to any
/// claimed coding, review or gate slot and to a loaded host, and it measures
/// each integration-branch commit once unless forced.
/// </summary>
public static class CoverageRunAdmission
{
    public static CoverageRunAdmissionDecision ForHost(
        CoverageHostLoad load,
        double maxLoadPerCore,
        int maxActiveSlots)
    {
        if (load.ActiveSlots > maxActiveSlots)
            return new(CoverageRunVerdict.SkipBusy,
                $"{load.ActiveSlots} active slot(s), limit {maxActiveSlots}");
        if (load.LoadPerCore is { } perCore && perCore > maxLoadPerCore)
            return new(CoverageRunVerdict.SkipBusy,
                string.Create(CultureInfo.InvariantCulture, $"load {perCore:0.00} per core, limit {maxLoadPerCore:0.00}"));
        return new(CoverageRunVerdict.Run, load.LoadPerCore is null ? "load unknown, no active slots" : "host idle");
    }

    public static CoverageRunAdmissionDecision ForCommit(string commit, string? lastMeasuredCommit, bool force)
    {
        if (!force && string.Equals(commit, lastMeasuredCommit, StringComparison.OrdinalIgnoreCase))
            return new(CoverageRunVerdict.SkipUnchanged, $"commit {Short(commit)} already measured");
        return new(CoverageRunVerdict.Run, lastMeasuredCommit is null
            ? "no earlier measurement"
            : $"tip moved from {Short(lastMeasuredCommit)}");
    }

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;
}
