using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace AgentRunner;

/// <summary>Places the source run's screenshot artifacts beside the disposable review checkout.</summary>
internal static class VisualReviewScreenshotBundle
{
    private const int MaximumImages = 100;
    private const int MaximumFailureDetails = 8;
    private const long MaximumImageBytes = 8L * 1024 * 1024;
    private const long MaximumTotalBytes = 100L * 1024 * 1024;
    private const long MaximumImagePixels = 80_000_000;

    internal static async Task<string> CaptureAsync(TaskServerClient client, string runId,
        string attemptRoot, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await CaptureAsync(await client.ListRunArtifactsAsync(runId, ct),
                (artifact, token) => client.GetArtifactContentAsync(runId, artifact.ArtifactId, token),
                attemptRoot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"visual screenshot evidence unavailable: {ex.Message}");
            return $"Delivered screenshots could not be loaded: {ex.Message}";
        }
    }

    internal static async Task<string> CaptureAsync(IReadOnlyList<ArtifactDto> runArtifacts,
        Func<ArtifactDto, CancellationToken, Task<ArtifactContentDto?>> readContent,
        string attemptRoot, CancellationToken ct)
    {
        var artifacts = runArtifacts
            .Where(VisualReviewEvidence.IsScreenshot)
            .OrderBy(artifact => VisualReviewEvidence.IsAfterScreenshot(artifact.Name) ? 0 : 1)
            .ThenBy(artifact => artifact.Name, StringComparer.Ordinal)
            .ToArray();
        if (artifacts.Length == 0) return "No delivered screenshots were attached to this run.";
        var directory = Path.Combine(attemptRoot, "visual-evidence");
        Directory.CreateDirectory(directory);
        var copiedImages = new List<string>();
        var failures = new List<string>();
        var failedCount = 0;
        void AddFailure(string detail)
        {
            failedCount++;
            if (failures.Count < MaximumFailureDetails) failures.Add(detail);
        }
        long total = 0;
        var copied = 0;
        var omittedByCount = 0;
        foreach (var artifact in artifacts)
        {
            if (copied == MaximumImages)
            {
                omittedByCount++;
                continue;
            }
            if (artifact.SizeBytes > MaximumImageBytes || total + artifact.SizeBytes > MaximumTotalBytes)
            {
                AddFailure($"- {JsonSerializer.Serialize(artifact.Name)}: omitted because of image evidence size limit");
                continue;
            }
            string? temporaryPath = null;
            try
            {
                var content = await readContent(artifact, ct);
                if (content is null)
                {
                    AddFailure($"- {JsonSerializer.Serialize(artifact.Name)}: unavailable");
                    continue;
                }
                var bytes = Convert.FromBase64String(content.ContentBase64);
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (bytes.LongLength != artifact.SizeBytes
                    || !string.Equals(sha, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    AddFailure($"- {JsonSerializer.Serialize(artifact.Name)}: integrity check failed");
                    continue;
                }
                if (!IsReadableImage(bytes, artifact.Name))
                {
                    AddFailure($"- {JsonSerializer.Serialize(artifact.Name)}: invalid image");
                    continue;
                }
                var extension = Path.GetExtension(artifact.Name).ToLowerInvariant();
                var stage = VisualReviewEvidence.IsAfterScreenshot(artifact.Name) ? "after" : "before";
                var path = Path.Combine(directory, $"{stage}-image-{copied + 1:D2}{extension}");
                temporaryPath = Path.Combine(directory, $"capture-{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
                File.Move(temporaryPath, path);
                temporaryPath = null;
                total += bytes.LongLength;
                copied++;
                copiedImages.Add($"- {JsonSerializer.Serialize(artifact.Name)}: {JsonSerializer.Serialize(path)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddFailure($"- {JsonSerializer.Serialize(artifact.Name)}: unavailable ({ex.GetType().Name})");
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        if (failedCount > failures.Count)
            failures.Add($"- {failedCount - failures.Count} more failed screenshots omitted from this list.");
        if (omittedByCount > 0)
            failures.Add($"- {omittedByCount} more screenshots omitted by the 100-image review limit.");
        return string.Join('\n', copiedImages.Concat(failures));
    }

    private static bool IsReadableImage(byte[] bytes, string name)
    {
        try
        {
            var identified = Image.Identify(bytes);
            if (identified is null || identified.Width < 1 || identified.Height < 1
                || (long)identified.Width * identified.Height > MaximumImagePixels)
                return false;
            var format = Image.DetectFormat(bytes);
            using var image = Image.Load(new DecoderOptions { MaxFrames = 1 }, bytes);
            var extension = Path.GetExtension(name).ToLowerInvariant();
            return extension switch
            {
                ".png" => format.Name.Equals("PNG", StringComparison.OrdinalIgnoreCase),
                ".jpg" or ".jpeg" => format.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase),
                ".webp" => format.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    internal static bool HasReadableAfterScreenshot(string attemptRoot)
    {
        var directory = Path.Combine(attemptRoot, "visual-evidence");
        return Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "after-image-*", SearchOption.TopDirectoryOnly).Any();
    }

    internal static string PromptSection(string screenshotEvidence, string repositoryPath)
    {
        var text = new StringBuilder();
        text.AppendLf("\n## Delivered screenshot files");
        text.AppendLf(screenshotEvidence);
        text.AppendLf("Open the absolute paths above with the image-viewing tool. The filename before each path is the citation to use in the verdict. If none is available, return a concern naming missing screenshot evidence.");
        var hardRules = Path.Combine(repositoryPath, "docs/quality/design/style-guide-hard-rules.md");
        if (File.Exists(hardRules))
        {
            text.AppendLf("\n## Applicable product visual rules (docs/quality/design/style-guide-hard-rules.md)");
            text.AppendLf(File.ReadAllText(hardRules));
        }
        var adminRules = Path.Combine(repositoryPath, "docs/operations/admin-design-guideline/index.html");
        if (File.Exists(adminRules))
        {
            text.AppendLf("\n## Applicable admin design rules (docs/operations/admin-design-guideline/index.html)");
            foreach (Match match in Regex.Matches(File.ReadAllText(adminRules), @"(?s)<li>.*?</li>"))
            {
                var plain = Regex.Replace(match.Value, "<[^>]+>", " ");
                if (plain.Contains("MUST", StringComparison.Ordinal)
                    && (plain.Contains("flat", StringComparison.OrdinalIgnoreCase)
                        || plain.Contains("theme", StringComparison.OrdinalIgnoreCase)
                        || plain.Contains("contrast", StringComparison.OrdinalIgnoreCase)
                        || plain.Contains("spacing", StringComparison.OrdinalIgnoreCase)
                        || plain.Contains("width", StringComparison.OrdinalIgnoreCase)))
                    text.AppendLf(Regex.Replace(plain, @"\s+", " ").Trim());
            }
        }
        return text.ToString();
    }
}
