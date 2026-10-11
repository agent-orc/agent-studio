using System.Text;
using System.Text.Json;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: fixed TRX timestamps only test parser duration and never meet current time.

public sealed class ReviewTestTimingEvidenceTests
{
    [Fact]
    public async Task Trx_stays_on_runner_and_only_summary_content_enters_report()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-test-timing-" + Guid.NewGuid().ToString("N"));
        var reportDirectory = Path.Combine(root, "repository", "suite", "TestResults");
        var artifactDirectory = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(reportDirectory);
        Directory.CreateDirectory(artifactDirectory);
        var trxPath = Path.Combine(reportDirectory, "review-timing.trx");
        var trx = """
                  <TestRun>
                    <Times start="2026-10-11T10:00:00Z" finish="2026-10-11T10:00:03Z" />
                    <TestDefinitions>
                      <UnitTest id="one"><TestMethod className="Example.Tests.FirstTests" name="Slow" /></UnitTest>
                      <UnitTest id="two"><TestMethod className="Example.Tests.OtherTests" name="Fast" /></UnitTest>
                    </TestDefinitions>
                    <Results>
                      <UnitTestResult testId="one" testName="Slow" duration="00:00:02.000" outcome="Passed" />
                      <UnitTestResult testId="two" testName="Fast" duration="00:00:00.250" outcome="Failed" />
                    </Results>
                  </TestRun>
                  """;
        await File.WriteAllTextAsync(trxPath, trx);
        try
        {
            var artifacts = await ReviewTestTimingEvidence.CaptureAsync(
                $"Results File: {trxPath}", root, artifactDirectory,
                "candidate.verify-3.timing", retryPerformed: true, _ => { }, default);

            Assert.Equal(2, artifacts.Count);
            var jsonArtifact = Assert.Single(artifacts, item => item.Name.EndsWith(".json", StringComparison.Ordinal));
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifactDirectory, jsonArtifact.Name)));
            Assert.Equal(2, summary.RootElement.GetProperty("total").GetProperty("tests").GetInt32());
            Assert.Equal(1, summary.RootElement.GetProperty("total").GetProperty("failed").GetInt32());
            Assert.Equal(3, summary.RootElement.GetProperty("total").GetProperty("wallSeconds").GetDouble());
            Assert.Equal(2.0, summary.RootElement.GetProperty("perClassSeconds")
                .GetProperty("Example.Tests.FirstTests").GetDouble());
            Assert.Equal("Slow", summary.RootElement.GetProperty("slowest50")[0].GetProperty("name").GetString());
            Assert.Equal(1, summary.RootElement.GetProperty("retries").GetInt32());

            var retained = Assert.Single(artifacts, item => item.Name.EndsWith(".trx", StringComparison.Ordinal));
            Assert.Null(retained.ContentBase64);
            Assert.Equal(Encoding.UTF8.GetByteCount(trx), retained.SizeBytes);
            Assert.Equal(trx, await File.ReadAllTextAsync(Path.Combine(artifactDirectory, retained.Name)));
            Assert.NotNull(jsonArtifact.ContentBase64);
            Assert.True(jsonArtifact.SizeBytes <= 256 * 1024);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Trx_path_outside_the_review_attempt_is_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-test-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        try
        {
            var artifacts = await ReviewTestTimingEvidence.CaptureAsync(
                "Results File: /outside/review-timing.trx", root, Path.Combine(root, "artifacts"),
                "candidate.verify-3.timing", retryPerformed: false, _ => { }, default);

            Assert.Empty(artifacts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Linked_trx_path_cannot_read_outside_the_review_attempt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "review-test-timing-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "review-test-timing-external-" + Guid.NewGuid().ToString("N") + ".trx");
        var reports = Path.Combine(root, "repository", "TestResults");
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(reports);
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(outside, "<TestRun />");
        var linked = Path.Combine(reports, "linked.trx");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            var captured = await ReviewTestTimingEvidence.CaptureAsync(
                $"Results File: {linked}", root, artifacts, "candidate.verify-3.timing",
                retryPerformed: false, _ => { }, default);

            Assert.Empty(captured);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Oversized_trx_is_rejected_before_reading_or_attaching_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-test-timing-" + Guid.NewGuid().ToString("N"));
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var path = Path.Combine(root, "oversized.trx");
        try
        {
            using (var stream = File.Create(path)) stream.SetLength(64L * 1024 * 1024 + 1);
            var messages = new List<string>();
            var captured = await ReviewTestTimingEvidence.CaptureAsync(
                $"Results File: {path}", root, artifacts, "timing", false, messages.Add, default);
            Assert.Empty(captured);
            Assert.Contains(messages, message => message.Contains("oversized TRX", StringComparison.Ordinal));
            Assert.Empty(Directory.EnumerateFiles(artifacts));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
