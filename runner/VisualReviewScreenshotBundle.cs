using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Places the source run's screenshot artifacts beside the disposable review checkout.</summary>
internal static class VisualReviewScreenshotBundle
{
    private const int MaximumImages = 100;
    private const long MaximumImageBytes = 8L * 1024 * 1024;
    private const long MaximumTotalBytes = 100L * 1024 * 1024;

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
        var listed = new List<string>();
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
                listed.Add($"- {JsonSerializer.Serialize(artifact.Name)}: omitted because of image evidence size limit");
                continue;
            }
            string? temporaryPath = null;
            try
            {
                var content = await readContent(artifact, ct);
                if (content is null)
                {
                    listed.Add($"- {JsonSerializer.Serialize(artifact.Name)}: unavailable");
                    continue;
                }
                var bytes = Convert.FromBase64String(content.ContentBase64);
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (bytes.LongLength != artifact.SizeBytes
                    || !string.Equals(sha, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    listed.Add($"- {JsonSerializer.Serialize(artifact.Name)}: integrity check failed");
                    continue;
                }
                var extension = Path.GetExtension(artifact.Name).ToLowerInvariant();
                var stage = VisualReviewEvidence.IsAfterScreenshot(artifact.Name) ? "after" : "before";
                var path = Path.Combine(directory, $"{stage}-image-{listed.Count + 1:D2}{extension}");
                temporaryPath = Path.Combine(directory, $"capture-{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
                File.Move(temporaryPath, path);
                temporaryPath = null;
                total += bytes.LongLength;
                copied++;
                listed.Add($"- {JsonSerializer.Serialize(artifact.Name)}: {JsonSerializer.Serialize(path)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                listed.Add($"- {JsonSerializer.Serialize(artifact.Name)}: unavailable ({ex.GetType().Name})");
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
        if (omittedByCount > 0)
            listed.Add($"- {omittedByCount} more screenshots omitted by the 100-image review limit.");
        return string.Join('\n', listed);
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
