using System.Text;
using AgentStudio.Cli;
using AgentStudio.Prompts;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>Supplies local review screenshots to the multimodal one-shot adapter.</summary>
internal static class LocalVisualScreenshotEvidence
{
    private static string[] Paths(string jobFolderPath)
    {
        var results = Path.Combine(jobFolderPath, "results");
        if (!Directory.Exists(results)) return [];
        return Directory.EnumerateFiles(results, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => VisualReviewEvidence.IsAfterScreenshot(
                Path.GetRelativePath(results, path)) ? 0 : 1)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Where(IsReadableImage)
            .Take(8)
            .ToArray();
    }

    private static bool IsReadableImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 1 or > 8L * 1024 * 1024) return false;
            var header = ImageHeaderReader.ReadFile(path);
            if (header is null || (long)header.Width * header.Height > 80_000_000) return false;
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => header.Format == "png",
                ".jpg" or ".jpeg" => header.Format == "jpeg",
                ".webp" => header.Format == "webp",
                _ => false,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    internal static string PromptSection(string jobFolderPath)
    {
        var paths = Paths(jobFolderPath);
        var text = new StringBuilder("\n\n## Attached screenshot images\n");
        if (paths.Length == 0) text.AppendLf("No delivered screenshots were attached to this run.");
        for (var i = 0; i < paths.Length; i++)
            text.AppendLf($"- Image {i + 1}: {Path.GetRelativePath(Path.Combine(jobFolderPath, "results"), paths[i])}");
        text.AppendLf("The numbered images above are attached to this model call in the same order. Inspect their pixels and cite each relevant filename and region.");
        return text.ToString();
    }

    internal static IReadOnlyList<CliOneShotImage> Images(string jobFolderPath) => Paths(jobFolderPath)
        .Select(path => new CliOneShotImage(Convert.ToBase64String(File.ReadAllBytes(path)),
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
            : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
            : "image/jpeg"))
        .ToArray();

    internal static bool HasAfterScreenshot(string jobFolderPath) =>
        Paths(jobFolderPath).Any(path =>
            VisualReviewEvidence.IsAfterScreenshot(Path.GetRelativePath(
                Path.Combine(jobFolderPath, "results"), path)));
}
