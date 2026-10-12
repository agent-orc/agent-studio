using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2841: a ReviewInfra successor carries the deterministic verify results
/// that passed in the failed attempt instead of executing them again, and
/// executes only what failed or did not run. Reproduced with a fake aspect
/// whose first call times out (exit 124, the AGT-2836 AspectTimeout).
/// </summary>
public sealed class ReviewVerifyResultReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "review-verify-reuse-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _reviewRoot;
    private DateTime _now = new(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc);
    private int _portBase = 27100;

    public ReviewVerifyResultReuseTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _reviewRoot = Path.Combine(_root, "review");
    }

    [Fact]
    public async Task Aspect_timeout_successor_reuses_passed_verify_results_and_reruns_the_aspect()
    {
        if (OperatingSystem.IsWindows()) return;
        var (sha, _) = await SeedOriginAsync();
        var aspect = await TimeoutOnceAspectAsync();
        IReadOnlyList<ReviewCommandDto> plan = [Verify("verify-1"), Verify("verify-2"), aspect];

        var failed = Workspace("attempt-failed", sha, plan);
        await failed.PrepareAsync(null!, default);
        var timeout = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => failed.ExecutePlanAsync(default));
        Assert.Equal("AspectTimeout", timeout.Classification);
        await failed.CleanupAsync();

        _now = _now.AddMinutes(4);
        var successor = Workspace("attempt-successor", sha, plan, reuseFrom: ["attempt-failed"]);
        await successor.PrepareAsync(null!, default);
        var evidence = await successor.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        // Each verify command executed exactly once, in the failed attempt; the
        // aspect that timed out ran again.
        Assert.Equal(1, Invocations("verify-1"));
        Assert.Equal(1, Invocations("verify-2"));
        Assert.Equal(2, Invocations("aspect"));
        foreach (var stepId in new[] { "verify-1", "verify-2" })
        {
            var command = Assert.Single(evidence.Commands, item => item.StepId == stepId);
            Assert.Equal("attempt-failed", command.VerifyReusedFromAttemptId);
            Assert.Equal(240, command.VerifyReusedAgeSeconds);
            Assert.Equal(0, command.ExitCode);
            // The full stdout of the original run is this attempt's artefact.
            Assert.Equal(
                $"{stepId} output",
                (await File.ReadAllTextAsync(Path.Combine(
                    successor.ArtifactPath, $"candidate.{stepId}.stdout.log"))).Trim());
            var verdict = Assert.Single(evidence.Verdicts, item => item.EvidenceChecked == $"command:{stepId}");
            Assert.Equal("pass", verdict.Status);
            Assert.Equal(ReviewCommandVerdictPolicy.CommandPassed, verdict.Classification);
            Assert.Contains(
                "verify result reused from attempt attempt-failed (4m)",
                verdict.Summary,
                StringComparison.Ordinal);
        }
        var rerunAspect = Assert.Single(evidence.Commands, item => item.StepId == aspect.StepId);
        Assert.Null(rerunAspect.VerifyReusedFromAttemptId);
        // The Result-SHA settled without ReviewInfra: nothing remains to reuse.
        Assert.Empty(StoredResults(successor));
    }

    [Fact]
    public async Task Only_the_failed_and_the_unrun_commands_execute_in_the_successor()
    {
        if (OperatingSystem.IsWindows()) return;
        var (sha, _) = await SeedOriginAsync();
        var toolGone = Path.Combine(_root, "tool-gone-once");
        await File.WriteAllTextAsync(toolGone, "");
        // verify-2 loses its toolchain once (exit 127, ToolUnavailable), so
        // verify-3 never runs in the failed attempt.
        IReadOnlyList<ReviewCommandDto> plan =
        [
            Verify("verify-1"),
            Verify("verify-2", $"if [ -f '{toolGone}' ]; then rm '{toolGone}'; exit 127; fi"),
            Verify("verify-3"),
        ];

        var failed = Workspace("attempt-tool-gone", sha, plan);
        await failed.PrepareAsync(null!, default);
        var exception = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => failed.ExecutePlanAsync(default));
        Assert.Equal("ToolUnavailable", exception.Classification);
        // A failed result is never stored; only the pass of verify-1 is.
        Assert.Single(StoredResults(failed));
        await failed.CleanupAsync();

        var successor = Workspace("attempt-after-tool", sha, plan, reuseFrom: ["attempt-tool-gone"]);
        await successor.PrepareAsync(null!, default);
        var evidence = await successor.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal(1, Invocations("verify-1"));
        Assert.Equal(2, Invocations("verify-2"));
        Assert.Equal(1, Invocations("verify-3"));
        Assert.Equal(
            ["verify-1"],
            evidence.Commands.Where(item => item.VerifyReusedFromAttemptId is not null)
                .Select(item => item.StepId)
                .ToArray());
    }

    [Theory]
    [InlineData("other result sha")]
    [InlineData("other command line")]
    [InlineData("plan names no source attempt")]
    [InlineData("plan names another attempt")]
    public async Task A_stored_pass_is_not_reused_when_its_identity_does_not_match(string variant)
    {
        if (OperatingSystem.IsWindows()) return;
        var (sha, nextSha) = await SeedOriginAsync();
        var failed = Workspace("attempt-source", sha, [Verify("verify-1"), await TimeoutOnceAspectAsync()]);
        await failed.PrepareAsync(null!, default);
        await Assert.ThrowsAsync<ReviewInfrastructureException>(() => failed.ExecutePlanAsync(default));
        await failed.CleanupAsync();

        var successor = variant switch
        {
            "other result sha" => Workspace(
                "attempt-next", nextSha, [Verify("verify-1")], ["attempt-source"], "refs/heads/task/next"),
            "other command line" => Workspace(
                "attempt-next", sha, [Verify("verify-1", ": changed")], ["attempt-source"]),
            "plan names no source attempt" => Workspace("attempt-next", sha, [Verify("verify-1")]),
            "plan names another attempt" => Workspace(
                "attempt-next", sha, [Verify("verify-1")], ["attempt-elsewhere"]),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
        await successor.PrepareAsync(null!, default);
        var evidence = await successor.ExecutePlanAsync(default);

        Assert.Equal(2, Invocations("verify-1"));
        Assert.Null(Assert.Single(evidence.Commands).VerifyReusedFromAttemptId);
        Assert.DoesNotContain(
            "verify result reused",
            Assert.Single(evidence.Verdicts).Summary,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("same key", true)]
    [InlineData("other repository", false)]
    [InlineData("other result sha", false)]
    [InlineData("other command", false)]
    [InlineData("other toolchain", false)]
    [InlineData("failed result", false)]
    [InlineData("non-pass verdict", false)]
    [InlineData("attempt not named by the plan", false)]
    [InlineData("expired", false)]
    public void Verify_cache_reuse_decision_matrix(string variant, bool reusable)
    {
        var key = new ReviewVerifyCacheKey("repo", new string('a', 40), "command", "toolchain");
        var entry = new VerifyCacheEntry(
            key.RepositoryId, key.ResultSha, key.CommandHash, key.ToolchainFingerprint,
            "attempt-failed", "verify-1", key.ResultSha, new string('t', 40), 0, "pass",
            "stdout", "stderr", _now.AddMinutes(-10));
        entry = variant switch
        {
            "same key" => entry,
            "other repository" => entry with { RepositoryId = "other-repo" },
            "other result sha" => entry with { ResultSha = new string('b', 40) },
            "other command" => entry with { CommandHash = "other-command" },
            "other toolchain" => entry with { ToolchainFingerprint = "other-toolchain" },
            "failed result" => entry with { ExitCode = 1 },
            "non-pass verdict" => entry with { VerdictStatus = "block" },
            "attempt not named by the plan" => entry with { AttemptId = "attempt-other" },
            "expired" => entry with { CreatedAt = _now.AddHours(-25) },
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

        Assert.Equal(
            reusable,
            ReviewVerifyResultCache.IsReusable(entry, key, ["attempt-failed"], utcNow: _now));
    }

    [Fact]
    public void A_build_stays_executed_while_its_no_build_test_executes()
    {
        var build = new ReviewCommandDto("verify-1", "build-tests", "sh", ["-lc", "dotnet build"]);
        var test = new ReviewCommandDto("verify-2", "build-tests", "sh", ["-lc", "dotnet test --no-build"]);
        var lint = new ReviewCommandDto("verify-3", "lint", "sh", ["-lc", "npm run lint"]);
        var plan = new ReviewPlanDto([build, test, lint], ["build-tests", "lint"]);

        Assert.Equal(
            ["verify-3"],
            ReviewVerifyResultCache.WithoutBuildsOfExecutedTests(plan.Commands, plan, ["verify-1", "verify-3"]));
        Assert.Equal(
            ["verify-1", "verify-2", "verify-3"],
            ReviewVerifyResultCache.WithoutBuildsOfExecutedTests(
                plan.Commands, plan, ["verify-1", "verify-2", "verify-3"]));
    }

    [Fact]
    public async Task A_failed_result_cannot_be_stored()
    {
        var entry = new VerifyCacheEntry(
            "repo", new string('a', 40), "command", "toolchain", "attempt-failed", "verify-1",
            new string('a', 40), new string('t', 40), 1, "block", "stdout", "stderr", _now);

        await Assert.ThrowsAsync<ArgumentException>(() => ReviewVerifyResultCache.WriteAsync(
            Path.Combine(_root, "never.json"), entry, default));
        Assert.False(File.Exists(Path.Combine(_root, "never.json")));
    }

    private ReviewCommandDto Verify(string stepId, string prefix = ":")
        => new(
            stepId,
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", $"printf '{stepId}\\n' >> '{InvocationLog}'; {prefix}; printf '{stepId} output\\n'"]);

    private async Task<ReviewCommandDto> TimeoutOnceAspectAsync()
    {
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "aspect-timed-out");
        var fakeCodex = Path.Combine(_root, "fake-codex-timeout-once.sh");
        await File.WriteAllTextAsync(
            fakeCodex,
            "#!/bin/sh\n"
            + $"printf 'aspect\\n' >> '{InvocationLog}'\n"
            + $"if [ ! -f '{marker}' ]; then touch '{marker}'; exit 124; fi\n"
            + "printf '%s\\n' '{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"[[ASPECT_VERDICT: status=pass; summary=Aspect inspected the exact subject.]]\"}}'\n");
        File.SetUnixFileMode(
            fakeCodex,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _fakeCodex = fakeCodex;
        return new ReviewCommandDto(
            "aspect-code-quality",
            "code-quality",
            "codex",
            [],
            TimeoutSeconds: 30,
            ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Inspect the exact result and return the required aspect sentinel.",
            CliType: CliSelection.CodexCli,
            Model: "gpt-5.4-mini",
            ThinkingLevel: "high");
    }

    private string? _fakeCodex;

    private string InvocationLog => Path.Combine(_root, "invocations.log");

    private int Invocations(string name)
        => File.Exists(InvocationLog) ? File.ReadAllLines(InvocationLog).Count(line => line == name) : 0;

    private static IReadOnlyList<string> StoredResults(RemoteReviewWorkspace workspace)
        => Directory.Exists(workspace.VerifyCacheRoot)
            ? Directory.EnumerateFiles(workspace.VerifyCacheRoot, "*.json", SearchOption.AllDirectories).ToArray()
            : [];

    private RemoteReviewWorkspace Workspace(
        string attemptId,
        string sha,
        IReadOnlyList<ReviewCommandDto> commands,
        IReadOnlyList<string>? reuseFrom = null,
        string resultRef = "refs/heads/main")
    {
        var portBase = _portBase += 8;
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-verify-reuse",
            "run-verify-reuse",
            TaskServerClient.RepositoryIdentity(_origin)!,
            _origin,
            sha,
            resultRef,
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(
                commands,
                commands.Select(command => command.Aspect).Distinct().ToArray(),
                IntegrationRef: "refs/heads/main",
                VerifyReuseFromAttemptIds: reuseFrom),
            _now);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            1,
            _now,
            _now.AddMinutes(2),
            "active",
            $"review-{attemptId}-f1",
            portBase);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "review-executor",
            RunnerName = "review-executor",
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = _reviewRoot,
            BaseBranch = "main",
            ClaudeCliBin = "unused",
            CodexCliBin = _fakeCodex ?? "codex",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
        };
        return new RemoteReviewWorkspace(
            options,
            subject,
            lease,
            _ => { },
            _ => Task.FromResult<FailureFingerprintHistoryDto?>(null),
            utcNow: () => _now);
    }

    /// <summary>main carries the reviewed result; task/next a different Result-SHA on top of it.</summary>
    private async Task<(string Sha, string NextSha)> SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "immutable product");
        await GitAsync(seed, "add", "product.txt");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "result");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        var sha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
        await GitAsync(seed, "checkout", "-b", "task/next");
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "next product");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-am", "next result");
        await GitAsync(seed, "push", "origin", "task/next");
        return (sha, (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim());
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    public void Dispose()
    {
        ResilientDirectory.TryDelete(_root);
    }
}
