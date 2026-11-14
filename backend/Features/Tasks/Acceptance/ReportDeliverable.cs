namespace AgentStudio.Tasks;

/// <summary>
/// The primary deliverable of a research card: one HTML document at
/// <c>results/report.html</c> (AGT-2417 convention). The lightweight report
/// pipeline, Review results, and completion share this one check, so no path
/// reviews or completes a research card without its report (AGT-3565).
/// </summary>
public static class ReportDeliverable
{
    public const string ResearchReportPath = "results/report.html";
    public const string ResearchRequirement = "a valid HTML document at " + ResearchReportPath;

    public static string ResearchReportFile(string folder)
        => Path.Combine(TaskPaths.ResultsDir(folder), "report.html");

    /// <summary>True when <c>results/report.html</c> is a complete HTML document.</summary>
    public static bool HasResearchReport(string folder)
    {
        var path = ResearchReportFile(folder);
        if (!File.Exists(path)) return false;
        try
        {
            var content = File.ReadAllText(path);
            return content.Contains("<html", StringComparison.OrdinalIgnoreCase)
                && content.Contains("</html>", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReportDeliverable: an unreadable report counts as missing");
            return false;
        }
    }

    /// <summary>
    /// The missing required report of the card, or null when it has one or
    /// its mode requires none. Only research has a strict primary report.
    /// </summary>
    public static string? MissingRequiredReport(TaskInfo task)
        => TaskModes.Normalize(task.Mode) == TaskModes.Research && !HasResearchReport(task.FolderPath)
            ? ResearchRequirement
            : null;
}
