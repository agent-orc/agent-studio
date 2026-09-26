using Xunit;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentRunner.Tests;

public sealed class RemoteArtifactTransferTests
{
    [Fact]
    public async Task Artifact_413_preserves_the_full_typed_problem_for_runner_logs()
    {
        var problem = JsonSerializer.Serialize(new
        {
            type = "artifact-request-too-large",
            title = new string('x', 320),
            status = 413,
            limitBytes = 25L * 1024 * 1024,
            receivedBytes = 30L * 1024 * 1024,
        });
        using var http = new HttpClient(new ArtifactLimitHandler(problem))
        {
            BaseAddress = new Uri("http://task-server"),
        };
        using var client = new TaskServerClient(http, "runner-under-test");

        var error = await Assert.ThrowsAsync<TaskServerException>(() =>
            client.UploadArtifactsAsync(new ArtifactIngestRequest(
                "AGT-1", [new RunnerArtifactUpload("results/proof.txt", "cHJvb2Y=")]),
                CancellationToken.None));

        Assert.Equal(413, error.StatusCode);
        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }

    private sealed class ArtifactLimitHandler(string problem) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
            {
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json"),
            });
    }

    [Fact]
    public void Oversized_and_build_output_files_are_skipped_but_bounded_files_are_selected()
    {
        var limits = new ArtifactTransferLimitsResponse(25 * 1024 * 1024, 10 * 1024 * 1024, 12 * 1024 * 1024);
        var files = new[]
        {
            ("/results/report.md", "report.md", 2L * 1024 * 1024),
            ("/results/trace.zip", "playwright/spec/trace.zip", 11L * 1024 * 1024),
            ("/results/module.js", "node_modules/pkg/module.js", 1L),
            ("/results/proof.png", "proof.png", 3L * 1024 * 1024),
        };

        var (selected, skipped) = ArtifactTransferPolicy.Select("/results", files, limits);

        Assert.Equal(["results/proof.png", "results/report.md"], selected.Select(file => file.RelativePath));
        Assert.Contains(skipped, issue => issue.Path.EndsWith("trace.zip", StringComparison.Ordinal));
        Assert.Contains(skipped, issue => issue.Path.Contains("node_modules", StringComparison.Ordinal));
        Assert.All(skipped, issue => Assert.Equal(ArtifactTransferOutcomes.ArtifactTooLarge, issue.Outcome));
    }

    [Fact]
    public void Playwright_traces_and_videos_are_skipped_even_when_below_the_file_cap()
    {
        var limits = new ArtifactTransferLimitsResponse(
            25 * 1024 * 1024,
            20 * 1024 * 1024,
            100 * 1024 * 1024);
        var files = new[]
        {
            ("/results/trace.zip", "playwright/example/trace.zip", 2L * 1024 * 1024),
            ("/results/video.webm", "playwright/example/video.webm", 3L * 1024 * 1024),
            ("/results/report.md", "report.md", 1L),
        };

        var (selected, skipped) = ArtifactTransferPolicy.Select("/results", files, limits);

        Assert.Equal(["results/report.md"], selected.Select(file => file.RelativePath));
        Assert.Contains(skipped, issue =>
            issue.Path.EndsWith("trace.zip", StringComparison.Ordinal)
            && issue.Reason.Contains("Playwright trace", StringComparison.Ordinal));
        Assert.Contains(skipped, issue =>
            issue.Path.EndsWith("video.webm", StringComparison.Ordinal)
            && issue.Reason.Contains("video", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Thirty_mebibyte_trace_is_withheld_with_path_size_and_sha_in_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "artifact-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trace = Path.Combine(root, "playwright", "trace.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
            await using (var stream = File.Create(trace))
                stream.SetLength(30L * 1024 * 1024);
            var limits = new ArtifactTransferLimitsResponse(
                25L * 1024 * 1024, 8L * 1024 * 1024, 100L * 1024 * 1024);
            var (selected, skipped) = ArtifactTransferPolicy.Select(
                root, RemoteTaskRunner.ObserveResultFiles(root), limits);
            Assert.Empty(selected);
            var issue = Assert.Single(skipped);

            var entries = await RemoteTaskRunner.BuildWithheldManifestEntriesAsync(
                root, skipped, CancellationToken.None);
            var entry = Assert.Single(entries);
            Assert.Equal("results/playwright/trace.zip", entry.Path);
            Assert.Equal(30L * 1024 * 1024, entry.SizeBytes);
            Assert.Equal("withheld", entry.TransferStatus);
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            using var manifest = JsonDocument.Parse(RemoteTaskRunner.BuildArtifactManifest(entries).Json);
            Assert.Equal(entry.Sha256,
                manifest.RootElement[0].GetProperty("sha256").GetString());
            Assert.Equal(issue.Reason,
                manifest.RootElement[0].GetProperty("reason").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Per_file_cap_rejection_names_the_file_budget_not_the_request_limit()
    {
        var limits = new ArtifactTransferLimitsResponse(
            25 * 1024 * 1024,
            10 * 1024 * 1024,
            100 * 1024 * 1024);

        var (_, skipped) = ArtifactTransferPolicy.Select(
            "/results",
            [("/results/report.bin", "report.bin", 11L * 1024 * 1024)],
            limits);

        var issue = Assert.Single(skipped);
        Assert.Contains("10 MB per-file result budget", issue.Reason);
        Assert.DoesNotContain("25 MB upload limit", issue.Reason);
    }

    [Theory]
    [InlineData(413)]
    [InlineData(507)]
    public void Capacity_rejections_are_typed_artifact_too_large(int statusCode)
    {
        var exception = new TaskServerException(statusCode, "capacity refused");

        Assert.True(ArtifactTransferPolicy.IsCapacityRejection(exception));
    }

    [Fact]
    public void Prompt_states_the_server_advertised_file_and_total_budgets()
    {
        var prompt = RemoteRunPrompt.Build(
            "Do the work.",
            modeFraming: null,
            resultsDirectory: "/tmp/results",
            artifactLimits: new ArtifactTransferLimitsResponse(
                25 * 1024 * 1024,
                10 * 1024 * 1024,
                42 * 1024 * 1024));

        Assert.Contains("10 MB", prompt);
        Assert.Contains("42 MB total", prompt);
        Assert.Contains("traces and videos are not kept unless the task explicitly asks", prompt);
    }

    [Fact]
    public void Complete_acknowledgement_accepts_every_uploaded_result_path()
    {
        var uploads = new List<RunnerArtifactUpload>
        {
            new("results/deliverables.md", "ZGVsaXZlcmFibGVz"),
            new("results/nested/proof.txt", "cHJvb2Y="),
        };
        var response = new ArtifactIngestResponse(
            "AGT-1",
            2,
            ["results/nested/proof.txt", "results/deliverables.md"],
            ResultDocumentGenerated: true,
            ResultDocumentStatus: "generated");

        RemoteTaskRunner.ValidateArtifactAcknowledgement("AGT-1", uploads, response);
    }

    [Fact]
    public void Partial_acknowledgement_is_rejected_for_that_single_file_request()
    {
        var uploads = new List<RunnerArtifactUpload>
        {
            new("results/deliverables.md", "ZGVsaXZlcmFibGVz"),
            new("results/nested/proof.txt", "cHJvb2Y="),
        };
        var response = new ArtifactIngestResponse(
            "AGT-1",
            1,
            ["results/deliverables.md"]);

        var error = Assert.Throws<InvalidDataException>(() =>
            RemoteTaskRunner.ValidateArtifactAcknowledgement("AGT-1", uploads, response));

        Assert.Contains("1/2 artifact(s)", error.Message);
    }
}
