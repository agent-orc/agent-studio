namespace AgentStudio.Git;

/// <summary>Repository paths reserved for card reports, not product changes.</summary>
public static class DeliveryReportPathPolicy
{
    public static readonly string[] Paths = ["status.md", "results/status.md"];

    public static bool IsReportPath(string path)
        => Paths.Contains(path.Replace('\\', '/'), StringComparer.Ordinal);

    public static bool AreOnlyReportConflicts(IReadOnlyList<string> paths)
        => paths.Count > 0 && paths.All(IsReportPath);
}
