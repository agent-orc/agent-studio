using System.Security.Cryptography;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class VisualReviewScreenshotTests
{
    [Theory]
    [InlineData("clean-footer.png", "pass")]
    [InlineData("run-on-footer.png", "concerns")]
    public async Task Screenshot_fixtures_are_delivered_to_visual_aspect_with_citable_names(
        string fixture, string modelStatus)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", fixture));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifact = new ArtifactDto("image-1", "run-1", $"bars/{fixture}",
            "image/png", sha, bytes.Length, "key", 1, DateTime.UnixEpoch);
        var content = new ArtifactContentDto(artifact.ArtifactId, artifact.RunId,
            artifact.Name, artifact.MediaType, sha, Convert.ToBase64String(bytes), bytes.Length);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([artifact],
                (_, _) => Task.FromResult<ArtifactContentDto?>(content), root, default);
            var image = Assert.Single(Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*"));
            Assert.Equal(bytes, File.ReadAllBytes(image));
            var prompt = VisualReviewScreenshotBundle.PromptSection(evidence, root);
            Assert.Contains(artifact.Name, prompt);
            Assert.Contains(image, prompt);
            Assert.Contains("image-viewing tool", prompt);

            // The model is stubbed: this verifies the screenshot and its cited
            // verdict survive the review boundary, not machine vision quality.
            var modelVerdict = new ReviewVerdictDto("visual-review", modelStatus,
                "RemoteAspectVerdict", modelStatus == "pass" ? "Footer is separated."
                    : "Footer metrics run together and end with a separator.",
                $"{artifact.Name}: footer", modelStatus == "pass" ? "none"
                    : "Separate footer metrics and remove the dangling separator.");
            var actual = VisualReviewEvidence.EnforceMissingScreenshots(modelVerdict,
                ["frontend/src/app/layout/footer.component.html"], hasReadableScreenshot: true);
            Assert.Equal(modelStatus, actual.Status);
            Assert.Contains($"{fixture}: footer", actual.EvidenceChecked);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_screenshot_is_a_named_concern_for_frontend_styles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        string evidence;
        try
        {
            evidence = await VisualReviewScreenshotBundle.CaptureAsync([], (_, _) =>
                Task.FromResult<ArtifactContentDto?>(null), root, default);
            Assert.Equal(evidence, VisualReviewScreenshotBundle.LoadEvidence(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Assert.Contains("No delivered screenshots", evidence);
        var reportedPass = new ReviewVerdictDto("visual-review", "pass", "RemoteAspectVerdict",
            "Looks fine.", "frontend/src/app/layout/footer.component.scss", "none");
        var enforced = VisualReviewEvidence.EnforceMissingScreenshots(reportedPass,
            ["frontend/src/app/layout/footer.component.scss"], hasReadableScreenshot: false);
        Assert.Equal("concerns", enforced.Status);
        Assert.Contains("screenshot", enforced.Missing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("footer.component.scss", enforced.EvidenceChecked);
        Assert.Equal("pass", VisualReviewEvidence.EnforceMissingScreenshots(reportedPass,
            ["backend/Program.cs"], hasReadableScreenshot: false).Status);
    }

    [Fact]
    public async Task Matching_before_and_after_artifacts_both_reach_the_review_prompt()
    {
        var beforeBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "run-on-footer.png"));
        var afterBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "clean-footer.png"));
        var before = Artifact("before", "bars/before-1920-light.png", beforeBytes);
        var after = Artifact("after", "bars/after-1920-light.png", afterBytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([before, after],
                (artifact, _) => Task.FromResult<ArtifactContentDto?>(new ArtifactContentDto(
                    artifact.ArtifactId, artifact.RunId, artifact.Name, artifact.MediaType,
                    artifact.Sha256,
                    Convert.ToBase64String(artifact.ArtifactId == "before" ? beforeBytes : afterBytes),
                    artifact.SizeBytes)), root, default);
            Assert.Contains(before.Name, evidence);
            Assert.Contains(after.Name, evidence);
            Assert.True(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.True(evidence.IndexOf(after.Name, StringComparison.Ordinal)
                < evidence.IndexOf(before.Name, StringComparison.Ordinal));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Before_only_artifact_does_not_satisfy_after_evidence_requirement()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "run-on-footer.png"));
        var before = Artifact("before", "bars/before-1920-light.png", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([before],
                (_, _) => Task.FromResult<ArtifactContentDto?>(new ArtifactContentDto(
                    before.ArtifactId, before.RunId, before.Name, before.MediaType,
                    before.Sha256, Convert.ToBase64String(bytes), before.SizeBytes)), root, default);
            Assert.Contains(before.Name, evidence);
            Assert.False(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));

            var reportedPass = new ReviewVerdictDto("visual-review", "pass", "RemoteAspectVerdict",
                "Looks fine.", before.Name, "none");
            var enforced = VisualReviewEvidence.EnforceMissingScreenshots(reportedPass,
                ["frontend/src/app/layout/footer.component.html"],
                VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.Equal("concerns", enforced.Status);
            Assert.Contains("after screenshots", enforced.Missing);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task After_artifact_is_prioritized_ahead_of_one_hundred_before_artifacts()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "clean-footer.png"));
        var before = Enumerable.Range(0, 100)
            .Select(index => Artifact($"before-{index}", $"bars/before-{index:D3}.png", bytes));
        var after = Artifact("after", "bars/after-1920-light.png", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync(
                [.. before, after],
                (artifact, _) => Task.FromResult<ArtifactContentDto?>(new ArtifactContentDto(
                    artifact.ArtifactId, artifact.RunId, artifact.Name, artifact.MediaType,
                    artifact.Sha256, Convert.ToBase64String(bytes), artifact.SizeBytes)),
                root, default);

            Assert.True(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.StartsWith($"- \"{after.Name}\"", evidence);
            Assert.Contains("omitted by the 12-image review limit", evidence);
            Assert.Contains("bars/before-079.png", evidence);
            Assert.Contains("further names omitted", evidence);
            Assert.Equal(12, Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Image_limit_counts_readable_images_so_later_valid_after_evidence_is_loaded()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "clean-footer.png"));
        var bad = Enumerable.Range(0, 100)
            .Select(index => Artifact($"bad-{index}", $"bars/after-{index:D3}.png", bytes));
        var valid = Artifact("valid", "bars/after-999.png", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync(
                [.. bad, valid],
                (artifact, _) => Task.FromResult<ArtifactContentDto?>(artifact.ArtifactId == "valid"
                    ? new ArtifactContentDto(artifact.ArtifactId, artifact.RunId, artifact.Name,
                        artifact.MediaType, artifact.Sha256, Convert.ToBase64String(bytes), artifact.SizeBytes)
                    : null), root, default);

            Assert.True(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.Contains($"\"{valid.Name}\":", evidence);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Oversized_screenshot_is_named_without_reading_its_content()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "clean-footer.png"));
        var oversized = Artifact("large", "bars/after-large.png", bytes) with
        {
            SizeBytes = 3L * 1024 * 1024,
        };
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([oversized],
                (_, _) => throw new InvalidOperationException("Oversized image must not be read."), root, default);

            Assert.Contains("bars/after-large.png", evidence);
            Assert.Contains("2 MiB per-image", evidence);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_artifacts_do_not_hide_a_later_valid_after_screenshot()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "clean-footer.png"));
        var failedRead = Artifact("failed-read", "bars/after-001.png", bytes);
        var invalidContent = Artifact("invalid-content", "bars/after-002.png", bytes);
        var valid = Artifact("valid", "bars/after-003.png", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync(
                [failedRead, invalidContent, valid],
                (artifact, _) => artifact.ArtifactId switch
                {
                    "failed-read" => throw new IOException("Artifact read failed."),
                    "invalid-content" => Task.FromResult<ArtifactContentDto?>(new ArtifactContentDto(
                        artifact.ArtifactId, artifact.RunId, artifact.Name, artifact.MediaType,
                        artifact.Sha256, "invalid base64", artifact.SizeBytes)),
                    _ => Task.FromResult<ArtifactContentDto?>(new ArtifactContentDto(
                        artifact.ArtifactId, artifact.RunId, artifact.Name, artifact.MediaType,
                        artifact.Sha256, Convert.ToBase64String(bytes), artifact.SizeBytes)),
                }, root, default);

            Assert.True(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.StartsWith($"- \"{valid.Name}\"", evidence);
            Assert.Contains($"\"{failedRead.Name}\": unavailable", evidence);
            Assert.Contains($"\"{invalidContent.Name}\": unavailable", evidence);
            Assert.Contains($"\"{valid.Name}\":", evidence);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Truncated_headers_do_not_use_the_limit_before_a_valid_after_screenshot()
    {
        var validBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "visual-review", "clean-footer.png"));
        var corruptBytes = validBytes[..20];
        var corrupt = Enumerable.Range(0, 100)
            .Select(index => Artifact($"corrupt-{index}", $"bars/after-{index:D3}.png", corruptBytes));
        var valid = Artifact("valid", "bars/after-999.png", validBytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync(
                [.. corrupt, valid],
                (artifact, _) => Task.FromResult<ArtifactContentDto?>(new ArtifactContentDto(
                    artifact.ArtifactId, artifact.RunId, artifact.Name, artifact.MediaType,
                    artifact.Sha256,
                    Convert.ToBase64String(artifact.ArtifactId == "valid" ? validBytes : corruptBytes),
                    artifact.SizeBytes)), root, default);

            Assert.Equal(8, evidence.Split("invalid image", StringSplitOptions.None).Length - 1);
            Assert.Contains("92 more failed screenshots omitted from this list", evidence);
            Assert.StartsWith($"- \"{valid.Name}\"", evidence);
            Assert.Contains($"\"{valid.Name}\":", evidence);
            Assert.DoesNotContain("omitted by the 12-image review limit", evidence);
            Assert.True(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "visual-evidence"), "*-image-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Webp_after_screenshot_uploaded_as_octet_stream_is_readable_evidence()
    {
        var bytes = VisualScreenshotFixture.Webp(1920, 1080);
        var artifact = VisualScreenshotFixture.Artifact("after", "run-1", "bars/after-1920-light.webp",
            "application/octet-stream", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            Assert.True(VisualReviewEvidence.IsScreenshot(artifact));
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([artifact],
                (_, _) => Task.FromResult<ArtifactContentDto?>(VisualScreenshotFixture.Content(artifact, bytes)),
                root, default);

            Assert.StartsWith($"- \"{artifact.Name}\"", evidence);
            Assert.Contains("after-image-01.webp", evidence);
            Assert.True(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.Equal(evidence, VisualReviewScreenshotBundle.LoadEvidence(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Image_extension_without_an_image_header_is_rejected_whatever_its_media_type()
    {
        var bytes = "not an image"u8.ToArray();
        var artifact = VisualScreenshotFixture.Artifact("text", "run-1", "bars/after-1920-light.png",
            "image/png", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([artifact],
                (_, _) => Task.FromResult<ArtifactContentDto?>(VisualScreenshotFixture.Content(artifact, bytes)),
                root, default);

            Assert.Contains($"\"{artifact.Name}\": invalid image", evidence);
            Assert.False(VisualReviewScreenshotBundle.HasReadableAfterScreenshot(root));
            Assert.Equal(evidence, VisualReviewScreenshotBundle.LoadEvidence(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Screenshot_removed_after_preparation_is_named_unavailable_on_reload()
    {
        var bytes = VisualScreenshotFixture.Png(320, 180);
        var artifact = VisualScreenshotFixture.Artifact("after", "run-1", "bars/after-1920-dark.png",
            "image/png", bytes);
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        try
        {
            var evidence = await VisualReviewScreenshotBundle.CaptureAsync([artifact],
                (_, _) => Task.FromResult<ArtifactContentDto?>(VisualScreenshotFixture.Content(artifact, bytes)),
                root, default);
            Assert.EndsWith(" (dark theme)", evidence);
            File.Delete(Path.Combine(root, VisualReviewScreenshotBundle.DirectoryName, "after-image-01.png"));

            Assert.Equal($"- \"{artifact.Name}\": unavailable after preparation",
                VisualReviewScreenshotBundle.LoadEvidence(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Attempt_without_a_screenshot_index_has_no_delivered_screenshots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"visual-review-{Guid.NewGuid():N}");
        Assert.Equal(VisualReviewScreenshotBundle.NoScreenshots, VisualReviewScreenshotBundle.LoadEvidence(root));
    }

    [Theory]
    [InlineData("results/bars/after-1920-light.webp", "image/webp")]
    [InlineData("results/bars/after-1920-light.WEBP", "image/webp")]
    [InlineData("results/bars/after-1920-light.png", "image/png")]
    [InlineData("results/bars/after-1920-light.jpeg", "image/jpeg")]
    public void Coding_runner_uploads_screenshots_with_an_image_media_type(string path, string expected)
        => Assert.Equal(expected, TaskServerClient.MediaTypeForPath(path));

    [Theory]
    [InlineData("bars/after-1920-light.png", "light")]
    [InlineData("bars/dark/after-1920.png", "dark")]
    [InlineData("bars/after_1920_DARK.webp", "dark")]
    [InlineData("bars/highlight-1920.png", null)]
    [InlineData("bars/after-1920.png", null)]
    public void Screenshot_theme_is_read_from_the_delivered_name(string name, string? expected)
        => Assert.Equal(expected, VisualReviewScreenshotBundle.Theme(name));

    [Theory]
    [InlineData("bars/before-1920-light.png", false)]
    [InlineData("before.png", false)]
    [InlineData("bars/before/1920-light.png", false)]
    [InlineData("bars/before-1920-light/footer.png", false)]
    [InlineData("bars/footer-before/screenshot.png", false)]
    [InlineData("bars/1920-light-before.png", false)]
    [InlineData("bars/after-1920-light.png", true)]
    [InlineData("bars/clean-footer.png", true)]
    public void Screenshot_stage_uses_delivered_artifact_name(string name, bool expectedAfter)
    {
        Assert.Equal(expectedAfter, VisualReviewEvidence.IsAfterScreenshot(name));
    }

    private static ArtifactDto Artifact(string id, string name, byte[] bytes) => new(
        id, "run-1", name, "image/png", Convert.ToHexStringLower(SHA256.HashData(bytes)),
        bytes.Length, "key", 1, DateTime.UnixEpoch);

    [Fact]
    public void Visual_prompt_receives_product_and_admin_rules()
    {
        var root = Path.Combine(Path.GetTempPath(), $"visual-rules-{Guid.NewGuid():N}");
        try
        {
            var product = Path.Combine(root, "docs/quality/design/style-guide-hard-rules.md");
            var admin = Path.Combine(root, "docs/operations/admin-design-guideline/index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(product)!);
            Directory.CreateDirectory(Path.GetDirectoryName(admin)!);
            File.WriteAllText(product, "No decorative left accent line on a card.");
            File.WriteAllText(admin, "<li>Light and dark themes MUST preserve contrast and spacing.</li>");

            var prompt = VisualReviewScreenshotBundle.PromptSection(
                "- bars/after.png: /review/visual-evidence/image-01.png", root);

            Assert.Contains("No decorative left accent", prompt);
            Assert.Contains("Light and dark themes MUST preserve contrast", prompt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
