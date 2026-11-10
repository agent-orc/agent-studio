using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Shared;

namespace AgentRunner;

/// <summary>
/// Places the source run's screenshot artifacts beside the disposable review checkout.
/// The daemon prepares the bundle and a detached worker runs the plan, so the
/// captured list is persisted as <c>visual-evidence/index.json</c> and the
/// worker reloads it instead of relying on daemon memory (AGT-3718).
/// </summary>
internal static class VisualReviewScreenshotBundle
{
    internal const string DirectoryName = "visual-evidence";
    internal const string IndexFileName = "index.json";
    internal const string NoScreenshots = "No delivered screenshots were attached to this run.";
    private const int MaximumImages = 12;
    private const int MaximumFailureDetails = 8;
    private const long MaximumImageBytes = 2L * 1024 * 1024;
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
            var index = new VisualScreenshotIndex([], [$"Delivered screenshots could not be loaded: {ex.Message}"]);
            try { await WriteIndexAsync(attemptRoot, index, ct); }
            catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
            {
                log($"visual screenshot index not written: {writeEx.Message}");
            }
            return Render(index, Path.Combine(attemptRoot, DirectoryName));
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
        var directory = Path.Combine(attemptRoot, DirectoryName);
        if (artifacts.Length == 0)
        {
            var empty = new VisualScreenshotIndex([], []);
            await WriteIndexAsync(attemptRoot, empty, ct);
            return Render(empty, directory);
        }
        Directory.CreateDirectory(directory);
        var copiedImages = new List<VisualScreenshot>();
        var failures = new List<string>();
        var omittedBySize = new List<string>();
        var failedCount = 0;
        void AddFailure(string detail)
        {
            failedCount++;
            if (failures.Count < MaximumFailureDetails) failures.Add(detail);
        }
        long total = 0;
        var copied = 0;
        var omittedByCount = new List<string>();
        foreach (var artifact in artifacts)
        {
            if (copied == MaximumImages)
            {
                omittedByCount.Add(artifact.Name);
                continue;
            }
            if (artifact.SizeBytes > MaximumImageBytes || total + artifact.SizeBytes > MaximumTotalBytes)
            {
                omittedBySize.Add(artifact.Name);
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
                var mediaType = ReadableImageMediaType(bytes, artifact.Name);
                if (mediaType is null)
                {
                    AddFailure($"- {JsonSerializer.Serialize(artifact.Name)}: invalid image");
                    continue;
                }
                var extension = Path.GetExtension(artifact.Name).ToLowerInvariant();
                var stage = VisualReviewEvidence.IsAfterScreenshot(artifact.Name) ? "after" : "before";
                var fileName = $"{stage}-image-{copied + 1:D2}{extension}";
                var path = Path.Combine(directory, fileName);
                temporaryPath = Path.Combine(directory, $"capture-{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
                File.Move(temporaryPath, path);
                temporaryPath = null;
                total += bytes.LongLength;
                copied++;
                copiedImages.Add(new VisualScreenshot(artifact.Name, fileName, mediaType, Theme(artifact.Name)));
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
        if (omittedBySize.Count > 0)
            failures.Add($"- Screenshots omitted by the {MaximumImageBytes / (1024 * 1024)} MiB per-image or " +
                $"{MaximumTotalBytes / (1024 * 1024)} MiB total size limit ({omittedBySize.Count}): " +
                $"{string.Join(", ", omittedBySize.Take(80).Select(name => JsonSerializer.Serialize(name)))}" +
                (omittedBySize.Count > 80 ? $"; {omittedBySize.Count - 80} further names omitted" : string.Empty) + ".");
        if (omittedByCount.Count > 0)
            failures.Add($"- Screenshots omitted by the {MaximumImages}-image review limit " +
                $"({omittedByCount.Count}): {string.Join(", ", omittedByCount.Take(80).Select(name => JsonSerializer.Serialize(name)))}" +
                (omittedByCount.Count > 80 ? $"; {omittedByCount.Count - 80} further names omitted" : string.Empty) + ".");
        var captured = new VisualScreenshotIndex(copiedImages, failures);
        await WriteIndexAsync(attemptRoot, captured, ct);
        return Render(captured, directory);
    }

    /// <summary>
    /// Rebuilds the evidence the preparing process captured. A missing index
    /// means no capture ran for this attempt.
    /// </summary>
    internal static string LoadEvidence(string attemptRoot)
    {
        var directory = Path.Combine(attemptRoot, DirectoryName);
        var indexPath = Path.Combine(directory, IndexFileName);
        if (!File.Exists(indexPath)) return NoScreenshots;
        VisualScreenshotIndex? index;
        try { index = JsonSerializer.Deserialize<VisualScreenshotIndex>(File.ReadAllText(indexPath), IndexJson); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Delivered screenshot index could not be read: {ex.Message}";
        }
        if (index is null) return "Delivered screenshot index could not be read: the index is empty.";
        return Render(index, directory);
    }

    private static string Render(VisualScreenshotIndex index, string directory)
    {
        var lines = new List<string>();
        var unavailable = new List<string>();
        foreach (var screenshot in index.Screenshots ?? [])
        {
            // The index names a file inside its own directory; never follow a
            // path that leaves it.
            var path = Path.Combine(directory, Path.GetFileName(screenshot.Path));
            if (!File.Exists(path))
            {
                unavailable.Add($"- {JsonSerializer.Serialize(screenshot.Name)}: unavailable after preparation");
                continue;
            }
            var theme = string.IsNullOrEmpty(screenshot.Theme) ? string.Empty : $" ({screenshot.Theme} theme)";
            lines.Add($"- {JsonSerializer.Serialize(screenshot.Name)}: {JsonSerializer.Serialize(path)}{theme}");
        }
        var text = string.Join('\n', lines.Concat(unavailable).Concat(index.Notes ?? []));
        return text.Length == 0 ? NoScreenshots : text;
    }

    private static async Task WriteIndexAsync(string attemptRoot, VisualScreenshotIndex index, CancellationToken ct)
    {
        var directory = Path.Combine(attemptRoot, DirectoryName);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"index-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(index, IndexJson), ct);
            File.Move(temporaryPath, Path.Combine(directory, IndexFileName), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The media type proven by the image header, or null when the bytes are not the named image.</summary>
    private static string? ReadableImageMediaType(byte[] bytes, string name)
    {
        var header = ImageHeaderReader.Read(bytes);
        if (header is null || (long)header.Width * header.Height > MaximumImagePixels) return null;
        return (Path.GetExtension(name).ToLowerInvariant(), header.Format) switch
        {
            (".png", "png") => "image/png",
            (".jpg" or ".jpeg", "jpeg") => "image/jpeg",
            (".webp", "webp") => "image/webp",
            _ => null,
        };
    }

    /// <summary>Reads <c>light</c> or <c>dark</c> from screenshot names such as <c>after-1920-light.png</c>.</summary>
    internal static string? Theme(string name)
    {
        var match = Regex.Match(Path.ChangeExtension(name.Replace('\\', '/'), null) ?? name,
            @"(?:^|[-_./])(light|dark)(?=$|[-_./])", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static bool HasReadableAfterScreenshot(string attemptRoot)
    {
        var directory = Path.Combine(attemptRoot, DirectoryName);
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

/// <summary>Persisted form of the screenshots captured for one review attempt.</summary>
internal sealed record VisualScreenshotIndex(
    IReadOnlyList<VisualScreenshot> Screenshots,
    IReadOnlyList<string> Notes);

/// <summary>
/// One copied screenshot: the delivered artifact name used as citation, the
/// file name inside <c>visual-evidence/</c>, the media type proven by the image
/// header, and the theme read from the name when it names one.
/// </summary>
internal sealed record VisualScreenshot(string Name, string Path, string MediaType, string? Theme);
