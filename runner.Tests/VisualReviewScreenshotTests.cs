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
            var image = Assert.Single(Directory.GetFiles(Path.Combine(root, "visual-evidence")));
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
        var evidence = await VisualReviewScreenshotBundle.CaptureAsync([], (_, _) =>
            Task.FromResult<ArtifactContentDto?>(null), root, default);
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
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "visual-evidence")).Length);
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
            Assert.Contains("omitted by the 100-image review limit", evidence);
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
            Assert.Single(Directory.GetFiles(Path.Combine(root, "visual-evidence")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
