using System.Security.Cryptography;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteReviewReportEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-review-evidence-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preparation_failure_card_names_exact_command_and_persists_complete_streams()
    {
        Directory.CreateDirectory(_root);
        var stdout = Encoding.UTF8.GetBytes("full install stdout\n");
        var stderr = Encoding.UTF8.GetBytes("full install stderr\n");
        var stdoutDigest = Digest(stdout);
        var stderrDigest = Digest(stderr);
        var command = new ReviewCommandEvidenceDto(
            "prepare-dependencies",
            "preparation",
            "/bin/bash",
            ["-lc", "dotnet restore Studio.slnx && npm --prefix frontend ci"],
            new string('a', 40),
            new string('a', 40),
            new string('b', 40),
            DateTime.UtcNow.AddSeconds(-3),
            DateTime.UtcNow,
            9,
            null,
            stdoutDigest,
            stderrDigest,
            Phase: "preparation",
            WorkspaceRole: "candidate",
            Budget: new ReviewCommandBudgetEvidenceDto("review-command", 120_000, 3_000, false));
        var request = new ReviewReportRequest(
            "reviewer",
            "instance",
            "lease",
            1,
            "report-key",
            "ReviewInfra",
            "PreparationFailed",
            "Dependency preparation directory is missing: /review/repository/stale-salvage",
            new ReviewWorkspaceProofDto(
                "repo", new string('a', 40), new string('a', 40), new string('b', 40),
                false, false, "workspace", "review-attempt-f1"),
            new ReviewEnvironmentDto(
                "host", "reviewer", "instance", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [command],
            [
                Artifact("candidate.prepare-dependencies.stdout.log", stdout, stdoutDigest),
                Artifact("candidate.prepare-dependencies.stderr.log", stderr, stderrDigest),
            ],
            []);

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root,
            "attempt-1",
            "subject-1",
            request,
            new string('c', 64),
            DateTime.UtcNow,
            default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.Contains("| preparation | candidate | prepare-dependencies |", report, StringComparison.Ordinal);
        Assert.Contains(
            "`/bin/bash -lc dotnet restore Studio.slnx && npm --prefix frontend ci`",
            report,
            StringComparison.Ordinal);
        Assert.Contains("review-command: 3000/120000 ms", report, StringComparison.Ordinal);
        Assert.Contains(
            "**Detail:** Dependency preparation directory is missing: /review/repository/stale-salvage",
            report,
            StringComparison.Ordinal);
        Assert.Contains("[stdout](remote-review-attempt-1-candidate_prepare-dependencies_stdout_log)", report, StringComparison.Ordinal);
        Assert.Contains("[stderr](remote-review-attempt-1-candidate_prepare-dependencies_stderr_log)", report, StringComparison.Ordinal);
        Assert.Equal(
            stdout,
            await File.ReadAllBytesAsync(Path.Combine(
                _root, "remote-review-attempt-1-candidate_prepare-dependencies_stdout_log")));
        Assert.Equal(
            stderr,
            await File.ReadAllBytesAsync(Path.Combine(
                _root, "remote-review-attempt-1-candidate_prepare-dependencies_stderr_log")));
    }

    /// <summary>
    /// AGT-2843: a baseline verify result reused from an earlier attempt must be
    /// visible as such. The grade names the source attempt and the age of the
    /// result, and the card review projection reads that same citation back, so
    /// neither surface presents a skipped baseline run as a fresh one.
    /// </summary>
    [Fact]
    public async Task Reused_baseline_result_is_cited_in_the_grade_and_in_the_card_projection()
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var baselineSha = new string('d', 40);
        var candidate = Command(
            "verify-2",
            "candidate",
            head,
            baselineSha,
            reusedFromAttemptId: "review_earlier",
            reusedAgeSeconds: 7_500);
        var baseline = Command(
            "verify-2",
            $"baseline-{new string('e', 12)}",
            baselineSha,
            baselineSha,
            reusedFromAttemptId: "review_earlier",
            reusedAgeSeconds: 7_500);
        var request = Request(head, [candidate, baseline]);

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_now", "subject-1", request, new string('c', 64),
            new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc), default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.Contains("baselineReused: true", report, StringComparison.Ordinal);
        Assert.Contains(
            "baselineReuse: \"baseline result reused from attempt review_earlier (2h 5m)\"",
            report,
            StringComparison.Ordinal);
        Assert.Contains(
            "| baseline result reused from attempt review_earlier (2h 5m) |",
            report,
            StringComparison.Ordinal);

        var projection = AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null);

        var attempt = Assert.Single(projection.Attempts);
        Assert.True(attempt.BaselineReused);
        Assert.Equal("baseline result reused from attempt review_earlier (2h 5m)", attempt.BaselineReuse);
    }

    [Fact]
    public async Task Baseline_executed_by_this_attempt_is_not_reported_as_reused()
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var baselineSha = new string('d', 40);
        var request = Request(head, [Command("verify-2", "candidate", head, baselineSha)]);

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_now", "subject-1", request, new string('c', 64),
            new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc), default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.DoesNotContain("baselineReused:", report, StringComparison.Ordinal);
        Assert.Contains("| baseline executed in this attempt |", report, StringComparison.Ordinal);

        var attempt = Assert.Single(
            AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null).Attempts);
        Assert.False(attempt.BaselineReused);
        Assert.Null(attempt.BaselineReuse);
    }

    private static ReviewCommandEvidenceDto Command(
        string stepId,
        string workspaceRole,
        string head,
        string? baselineSha,
        string? reusedFromAttemptId = null,
        long reusedAgeSeconds = 0)
        => new(
            stepId,
            "build-tests",
            "/bin/bash",
            ["-c", "dotnet test"],
            head,
            head,
            new string('b', 40),
            new DateTime(2026, 9, 15, 19, 50, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 15, 19, 59, 0, DateTimeKind.Utc),
            1,
            null,
            new string('1', 64),
            new string('2', 64),
            BaselineSha: baselineSha,
            BaselineCacheHit: reusedFromAttemptId is not null,
            Phase: "verification",
            WorkspaceRole: workspaceRole,
            BaselineReusedFromAttemptId: reusedFromAttemptId,
            BaselineReusedAgeSeconds: reusedAgeSeconds);

    private static ReviewReportRequest Request(
        string head,
        IReadOnlyList<ReviewCommandEvidenceDto> commands)
        => new(
            "reviewer",
            "instance",
            "lease",
            1,
            "report-key",
            "ProductFailure",
            "NewTestFailures",
            "One new failure against the baseline.",
            new ReviewWorkspaceProofDto("repo", head, head, new string('b', 40), false, false, "workspace", "ns"),
            new ReviewEnvironmentDto(
                "host", "reviewer", "instance", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            commands,
            [],
            [
                new ReviewVerdictDto(
                    "build-tests",
                    "block",
                    "NewTestFailures",
                    "1 new failures: Product.NewFailure.",
                    "command:verify-2",
                    "Product.NewFailure"),
            ]);

    private static AgentStudio.Shared.TaskInfo Job(string folder) => new()
    {
        Id = "AGT-2843",
        TaskKey = "PROJ-001::AGT-2843",
        Key = "AGT-2843",
        Title = "AGT-2843",
        State = AgentStudio.Shared.TaskStates.HumanReview,
        WatchPath = Path.GetDirectoryName(folder) ?? folder,
        ProjectName = "Demo",
        FolderPath = folder,
    };

    private static ReviewArtifactEvidenceDto Artifact(string name, byte[] content, string digest)
        => new(
            name,
            "text/plain; charset=utf-8",
            digest,
            content.LongLength,
            Convert.ToBase64String(content));

    private static string Digest(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
