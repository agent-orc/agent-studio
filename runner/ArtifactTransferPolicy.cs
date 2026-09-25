namespace AgentRunner;

public static class ArtifactTransferOutcomes
{
    public const string ArtifactTooLarge = "ArtifactTooLarge";
    public const string TransferFailed = "ArtifactTransferFailed";
}

public sealed record ArtifactTransferCandidate(
    string FullPath,
    string RelativePath,
    long SizeBytes,
    string? Sha256 = null);

public sealed record ArtifactTransferPlan(
    ArtifactTransferLimitsResponse Limits,
    IReadOnlyList<ArtifactTransferCandidate> Files,
    IReadOnlyList<ArtifactTransferIssue> Skipped,
    DurableArtifactManifest Manifest);

/// <summary>
/// Pure bounded selection for result evidence. The Task Server advertises all
/// byte ceilings; this policy only applies them and excludes dependency/build
/// trees that are never useful review evidence.
/// </summary>
public static class ArtifactTransferPolicy
{
    private static readonly HashSet<string> ExcludedDirectories = new(
        ["node_modules", "bin", "obj"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedVideoExtensions = new(
        [".webm", ".mp4", ".mov", ".avi"],
        StringComparer.OrdinalIgnoreCase);

    public static (IReadOnlyList<ArtifactTransferCandidate> Files, IReadOnlyList<ArtifactTransferIssue> Skipped)
        Select(
            string resultsDirectory,
            IEnumerable<(string FullPath, string RelativePath, long SizeBytes)> files,
            ArtifactTransferLimitsResponse limits)
    {
        var selected = new List<ArtifactTransferCandidate>();
        var skipped = new List<ArtifactTransferIssue>();
        long total = 0;
        foreach (var file in files.OrderBy(Priority).ThenBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var relative = file.RelativePath.Replace('\\', '/').TrimStart('/');
            var wirePath = relative.StartsWith("results/", StringComparison.OrdinalIgnoreCase)
                ? relative
                : "results/" + relative;
            if (HasExcludedDirectory(relative))
            {
                skipped.Add(new ArtifactTransferIssue(
                    wirePath,
                    file.SizeBytes,
                    "is in an excluded dependency or build-output directory"));
                continue;
            }
            if (IsPlaywrightTrace(relative))
            {
                skipped.Add(new ArtifactTransferIssue(
                    wirePath,
                    file.SizeBytes,
                    "is a Playwright trace excluded by the result-artifact policy"));
                continue;
            }
            if (IsVideo(relative))
            {
                skipped.Add(new ArtifactTransferIssue(
                    wirePath,
                    file.SizeBytes,
                    "is a video excluded by the result-artifact policy"));
                continue;
            }
            if (file.SizeBytes > limits.MaxFileBytes)
            {
                skipped.Add(new ArtifactTransferIssue(
                    wirePath,
                    file.SizeBytes,
                    $"exceeded the {FormatMb(limits.MaxFileBytes)} MB per-file result budget"));
                continue;
            }
            if (total + file.SizeBytes > limits.MaxTotalBytes)
            {
                skipped.Add(new ArtifactTransferIssue(
                    wirePath,
                    file.SizeBytes,
                    $"exceeded the {FormatMb(limits.MaxTotalBytes)} MB total result budget"));
                continue;
            }
            selected.Add(new ArtifactTransferCandidate(file.FullPath, wirePath, file.SizeBytes));
            total += file.SizeBytes;
        }
        return (selected, skipped);
    }

    public static bool IsCapacityRejection(TaskServerException exception)
        => exception.StatusCode is 413 or 507;

    public static string FormatMb(long bytes)
        => Math.Round(bytes / 1024d / 1024d, 1, MidpointRounding.AwayFromZero)
            .ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static bool HasExcludedDirectory(string relativePath)
        => relativePath.Split('/', '\\').Any(ExcludedDirectories.Contains);

    private static bool IsPlaywrightTrace(string relativePath)
        => string.Equals(
            Path.GetFileName(relativePath),
            "trace.zip",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string relativePath)
        => ExcludedVideoExtensions.Contains(Path.GetExtension(relativePath));

    private static int Priority((string FullPath, string RelativePath, long SizeBytes) file)
        => file.RelativePath.Replace('\\', '/') switch
        {
            "deliverables.md" or "results/deliverables.md" => 0,
            "status.md" or "results/status.md" => 1,
            _ => 2,
        };
}
