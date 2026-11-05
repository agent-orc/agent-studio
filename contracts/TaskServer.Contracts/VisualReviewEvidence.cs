namespace AgentStudio.TaskServer.Contracts;

/// <summary>Selection shared by sealed review planning and the review executor.</summary>
public static class VisualReviewEvidence
{
    public static bool IsFrontendVisualChange(IEnumerable<string> paths) => paths.Any(path =>
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("frontend/", StringComparison.OrdinalIgnoreCase)
            && (normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".scss", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
    });

    public static bool IsScreenshot(ArtifactDto artifact) =>
        artifact.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && (artifact.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || artifact.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || artifact.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || artifact.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

    /// <summary>Unmarked screenshots represent the delivered view; explicit before images do not.</summary>
    public static bool IsAfterScreenshot(string name)
    {
        var normalized = name.Replace('\\', '/');
        var parts = normalized.Split('/');
        return !parts.Select((part, index) => index == parts.Length - 1
                ? Path.GetFileNameWithoutExtension(part) : part)
            .Any(part => System.Text.RegularExpressions.Regex.IsMatch(part,
                @"(^before($|[-_.])|[-_.]before($|[-_.]))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    public static ReviewVerdictDto EnforceMissingScreenshots(ReviewVerdictDto verdict,
        IEnumerable<string> changedPaths, bool hasReadableScreenshot)
    {
        var changed = changedPaths.Where(path => IsFrontendVisualChange([path])).ToArray();
        if (hasReadableScreenshot || changed.Length == 0
            || string.Equals(verdict.Status, "block", StringComparison.OrdinalIgnoreCase))
            return verdict;
        return verdict with
        {
            Status = "concerns",
            Classification = "missing-visual-evidence",
            Summary = "Frontend template or style changed without readable screenshot evidence.",
            EvidenceChecked = string.Join(", ", changed),
            Missing = "Delivered after screenshots under results/ for the changed frontend surface.",
        };
    }
}
