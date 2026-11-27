extern alias Runner;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task ObservedModels_SurviveCommandEvidenceAndReceiptReplayWithoutAggregateDoubleCounting()
    {
        var frame = """
            {"type":"result","session_id":"review-session","usage":{"input_tokens":999999},"modelUsage":{
              "claude-opus-5-5":{"inputTokens":10,"outputTokens":2,"cacheReadInputTokens":100},
              "claude-sonnet-5":{"inputTokens":20,"outputTokens":3,"cacheReadInputTokens":200}}}
            """.ReplaceLineEndings("");
        var command = await UsageEvidence(string.Join('\n', frame, frame), "claude-opus-5-5", "claude");
        Assert.Equal("claude-opus-5-5", command.PinnedModel);
        Assert.Equal(command.PinnedModel, command.Model);
        Assert.Equal("per-model", command.ModelSource);
        Assert.Null(RemotePipelineReviewEvidenceProjector.ObservedUsageModel(command));
        Assert.True(command.ModelMismatch);
        Assert.Equal(30, command.InputTokens);
        Assert.Equal(2, command.ModelUsage!.Count);
        var report = Request(new string('a', 40), [command]);
        var pricing = TokenTestServices.Pricing();
        var review = UsageAttempt();
        var calls = RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(pricing, review, report);
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls.Select(call => call.ParticipantId).Distinct().Count());
        Assert.Equal(330, calls.Sum(call => call.InputTokens + call.CacheReadTokens));
        foreach (var row in command.ModelUsage)
        {
            var call = Assert.Single(calls, call => call.Model == row.Model);
            Assert.Equal(command.PinnedModel, call.PinnedModel);
            Assert.Equal(row.ModelMismatch, call.ModelMismatch);
            Assert.True(call.ModelPriced);
            Assert.Equal(pricing.Estimate(row.Model, row.InputTokens, row.OutputTokens,
                row.CacheReadTokens, row.CacheCreationTokens, command.FinishedAt).Total, call.EstimatedApiCostUsd);
        }
        Assert.True(Assert.Single(calls, call => call.Model == "claude-sonnet-5").ModelMismatch);
        var summary = PersistUsageTwice(calls, review.AttemptId);
        Assert.Equal(2, summary.Calls);
        Assert.Equal(30, summary.InputTokens);
        Assert.Equal(300, summary.CacheReadTokens);
        Assert.True(summary.AllModelsPriced);
        Assert.True(summary.HasModelMismatch);
        Assert.Equal(calls.Sum(call => call.EstimatedApiCostUsd), summary.EstimatedApiCostUsd);
    }

    [Theory]
    [InlineData(",\"cached_input_tokens\":0", true, 1000, 0)]
    [InlineData(",\"cached_input_tokens\":900", true, 100, 900)]
    [InlineData("", false, 1000, 0)]
    [InlineData(",\"cached_input_tokens\":null", false, 1000, 0)]
    [InlineData(",\"cached_input_tokens\":\"0\"", false, 1000, 0)]
    [InlineData(",\"cached_input_tokens\":-1", false, 1000, 0)]
    public async Task CodexCacheEvidence_ControlsReceiptCostCompletenessAndReplay(
        string cache, bool complete, long input, long cacheRead)
    {
        var frame = "{\"type\":\"turn.completed\",\"model\":\"gpt-6.1-sol\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":20" + cache + "}}";
        var command = await UsageEvidence(string.Join('\n', frame, frame), "gpt-6-sol", "codex");
        Assert.Equal(input, command.InputTokens);
        Assert.Equal(cacheRead, command.CacheReadTokens);
        Assert.Equal(1000, command.InputTokens + command.CacheReadTokens);
        Assert.Equal(complete ? true : (bool?)null, command.InputIncludesCached);
        Assert.Equal("gpt-6-sol", command.Model);
        Assert.Equal("gpt-6.1-sol", RemotePipelineReviewEvidenceProjector.ObservedUsageModel(command));
        Assert.Equal("gpt-6-sol", command.PinnedModel);
        Assert.True(command.ModelMismatch);
        var report = Request(new string('a', 40), [command]);
        var review = UsageAttempt();
        var calls = RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(TokenTestServices.Pricing(), review, report);
        var call = Assert.Single(calls);
        Assert.Equal(input, call.InputTokens);
        Assert.Equal(cacheRead, call.CacheReadTokens);
        Assert.Equal(command.InputIncludesCached, call.InputIncludesCached);
        Assert.Equal(complete, call.ModelPriced);
        Assert.Equal("gpt-6.1-sol", call.Model);
        Assert.True(call.ModelMismatch);
        Assert.Equal("gpt-6-sol", call.PinnedModel);
        if (complete) Assert.True(call.EstimatedApiCostUsd > 0);
        else Assert.Equal(0m, call.EstimatedApiCostUsd);
        var summary = PersistUsageTwice(calls, review.AttemptId);
        Assert.Single(summary.Entries);
        Assert.Equal(complete, summary.AllModelsPriced);
        Assert.Equal(input, summary.InputTokens);
        Assert.Equal(cacheRead, summary.CacheReadTokens);
    }

    [Fact]
    public async Task UnobservedModel_RemainsAnUnpricedRequestedFallbackInEvidenceAndReceipt()
    {
        var command = await UsageEvidence(
            """{"type":"result","usage":{"input_tokens":10,"output_tokens":2}}""", "claude-opus-5-5", "claude");
        Assert.Equal(command.PinnedModel, command.Model);
        Assert.Equal("requested-fallback", command.ModelSource);
        Assert.Null(RemotePipelineReviewEvidenceProjector.ObservedUsageModel(command));
        Assert.Equal("claude-opus-5-5", command.PinnedModel);
        Assert.False(command.ModelMismatch);
        var calls = RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(TokenTestServices.Pricing(),
            UsageAttempt(), Request(new string('a', 40), [command]));
        var call = Assert.Single(calls);
        Assert.Null(call.Model);
        Assert.Equal(command.PinnedModel, call.PinnedModel);
        Assert.False(call.ModelPriced);
        Assert.Equal(0m, call.EstimatedApiCostUsd);
        Assert.False(PersistUsageTwice(calls, UsageAttempt().AttemptId).AllModelsPriced);
    }

    [Fact]
    public void LegacyCommandWithoutRows_UsesReceiptAwarePricing()
    {
        var command = Command("aspect-code-quality", "candidate", new string('a', 40), null) with
        {
            ExecutionKind = ReviewCommandKinds.AgentAspect,
            Model = "gpt-6-sol", InputTokens = 1000, OutputTokens = 20,
        };
        var call = Assert.Single(RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(TokenTestServices.Pricing(),
            UsageAttempt(), Request(new string('a', 40), [command])));
        Assert.False(call.ModelPriced);
        Assert.Null(call.InputIncludesCached);
        Assert.Equal(0m, call.EstimatedApiCostUsd);
    }

    private async Task<ReviewCommandEvidenceDto> UsageEvidence(string stdout, string pinned, string cli)
    {
        var at = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        var sha = new string('a', 40);
        var plan = new ReviewCommandDto("aspect-code-quality", "code-quality", cli, [],
            ExecutionKind: ReviewCommandKinds.AgentAspect, CliType: cli, Model: pinned, ThinkingLevel: "high");
        var subject = new AgentStudio.TaskServer.Contracts.ReviewSubjectDto("subject", "task", "run", "repo", null,
            sha, null, null, null, "host", "policy", new ReviewPlanDto([plan], [plan.Aspect]), at);
        var lease = new ReviewLeaseDto("lease", "review-usage", "subject", "executor", "instance", "host", 1,
            at, at.AddMinutes(2), "active", "usage-evidence", 25400);
        var workspace = new Runner::AgentRunner.RemoteReviewWorkspace(
            new Runner::AgentRunner.RunnerOptions
            {
                ServerUrl = "http://localhost", RunnerId = "executor", RunnerName = "executor",
                Hostname = "host", BackendName = "remote-review",
                WorkDir = Path.Combine(_root, "coding"), BaseBranch = "main",
                ReviewWorkDir = Path.Combine(_root, "review"), ReviewTempBase = _root,
            },
            subject, lease, _ => { });
        Directory.CreateDirectory(workspace.ArtifactPath);
        var usage = Runner::AgentRunner.RemoteReviewAgentCommandRunner.ParseUsage(stdout, pinned, cli);
        var evidence = await workspace.AddCommandEvidenceAsync(plan.StepId, plan.Aspect, cli, [], sha, sha,
            new Runner::AgentRunner.ProcessResult(0, "Reviewed", ""), at, at.AddSeconds(1), null, 120,
            "verification", "candidate", null, null, false, false, null, [], default,
            plannedCommand: plan, agentUsage: usage);
        // Admission compares these frozen fields with the plan, even when usage differs.
        Assert.Equal(plan.Model, evidence.Model);
        Assert.Equal(plan.ThinkingLevel, evidence.ThinkingLevel);
        // Exercise the independently deployed runner/backend wire boundary.
        return JsonSerializer.Deserialize<ReviewCommandEvidenceDto>(JsonSerializer.Serialize(evidence))!;
    }

    private TaskTokenSummary PersistUsageTwice(IReadOnlyList<TaskTokenCall> calls, string attemptId)
    {
        var folder = Path.Combine(_root, "receipt");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "task.json");
        File.WriteAllText(path, """{"id":"usage-task","title":"Review usage"}""");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root, ["WatchPaths:0:Name"] = "Demo", ["WatchPaths:0:Path"] = _root }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var participant = RemotePipelineReviewEvidenceProjector.ReviewParticipantPrefix + attemptId;
        Assert.True(mutations.SetTokenReceiptEntriesOnFolder(folder, participant, calls));
        var first = File.ReadAllText(path);
        Assert.True(mutations.SetTokenReceiptEntriesOnFolder(folder, participant, calls));
        Assert.Equal(first, File.ReadAllText(path));
        using var document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("tokenSummary").Deserialize<TaskTokenSummary>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static AgentStudio.Shared.ReviewAttemptDto UsageAttempt()
    {
        var at = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        return new AgentStudio.Shared.ReviewAttemptDto("review-usage", "Demo::usage-task", "repo", "run", null,
            new AgentStudio.Shared.ReviewSubjectDto("subject", "repo", new string('a', 40), "run",
                new string('d', 64), "policy", [], at), AttemptLifecycleState.Leased,
            null, 1, 0, at, null, null, null, null, null, []);
    }

    [Fact]
    public async Task SuiteCoverageSurvivesTheReportAndAppearsInTheCardReason()
    {
        Directory.CreateDirectory(_root);
        var request = Request(new string('a', 40), []) with
        {
            SuiteSelection = new(["frontend-unit"], ["backend-routine"],
                ["Only frontend/ changed."]) { AttemptedSuites = ["frontend-unit"] },
        };
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var file = await RemoteReviewReportEvidence.WriteAsync(_root, "selection", "subject",
            request, new string('c', 64), now, default);
        var report = await File.ReadAllTextAsync(Path.Combine(_root, file));
        Assert.Contains("Suites ran: frontend-unit", report);
        Assert.Contains("Suites omitted: backend-routine", report);
        var evidence = AgentStudio.TestRuns.TaskScopedTestEvidenceReader.Read(Job(_root));
        Assert.Contains(evidence.Sources, item => item.Reason.Contains("Suites ran: frontend-unit", StringComparison.Ordinal)
            && item.Reason.Contains("Suites omitted: backend-routine", StringComparison.Ordinal));
    }

    [Fact]
    public void Red_review_input_retains_all_errors_outside_the_output_tail()
    {
        var head = new string('a', 40);
        var now = new DateTime(2026, 11, 23, 4, 34, 0, DateTimeKind.Utc);
        var names = new[] { "Example.First", "Example.Second", "Example.Third" };
        var command = new ReviewCommandEvidenceDto("verify-1", "build-tests", "dotnet",
            ["test", "Tests.csproj", "--no-build"], head, head, new string('b', 40), now, now,
            1, null, new string('1', 64), new string('2', 64), NewFailures: names,
            OutputTail: new("Tail without test messages", "", true, false),
            FailureMessages: names.ToDictionary(name => name, name => "Assertion for " + name));
        AgentStudio.Tasks.DeliveryFailureEvidence.RecordReview(_root, Request(head, [command]));
        var prompt = AgentStudio.Tasks.DeliveryFailureEvidence.Append(_root, "Fix the review.");
        Assert.Contains("dotnet test Tests.csproj --no-build", prompt);
        Assert.Contains(head, prompt);
        foreach (var name in names) Assert.Contains("Assertion for " + name, prompt);
    }

    [Fact]
    public async Task Grade_reports_each_aspect_cache_usage_and_review_total()
    {
        Directory.CreateDirectory(_root);
        var now = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        ReviewCommandEvidenceDto Aspect(string id, long created, long read) => new(
            "aspect-" + id, id, "claude", [], new string('a', 40), new string('a', 40),
            new string('b', 40), now, now, 0, null, new string('1', 64), new string('2', 64),
            ExecutionKind: ReviewCommandKinds.AgentAspect,
            CacheCreationTokens: created, CacheReadTokens: read);
        var request = Request(new string('a', 40),
            [Aspect("requirement-fit", 60_000, 0), Aspect("code-quality", 2_000, 58_000)]);

        var file = await RemoteReviewReportEvidence.WriteAsync(_root, "cache-review", "subject",
            request, new string('c', 64), now, default);
        var grade = await File.ReadAllTextAsync(Path.Combine(_root, file));

        Assert.Contains("| requirement-fit | 60000 | 0 |", grade);
        Assert.Contains("| code-quality | 2000 | 58000 |", grade);
        Assert.Contains("| **Review total** | **62000** | **58000** |", grade);
    }

    [Fact]
    public async Task Grade_names_brief_version_and_skipped_content_aspect()
    {
        Directory.CreateDirectory(_root);
        var request = Request(new string('a', 40), []) with
        {
            BriefSha256 = new string('b', 64),
            SkippedAspects = [new ReviewSkippedAspectDto(
                "concept-fit", "Disabled by project pipeline-step setting.")],
        };

        var file = await RemoteReviewReportEvidence.WriteAsync(
            _root, "concept-review", "concept-subject", request,
            new string('c', 64), new DateTime(2026, 10, 6, 7, 0, 0, DateTimeKind.Utc), default);

        var grade = await File.ReadAllTextAsync(Path.Combine(_root, file));
        Assert.Contains($"briefSha256: \"{new string('b', 64)}\"", grade);
        Assert.Contains("| concept-fit | Disabled by project pipeline-step setting. |", grade);
    }

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
                false, false, "workspace", "review-attempt-f1",
                IntegrationRef: "refs/heads/develop", MergeBaseSha: new string('c', 40),
                IntegrationTipSha: new string('d', 40)),
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
        Assert.Contains("- Integration ref: `refs/heads/develop`", report);
        Assert.Contains($"- Reviewed integration tip: `{new string('d', 40)}`", report);
        Assert.Contains($"- Tree: `{new string('b', 40)}`", report);
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
        Assert.Contains(
            "| [build-tests](aspect-build-tests.md) | block |",
            report,
            StringComparison.Ordinal);
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

    /// <summary>
    /// AGT-2841: a ReviewInfra successor that carried a passed verify result
    /// over from the failed attempt says so in the grade and in the card
    /// projection; the command it executed itself reads as executed.
    /// </summary>
    [Fact]
    public async Task Reused_verify_result_is_cited_in_the_grade_and_in_the_card_projection()
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var reused = Command("verify-1", "candidate", head, null) with
        {
            ExitCode = 0,
            VerifyReusedFromAttemptId = "review_failed",
            VerifyReusedAgeSeconds = 240,
        };
        var executed = Command("verify-2", "candidate", head, null) with { ExitCode = 0 };
        var request = Request(head, [reused, executed]) with
        {
            Outcome = "Pass",
            FailureClassification = null,
        };

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_successor", "subject-1", request, new string('c', 64),
            new DateTime(2026, 10, 12, 21, 4, 0, DateTimeKind.Utc), default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.Contains("verifyReused: true", report, StringComparison.Ordinal);
        Assert.Contains(
            "verifyReuse: \"verify-1: verify result reused from attempt review_failed (4m)\"",
            report,
            StringComparison.Ordinal);
        Assert.Contains(
            "| verify result reused from attempt review_failed (4m) |",
            report,
            StringComparison.Ordinal);
        Assert.Contains("| executed in this attempt |", report, StringComparison.Ordinal);

        var attempt = Assert.Single(
            AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null).Attempts);
        Assert.True(attempt.VerifyReused);
        Assert.Equal("verify-1: verify result reused from attempt review_failed (4m)", attempt.VerifyReuse);
    }

    [Fact]
    public async Task Verify_commands_executed_by_this_attempt_are_not_reported_as_reused()
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var request = Request(head, [Command("verify-2", "candidate", head, null)]);

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_now", "subject-1", request, new string('c', 64),
            new DateTime(2026, 10, 12, 21, 4, 0, DateTimeKind.Utc), default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.DoesNotContain("verifyReused:", report, StringComparison.Ordinal);
        var attempt = Assert.Single(
            AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null).Attempts);
        Assert.False(attempt.VerifyReused);
        Assert.Null(attempt.VerifyReuse);
    }

    /// <summary>
    /// AGT-2863: a review-daemon restart adopts running detached workers, so the
    /// build that graded an attempt can be older than the daemon that reported
    /// it. Executor and fence cannot tell those apart; the grade file names the
    /// worker release, its binary, and the reporting daemon's release.
    /// </summary>
    [Fact]
    public async Task Grade_names_the_worker_release_that_actually_produced_the_verdict()
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var request = Request(head, [Command("verify-2", "candidate", head, null)]) with
        {
            Environment = new ReviewEnvironmentDto(
                "host", "reviewer", "instance", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                new ReviewWorkerProvenanceDto(
                    "20260917T1010Z-v0.5.0-tmpguard-fcdb68b24",
                    "/opt/agent-host/releases/20260917T1010Z-v0.5.0-tmpguard-fcdb68b24/agent-host",
                    "20260917T1550Z-v0.6.0-551484dca")),
        };

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_now", "subject-1", request, new string('c', 64),
            new DateTime(2026, 9, 17, 18, 13, 0, DateTimeKind.Utc), default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.Contains(
            "- Worker release: `20260917T1010Z-v0.5.0-tmpguard-fcdb68b24`",
            report,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Worker binary: `/opt/agent-host/releases/20260917T1010Z-v0.5.0-tmpguard-fcdb68b24/agent-host`",
            report,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Daemon release: `20260917T1550Z-v0.6.0-551484dca`",
            report,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Release provenance: graded by release 20260917T1010Z-v0.5.0-tmpguard-fcdb68b24, "
            + "current 20260917T1550Z-v0.6.0-551484dca",
            report,
            StringComparison.Ordinal);
        Assert.Contains(
            "workerReleaseSuperseded: \"graded by release 20260917T1010Z-v0.5.0-tmpguard-fcdb68b24, "
            + "current 20260917T1550Z-v0.6.0-551484dca\"",
            report,
            StringComparison.Ordinal);

        // Every review surface reads the same projection, so the card cannot
        // present a superseded grade as one from the current release.
        var attempt = Assert.Single(
            AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null).Attempts);
        Assert.Equal("20260917T1010Z-v0.5.0-tmpguard-fcdb68b24", attempt.WorkerReleaseId);
        Assert.Equal("20260917T1550Z-v0.6.0-551484dca", attempt.DaemonReleaseId);
        Assert.Equal(
            "graded by release 20260917T1010Z-v0.5.0-tmpguard-fcdb68b24, "
            + "current 20260917T1550Z-v0.6.0-551484dca",
            attempt.WorkerReleaseSuperseded);
    }

    [Fact]
    public async Task Grade_of_a_matching_release_states_the_build_without_a_superseded_notice()
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var request = Request(head, [Command("verify-2", "candidate", head, null)]) with
        {
            Environment = new ReviewEnvironmentDto(
                "host", "reviewer", "instance", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                new ReviewWorkerProvenanceDto(
                    "20260917T1550Z-v0.6.0-551484dca",
                    "/opt/agent-host/releases/20260917T1550Z-v0.6.0-551484dca/agent-host",
                    "20260917T1550Z-v0.6.0-551484dca")),
        };

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_now", "subject-1", request, new string('c', 64),
            new DateTime(2026, 9, 17, 18, 13, 0, DateTimeKind.Utc), default);

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        Assert.Contains(
            "- Worker release: `20260917T1550Z-v0.6.0-551484dca`",
            report,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Release provenance:", report, StringComparison.Ordinal);
        Assert.DoesNotContain("workerReleaseSuperseded:", report, StringComparison.Ordinal);

        var attempt = Assert.Single(
            AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null).Attempts);
        Assert.Equal("20260917T1550Z-v0.6.0-551484dca", attempt.WorkerReleaseId);
        Assert.Null(attempt.WorkerReleaseSuperseded);
    }

    /// <summary>
    /// AGT-3648 S4-F12: a review host on an older develop build graded a card
    /// while the server ran 3d61e8b. The grade records that at arrival and the
    /// card's review projection carries it; a host on the Stable commit adds
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData("20261105T090000Z-review-f947f2b1c3d4",
        "graded by release 20261105T090000Z-review-f947f2b1c3d4 (commit f947f2b), which differs from Stable 0.9.5 (3d61e8b)")]
    [InlineData("0.9.5+sha.unknown",
        "graded by release 0.9.5+sha.unknown, whose commit is unknown; Stable 0.9.5 (3d61e8b) cannot be confirmed")]
    [InlineData("20261109T101203Z-review-3d61e8b0c4ab", null)]
    public async Task Grade_records_when_the_grading_release_is_not_the_stable_build(
        string workerRelease, string? expected)
    {
        Directory.CreateDirectory(_root);
        var head = new string('a', 40);
        var request = Request(head, [Command("verify-2", "candidate", head, null)]) with
        {
            Environment = new ReviewEnvironmentDto(
                "host", "reviewer", "instance", "linux", "x64", "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                new ReviewWorkerProvenanceDto(workerRelease, "/opt/agent-host/agent-host", workerRelease)),
        };

        var reportFile = await RemoteReviewReportEvidence.WriteAsync(
            _root, "review_now", "subject-1", request, new string('c', 64),
            new DateTime(2026, 11, 9, 18, 13, 0, DateTimeKind.Utc), default,
            new StableReleaseIdentity("0.9.5", "3d61e8b0c4ab", null));

        var report = await File.ReadAllTextAsync(Path.Combine(_root, reportFile));
        var attempt = Assert.Single(
            AgentStudio.Review.ReviewProjectionReader.Read(Job(_root), [], null).Attempts);
        Assert.Equal(expected, attempt.WorkerReleaseStable);
        Assert.Null(attempt.WorkerReleaseSuperseded);
        if (expected is null)
            Assert.DoesNotContain("workerReleaseStable:", report, StringComparison.Ordinal);
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
