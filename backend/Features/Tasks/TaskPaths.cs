namespace AgentStudio.Tasks;

/// <summary>
/// Centralises filesystem layout under a job folder. Every consumer of
/// <c>logs/</c> or <c>cli-output.log</c> should go through here so the
/// magic strings live in one place.
/// </summary>
internal static class TaskPaths
{
    public const string CliOutputLogFileName = "cli-output.log";
    public const string SessionEventsLogFileName = "session-events.jsonl";
    public const string TimelineLogFileName = "timeline.jsonl";
    public const string LogsDirName = "logs";
    public const string RunContextDirName = "run-context";
    public const string ResultsDirName = "results";
    public const string ReviewEvidenceFileName = "review-evidence.jsonl";
    public const string ReviewObservationsFileName = "review-observations.jsonl";
    public const string ResultsHistoryDirName = "history";

    public static string LogsDir(string jobFolder) => Path.Combine(jobFolder, LogsDirName);
    /// <summary>Per-run captured-context files (<c>logs/run-context/&lt;ts&gt;.md</c>); see <c>SessionEvent.ContextRef</c>.</summary>
    public static string RunContextDir(string jobFolder) => Path.Combine(jobFolder, LogsDirName, RunContextDirName);
    public static string CliOutputLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, CliOutputLogFileName);
    public static string SessionEventsLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, SessionEventsLogFileName);
    public static string TimelineLog(string jobFolder) => Path.Combine(jobFolder, LogsDirName, TimelineLogFileName);
    public static string ResultsDir(string jobFolder) => Path.Combine(jobFolder, ResultsDirName);
    public static string ReviewEvidenceLog(string jobFolder) => Path.Combine(jobFolder, ResultsDirName, ReviewEvidenceFileName);
    public static string ReviewObservationsLog(string jobFolder) => Path.Combine(jobFolder, ResultsDirName, ReviewObservationsFileName);

    /// <summary>
    /// The files under <c>results/</c> that the card produced. <c>results/history/</c>
    /// is excluded: it holds what Studio itself keeps there (earlier Result
    /// versions, rotated review evidence, and the legacy Result-history cache
    /// that a read-only card open writes, AGT-3086), so it is never evidence of
    /// work. Throws like <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>;
    /// callers keep their own fail-closed handling.
    /// </summary>
    public static IEnumerable<string> ResultArtifacts(string jobFolder)
    {
        var resultsDir = ResultsDir(jobFolder);
        if (!Directory.Exists(resultsDir)) return [];
        return Directory
            .EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories)
            .Where(file => !IsResultsHistoryPath(resultsDir, file));
    }

    /// <summary>True when <paramref name="file"/> lies under <c>results/history/</c>.</summary>
    public static bool IsResultsHistoryPath(string resultsDir, string file)
    {
        var relative = Path.GetRelativePath(resultsDir, file);
        var firstSeparator = relative.IndexOfAny(['/', '\\']);
        var first = firstSeparator < 0 ? relative : relative[..firstSeparator];
        return string.Equals(first, ResultsHistoryDirName, StringComparison.OrdinalIgnoreCase);
    }
}
