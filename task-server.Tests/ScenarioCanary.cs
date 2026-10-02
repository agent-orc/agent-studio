using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;
using static AgentStudio.TestSupport.BuiltProcessLauncher;
using static AgentStudio.TestSupport.ProcessWaiters;

namespace TaskServer.Tests;

public sealed partial class ScenarioContext
{
    private static bool LiveProviderReview =>
        Environment.GetEnvironmentVariable("SCENARIO_PROVIDER_REVIEW") == "1";

    private static ReviewPlanDto CanaryReviewPlan()
    {
        var commands = new List<ReviewCommandDto>
        {
            new("fixture-checks", "build-tests", "sh",
                ["-c", "test -s scenario-run-log.txt && sh tests/known-passing.sh && ! sh tests/known-failing.sh"],
                TimeoutSeconds: 30),
        };
        if (LiveProviderReview)
            commands.Add(new ReviewCommandDto(
                "provider-review", "requirement-fit", "codex", [], TimeoutSeconds: 180,
                ExecutionKind: ReviewCommandKinds.AgentAspect,
                Prompt: "Review this tiny fixture's supplied coding diff. The task was to run the known passing and failing shell checks and commit their results log. Inspect the log and checks. Do not edit files. Return [[ASPECT_VERDICT: status=pass; summary=checks recorded]] only if the log correctly reports both outcomes; otherwise return a block verdict citing the exact file and defect.",
                CliType: "codex", Model: "gpt-5.6-sol", ThinkingLevel: "medium"));
        return new ReviewPlanDto(commands,
            LiveProviderReview ? ["build-tests", "requirement-fit"] : ["build-tests"],
            IntegrationRef: "refs/heads/main");
    }

    private async Task<ReviewAttemptDto> ExecuteCanaryReviewAsync(ReviewSubjectDto subject)
    {
        // Start the separately built host, with its own deployment dependencies.
        // Loading its DLL into the web test host would mix their runtime graphs.
        var root = NewTempDirectory();
        var reviewer = StartBuilt(_root, "runner", "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_AUTH_TOKEN"] = _reviewCredential,
                ["RUNNER_AUTH_TOKEN_FILE"] = null,
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_ROLE"] = "review",
                ["RUNNER_HOST_CODING_SLOTS"] = "0",
                ["RUNNER_HOST_REVIEW_SLOTS"] = "1",
                ["RUNNER_HOST_GATE_SLOTS"] = "0",
            },
            "--poll", "--server", _serverUrl,
            "--runner-id", ReviewExecutorId, "--runner-name", ReviewExecutorId,
            "--hostname", ReviewHostId, "--role", "review",
            "--git-remote", _bareRepositoryPath,
            "--workdir", Path.Combine(root, "work"),
            "--review-workdir", Path.Combine(root, "review"),
            "--state-dir", Path.Combine(root, "state"),
            "--cli", "codex", "--ttl", "15", "--max-parallelism", "1",
            // Shared CI host load must not prevent this single bounded fixture
            // from starting. Production admission defaults remain unchanged.
            "--claim-max-load-per-core", "1000",
            "--poll-seconds", "1");
        _disposables.Add(reviewer);
        ReviewAttemptDto? review = null;
        await WaitForConditionAsync(async () =>
        {
            var records = await _serverClient.GetFromJsonAsync<List<AuditRecordDto>>(
                "/api/v1/management/audit");
            var audit = records?.FirstOrDefault(row => row.Action == "review.reported");
            if (audit is null) return false;
            review = await _serverClient.GetFromJsonAsync<ReviewAttemptDto>(
                $"/api/v1/reviews/attempts/{audit.TargetId}");
            return review?.CleanedAt is not null;
        }, reviewer, TimeSpan.FromSeconds(240), "the real review daemon reported and cleaned its coding subject");
        Assert.NotNull(review);
        Assert.Equal(subject.SubjectId, review.SubjectId);
        Assert.True(review.Outcome == "Pass",
            $"Review {review.AttemptId}: {review.Outcome} ({review.FailureClassification}). " +
            string.Join(Environment.NewLine, reviewer.OutputLines.TakeLast(20)));
        return review;
    }

    private async Task PublishCanaryAsync(ReviewSubjectDto subject, ReviewAttemptDto review)
    {
        // This is the supervised one-box acceptance publisher, scoped to the
        // disposable fixture. It does not stand in for I08's autonomous owner.
        Assert.Equal("Pass", review.Outcome);
        Assert.Equal(subject.SubjectId, review.SubjectId);
        Assert.Equal(_codingRun.RunId, subject.SourceRunId);
        Assert.Equal(_codingRun.ResultSha, subject.ExpectedResultSha);
        var records = await _serverClient.GetFromJsonAsync<List<AuditRecordDto>>(
            "/api/v1/management/audit");
        var audit = Assert.Single(records!, row =>
            row.Action == "review.reported" && row.TargetId == review.AttemptId);
        using var receipt = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal(subject.ExpectedResultSha, receipt.RootElement.GetProperty("ActualHead").GetString());
        var reviewReportSha256 = receipt.RootElement.GetProperty("payloadHash").GetString();
        var publisher = NewTempDirectory();
        await RunAsync("git", ["clone", _bareRepositoryPath, publisher], _root);
        var baseline = (await GitOutputAsync(publisher, "rev-parse", "HEAD")).Trim();
        await RunAsync("git", ["fetch", "origin", subject.ResultRef!], publisher);
        var fetched = (await GitOutputAsync(publisher, "rev-parse", "FETCH_HEAD")).Trim();
        Assert.Equal(subject.ExpectedResultSha, fetched);
        Assert.NotEqual(baseline, fetched);
        await RunAsync("git", ["merge", "--ff-only", fetched], publisher);
        await RunAsync("git", ["push", $"--force-with-lease=refs/heads/main:{baseline}",
            "origin", "HEAD:refs/heads/main"], publisher);
        var published = (await GitOutputAsync(publisher, "ls-remote", "origin", "refs/heads/main"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(subject.ExpectedResultSha, published);
        if (Environment.GetEnvironmentVariable("SCENARIO_REPORT_DIR") is { Length: > 0 } reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "canary-publication.json"),
                JsonSerializer.Serialize(new
                {
                    codingRun = subject.SourceRunId, subjectId = subject.SubjectId,
                    reviewAttempt = review.AttemptId, review.Outcome,
                    reviewReportSha256,
                    providerAuthenticatedReview = LiveProviderReview,
                    publication = "supervised-disposable-fixture",
                    canonicalRef = "refs/heads/main", beforeSha = baseline,
                    reviewedSha = subject.ExpectedResultSha, publishedSha = published,
                    imageEvidence = "source-built", studioDetached = IsCompose,
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task<string> GitOutputAsync(string directory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
