using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteReviewWorkspaceTests : IDisposable
{
    [Trait("Category", "ReviewFlaky")]
    private sealed class ReviewFlakyClassTraitFixture
    {
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remote-review-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _reviewRoot;

    public RemoteReviewWorkspaceTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _reviewRoot = Path.Combine(_root, "review");
    }

    public static TheoryData<string, string[]> NodeFailureFixtures => new()
    {
        {
            "npm-jest.stdout.txt",
            ["src/cart/cart.service.spec.ts > CartService > calculates the total"]
        },
        {
            "ng-karma.stdout.txt",
            ["CartComponent removes an item after confirmation"]
        },
        {
            "npm-vitest.stderr.txt",
            [
                "src/math.spec.ts > arithmetic > adds positive values",
                "src/math.spec.ts > arithmetic > rejects NaN",
            ]
        },
        {
            "node-test.stdout.txt",
            ["rejects an expired lease", "returns the current lease"]
        },
        {
            "npm-lifecycle.stderr.txt",
            ["npm script test:unit"]
        },
        {
            "npm-legacy.stderr.txt",
            ["npm script test"]
        },
    };

    [Theory]
    [MemberData(nameof(NodeFailureFixtures))]
    public void Parsed_test_failures_understands_node_test_runner_output(
        string fixtureName,
        string[] expected)
    {
        var output = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "review-failures",
            fixtureName));
        var result = fixtureName.Contains("stderr", StringComparison.Ordinal)
            ? new ProcessResult(1, string.Empty, output)
            : new ProcessResult(1, output, string.Empty);

        Assert.Equal(expected, RemoteReviewWorkspace.ParsedTestFailures(result));
    }

    [Fact]
    public void Parsed_test_failures_ignores_failure_text_when_process_succeeds()
    {
        var result = new ProcessResult(0, "FAIL src/example.spec.ts > suite > test", string.Empty);

        Assert.Empty(RemoteReviewWorkspace.ParsedTestFailures(result));
    }

    [Fact]
    public void Parsed_test_failures_strips_terminal_colours_and_ignores_tap_todo_items()
    {
        var result = new ProcessResult(
            1,
            "\u001b[31m FAIL  src/example.spec.ts > suite > fails\u001b[39m\n" +
            "not ok 2 - planned follow-up # TODO pending implementation",
            string.Empty);

        Assert.Equal(
            ["src/example.spec.ts > suite > fails"],
            RemoteReviewWorkspace.ParsedTestFailures(result));
    }

    [Fact]
    public void Review_flaky_trait_index_reads_xunit_class_and_method_traits()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

        var index = ReviewFlakyTestIndex.Discover(repositoryRoot);

        Assert.True(index.Contains(
            "AgentRunner.Tests.RemoteTaskRunnerRestartTests.Restarted_runner_follows_fake_job_and_delivers_completion_without_a_zombie_lease"));
        Assert.True(index.Contains(
            "AgentRunner.Tests.RemoteReviewWorkspaceTests.ReviewFlakyClassTraitFixture.Any_test_method"));
        Assert.False(index.Contains(
            "AgentRunner.Tests.RemoteTaskRunnerRestartTests.Persisted_slot_state_written_before_the_base_sha_field_still_loads"));
    }

    [Fact]
    public void Review_failure_parser_quarantines_only_a_marked_failure_that_passes_its_retry()
    {
        const string marked = "Product.Tests.ProcessTiming";
        const string ordinary = "Product.Tests.ProductRule";
        var initial = BaselineComparison.Create(
            new string('a', 40),
            [],
            [marked, ordinary],
            cacheHit: false);
        var index = new ReviewFlakyTestIndex(methods: [marked]);

        var retried = initial.Reclassify([], index);
        var verdict = RemoteReviewWorkspace.BaselineVerdict(
            BaselineCommand("exit 0"),
            retried);

        Assert.Empty(retried.NewFailures);
        Assert.Equal([marked], retried.FlakyQuarantinedFailures);
        Assert.Equal("pass", verdict.Status);
        Assert.Equal("FlakyQuarantine", verdict.Classification);
        Assert.Contains(marked, verdict.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(ordinary, verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_failure_parser_keeps_a_reproduced_marked_failure_blocking()
    {
        const string marked = "Product.Tests.ProcessTiming";
        var initial = BaselineComparison.Create(
            new string('a', 40),
            [],
            [marked],
            cacheHit: false);

        var retried = initial.Reclassify(
            [marked],
            new ReviewFlakyTestIndex(methods: [marked]));
        var verdict = RemoteReviewWorkspace.BaselineVerdict(
            BaselineCommand("exit 1"),
            retried);

        Assert.Equal([marked], retried.NewFailures);
        Assert.Empty(retried.FlakyQuarantinedFailures);
        Assert.Equal("block", verdict.Status);
        Assert.Equal("NewTestFailures", verdict.Classification);
    }

    [Fact]
    public async Task Exact_sha_workspace_records_head_tree_commands_environment_and_clean_after()
    {
        var sha = await SeedOriginAsync();
        var (workspace, _) = Workspace(
            "attempt-a", sha,
            [new ReviewCommandDto("verify", "build-tests", "git", ["rev-parse", "HEAD"])],
            24000);

        var prepared = await workspace.PrepareAsync(null!, default);
        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal(sha, prepared.ActualHead);
        Assert.False(prepared.DirtyBefore);
        Assert.Equal(sha, Assert.Single(evidence.Commands).HeadBefore);
        Assert.Equal("Pass", evidence.Outcome);
        Assert.False(evidence.Workspace.DirtyAfter);
        Assert.Equal("review-attempt-a-f1", evidence.Workspace.ResourceNamespace);
        Assert.Equal("24000", workspace.ProcessEnvironment()["PORT"]);
        Assert.StartsWith(workspace.AttemptRoot, workspace.ProcessEnvironment()["XDG_CACHE_HOME"]);
        var environment = workspace.EnvironmentEvidence();
        Assert.Contains("sha256=", environment.Toolchain["git"], StringComparison.Ordinal);
        Assert.Contains("sha256=", environment.Toolchain["command:verify"], StringComparison.Ordinal);
        Assert.Equal(workspace.BaselineCacheRoot, environment.Isolation["baselineResultCache"]);
        Assert.True(await workspace.CleanupAsync());
        Assert.False(Directory.Exists(workspace.AttemptRoot));
    }

    [Fact]
    public async Task Agent_aspect_runs_read_only_on_remote_host_and_reports_attributed_evidence()
    {
        if (OperatingSystem.IsWindows()) return;
        var sha = await SeedOriginAsync();
        var fakeCodex = Path.Combine(_root, "fake-codex.sh");
        await File.WriteAllTextAsync(fakeCodex, """
            #!/bin/sh
            printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"Looks good.\n[[ASPECT_VERDICT: status=pass; summary=Remote aspect inspected the exact subject.]]"}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":21,"output_tokens":8,"cached_input_tokens":5}}'
            """);
        File.SetUnixFileMode(
            fakeCodex,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var command = new ReviewCommandDto(
            "aspect-code-quality",
            "code-quality",
            "codex",
            [],
            TimeoutSeconds: 30,
            ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Inspect the exact result and return the required aspect sentinel.",
            CliType: AgentCliProcess.CodexCli,
            Model: "gpt-5.4-mini",
            ThinkingLevel: "high");
        var (workspace, _) = Workspace(
            "attempt-agent-aspect",
            sha,
            [command],
            24100,
            codexCliBin: fakeCodex);

        await workspace.PrepareAsync(null!, default);
        var evidence = await workspace.ExecutePlanAsync(default);

        var executed = Assert.Single(evidence.Commands);
        Assert.Equal("Pass", evidence.Outcome);
        Assert.Equal("remote", executed.ExecutionLocation);
        Assert.Equal("review-host", executed.HostId);
        Assert.Equal("review-executor", executed.ExecutorId);
        Assert.Equal("attempt-agent-aspect", executed.AttemptId);
        Assert.Equal(ReviewCommandKinds.AgentAspect, executed.ExecutionKind);
        Assert.Equal("gpt-5.4-mini", executed.Model);
        Assert.Equal("high", executed.ThinkingLevel);
        Assert.Equal(21, executed.InputTokens);
        Assert.Equal(8, executed.OutputTokens);
        Assert.Equal(5, executed.CacheReadTokens);
        Assert.Contains(evidence.Artifacts, artifact =>
            artifact.Sha256 == executed.StdoutSha256 && artifact.ContentBase64 is not null);
        Assert.Equal("pass", Assert.Single(evidence.Verdicts).Status);
        Assert.False(evidence.Workspace.DirtyAfter);
    }

    [Fact]
    public void LoggedOutAgentToolchain_IsReviewInfrastructure()
    {
        var command = new ReviewCommandDto(
            "aspect-requirement-fit",
            "requirement-fit",
            "claude",
            [],
            TimeoutSeconds: 30,
            ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Inspect the exact result and return the required aspect sentinel.",
            CliType: AgentCliProcess.ClaudeCli,
            Model: "claude-sonnet-4-6",
            ThinkingLevel: "high");
        var classification = RemoteReviewWorkspace.ReviewCommandInfrastructureClassification(
            command,
            new ProcessResult(
                1,
                string.Empty,
                "Not logged in. Run claude auth login to sign in."));

        Assert.Equal("ProviderAuthenticationUnavailable", classification);
    }

    [Fact]
    public async Task Node_fixture_without_node_modules_prepares_before_angular_style_build()
    {
        var sha = await SeedOriginWithFilesAsync(new Dictionary<string, string>
        {
            ["frontend/package.json"] = """
                {
                  "name": "remote-review-node",
                  "version": "1.0.0",
                  "scripts": { "build": "ng build" },
                  "devDependencies": { "fixture-cli": "file:fixture-cli" }
                }
                """,
            ["frontend/package-lock.json"] = """
                {
                  "name": "remote-review-node",
                  "version": "1.0.0",
                  "lockfileVersion": 3,
                  "requires": true,
                  "packages": {
                    "": { "name": "remote-review-node", "version": "1.0.0", "devDependencies": { "fixture-cli": "file:fixture-cli" } },
                    "fixture-cli": { "name": "fixture-cli", "version": "1.0.0", "bin": { "ng": "index.js" } },
                    "node_modules/fixture-cli": { "resolved": "fixture-cli", "link": true }
                  }
                }
                """,
            ["frontend/fixture-cli/package.json"] = """
                { "name": "fixture-cli", "version": "1.0.0", "bin": { "ng": "index.js" } }
                """,
            ["frontend/fixture-cli/index.js"] = "#!/usr/bin/env node\nconsole.log('angular-build=ready');\n",
        });
        var preparation = new ReviewPreparationCommandDto(
            "prepare-1",
            PosixShell.RequirePath(),
            ["-c", "npm ci"],
            "frontend",
            DependencyScopes:
            [
                new ReviewDependencyScopeDto("frontend", ["package-lock.json"]),
            ]);
        var (workspace, _) = Workspace(
            "attempt-node-prepare",
            sha,
            [new ReviewCommandDto(
                "verify-node",
                "build-tests",
                PosixShell.RequirePath(),
                ["-c", "npm --prefix frontend run build"],
                TimeoutSeconds: 120)],
            24004,
            preparation: [preparation],
            preserveGlobs: ["frontend/node_modules", "frontend/.angular"]);

        await workspace.PrepareAsync(null!, default);
        Assert.False(Directory.Exists(Path.Combine(workspace.RepositoryPath, "frontend", "node_modules")));
        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        Assert.Collection(evidence.Commands,
            install =>
            {
                Assert.Equal("preparation", install.Phase);
                Assert.Equal("candidate", install.WorkspaceRole);
                Assert.Equal(0, install.ExitCode);
                Assert.False(install.DependencyCacheHit);
            },
            verify =>
            {
                Assert.Equal("verification", verify.Phase);
                Assert.Equal(0, verify.ExitCode);
            });
        Assert.False(evidence.Workspace.DirtyAfter);
    }

    [Fact]
    public async Task Candidate_and_baseline_run_the_same_preparation_contract()
    {
        var (baselineSha, subjectSha) = await SeedSubjectBranchAsync();
        var preparation = new ReviewPreparationCommandDto(
            "prepare-1",
            PosixShell.RequirePath(),
            ["-c", "mkdir -p node_modules && printf ready > node_modules/prepared"],
            DependencyScopes:
            [
                new ReviewDependencyScopeDto("", []),
            ]);
        var command = new ReviewCommandDto(
            "verify-2",
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", "test -f node_modules/prepared || exit 127; if grep -q subject product.txt; then exit 1; fi"],
            CompareToBaseline: true);
        var (workspace, _) = Workspace(
            "attempt-symmetry",
            subjectSha,
            [command],
            24006,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main",
            preparation: [preparation],
            preserveGlobs: ["node_modules"]);

        await workspace.PrepareAsync(null!, default);
        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("ProductFailure", evidence.Outcome);
        var preparationEvidence = evidence.Commands
            .Where(item => item.Phase == "preparation")
            .ToArray();
        Assert.Contains(preparationEvidence, item => item.WorkspaceRole == "candidate");
        Assert.Contains(preparationEvidence, item =>
            item.WorkspaceRole.StartsWith("baseline-", StringComparison.Ordinal));
        Assert.All(preparationEvidence, item => Assert.Equal(0, item.ExitCode));
    }

    [Fact]
    public async Task Dependency_cache_reuses_unchanged_lock_and_reinstalls_after_digest_change()
    {
        var counter = Path.Combine(_root, "install-counter.txt");
        var sha = await SeedOriginWithFilesAsync(new Dictionary<string, string>
        {
            ["package-lock.json"] = "lock-v1",
            [".gitignore"] = "node_modules/\n.nm-state\n",
        });
        var shell = $"mkdir -p node_modules && printf x >> '{counter}'";
        var preparation = new ReviewPreparationCommandDto(
            "prepare-1",
            PosixShell.RequirePath(),
            ["-c", shell],
            DependencyScopes:
            [
                new ReviewDependencyScopeDto("", ["package-lock.json"]),
            ]);
        var verify = new ReviewCommandDto(
            "verify",
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", "test -d node_modules"]);

        async Task<ReviewExecutionEvidence> RunAsync(string attempt, string revision)
        {
            var (workspace, _) = Workspace(
                attempt,
                revision,
                [verify],
                24010,
                resultRef: revision,
                preparation: [preparation],
                preserveGlobs: ["node_modules"]);
            await workspace.PrepareAsync(null!, default);
            return await workspace.ExecutePlanAsync(default);
        }

        var cold = await RunAsync("cache-cold", sha);
        var warm = await RunAsync("cache-warm", sha);
        var seed = Path.Combine(_root, "seed");
        await File.WriteAllTextAsync(Path.Combine(seed, "package-lock.json"), "lock-v2");
        await GitAsync(seed, "add", "package-lock.json");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "change lock");
        await GitAsync(seed, "push", "origin", "main");
        var changedSha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
        var changed = await RunAsync("cache-changed", changedSha);

        Assert.False(cold.Commands[0].DependencyCacheHit);
        Assert.True(warm.Commands[0].DependencyCacheHit);
        Assert.Contains(changed.Commands[0].DependencyCache!, item => item.Reason == "lock-changed");
        Assert.Equal(2, (await File.ReadAllTextAsync(counter)).Length);
    }

    [Fact]
    public async Task Preparation_failure_is_review_infrastructure_with_unabridged_command_evidence()
    {
        var sha = await SeedOriginAsync();
        var preparation = new ReviewPreparationCommandDto(
            "prepare-fails",
            PosixShell.RequirePath(),
            ["-c", "printf 'complete stdout'; printf 'complete stderr' >&2; exit 9"],
            TimeoutSeconds: 17);
        var (workspace, _) = Workspace(
            "attempt-prepare-fails",
            sha,
            [new ReviewCommandDto("must-not-run", "build-tests", "git", ["status"])],
            24012,
            preparation: [preparation]);
        await workspace.PrepareAsync(null!, default);

        var exception = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        Assert.Equal("PreparationFailed", exception.Classification);
        var evidence = Assert.IsType<ReviewExecutionEvidence>(exception.Evidence);
        Assert.Equal("ReviewInfra", evidence.Outcome);
        var command = Assert.Single(evidence.Commands);
        Assert.Equal("prepare-fails", command.StepId);
        Assert.Equal(9, command.ExitCode);
        Assert.Equal(17_000, command.Budget!.LimitMs);
        Assert.Contains("exit=9", exception.Message, StringComparison.Ordinal);
        Assert.Equal("complete stdout\n", ArtifactText(evidence.Artifacts, command.StdoutSha256));
        Assert.Equal("complete stderr\n", ArtifactText(evidence.Artifacts, command.StderrSha256));
    }

    [Fact]
    public async Task Missing_preparation_directory_is_named_in_failure_detail()
    {
        var sha = await SeedOriginAsync();
        var preparation = new ReviewPreparationCommandDto(
            "prepare-missing",
            PosixShell.RequirePath(),
            ["-c", "npm ci"],
            "stale-salvage");
        var (workspace, _) = Workspace(
            "attempt-prepare-missing",
            sha,
            [new ReviewCommandDto("must-not-run", "build-tests", "git", ["status"])],
            24013,
            preparation: [preparation]);
        await workspace.PrepareAsync(null!, default);

        var exception = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        var missingPath = Path.Combine(workspace.RepositoryPath, "stale-salvage");
        Assert.Equal("PreparationFailed", exception.Classification);
        Assert.Contains(
            $"Dependency preparation directory is missing: {missingPath}",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_toolchain_exit_127_is_never_a_product_failure()
    {
        var sha = await SeedOriginAsync();
        var preparation = new ReviewPreparationCommandDto(
            "prepare-1",
            PosixShell.RequirePath(),
            ["-c", "mkdir -p node_modules && printf ready > node_modules/prepared"],
            DependencyScopes:
            [
                new ReviewDependencyScopeDto("", []),
            ]);
        var (workspace, _) = Workspace(
            "attempt-tool-missing",
            sha,
            [new ReviewCommandDto(
                "verify-node",
                "build-tests",
                PosixShell.RequirePath(),
                ["-c", "exit 127"])],
            24014,
            preparation: [preparation],
            preserveGlobs: ["node_modules"]);
        await workspace.PrepareAsync(null!, default);

        var exception = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        Assert.Equal("ToolUnavailable", exception.Classification);
        Assert.Equal("ReviewInfra", exception.Evidence!.Outcome);
        Assert.Equal(
            127,
            Assert.Single(exception.Evidence.Commands, item => item.Phase == "verification").ExitCode);
        Assert.False(exception.Evidence.Workspace.DirtyAfter);
    }

    [Fact]
    public void Retention_removes_only_expired_inactive_attempt_workspaces()
    {
        var now = new DateTime(2026, 8, 2, 18, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_reviewRoot);
        var expired = CreateReviewDirectory("review-expired-f1", now.AddHours(-73));
        var active = CreateReviewDirectory("review-active-f2", now.AddDays(-8));
        var young = CreateReviewDirectory("review-young-f1", now.AddHours(-71));
        var baseline = CreateReviewDirectory(".baseline-cache", now.AddDays(-30));
        var unrelated = CreateReviewDirectory("operator-notes", now.AddDays(-30));

        var result = ReviewWorkspaceRetention.Sweep(
            _reviewRoot,
            ["review-active-f2"],
            now,
            _ => { });

        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists(baseline));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal(new ReviewWorkspaceRetentionResult(3, 1, 1, 1, 1), result);
    }

    [Fact]
    public void Retention_preserves_attempt_at_exact_72_hour_boundary()
    {
        var now = new DateTime(2026, 8, 2, 18, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_reviewRoot);
        var boundary = CreateReviewDirectory(
            "review-boundary-f1",
            now - ReviewWorkspaceRetention.MaximumOrphanAge);

        var result = ReviewWorkspaceRetention.Sweep(_reviewRoot, [], now, _ => { });

        Assert.True(Directory.Exists(boundary));
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.YoungSkipped);
    }

    [Fact]
    public async Task Wrong_sha_fails_before_any_review_command()
    {
        await SeedOriginAsync();
        var wrong = new string('a', 40);
        var (workspace, _) = Workspace(
            "attempt-wrong", wrong,
            [new ReviewCommandDto("must-not-run", "requirements", "git", ["status"])],
            24008);

        var error = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.PrepareAsync(null!, default));

        Assert.Equal("ShaMismatch", error.Classification);
        Assert.Empty(Directory.Exists(workspace.ArtifactPath)
            ? Directory.EnumerateFiles(workspace.ArtifactPath)
            : []);
    }

    [Fact]
    public async Task Review_mutation_is_detected_even_when_the_command_exits_zero()
    {
        var sha = await SeedOriginAsync();
        var (workspace, _) = Workspace(
            "attempt-mutates", sha,
            [new ReviewCommandDto(
                "mutation", "code-quality", PosixShell.RequirePath(),
                ["-c", "printf mutation > unexpected.txt"])],
            24016);
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.True(evidence.Workspace.DirtyAfter);
        Assert.Equal("Pass", evidence.Outcome);
    }

    [Fact]
    public async Task Three_parallel_reviews_use_distinct_workspaces_caches_ports_and_outputs()
    {
        var sha = await SeedOriginAsync();
        var items = Enumerable.Range(0, 3)
            .Select(index => Workspace(
                $"attempt-{index}", sha,
                [new ReviewCommandDto($"step-{index}", "artifacts", "git", ["status", "--short"])],
                25000 + index * 8))
            .ToArray();

        await Task.WhenAll(items.Select(item => item.Workspace.PrepareAsync(null!, default)));
        var evidence = await Task.WhenAll(items.Select(item => item.Workspace.ExecutePlanAsync(default)));

        Assert.Equal(3, items.Select(item => item.Workspace.AttemptRoot).Distinct().Count());
        Assert.Equal(3, items.Select(item => item.Workspace.CachePath).Distinct().Count());
        Assert.Equal(3, items.Select(item => item.Workspace.ProcessEnvironment()["PORT"]).Distinct().Count());
        Assert.Equal(6, evidence.SelectMany(item => item.Artifacts)
            .Select(item => item.Name).Distinct().Count());
        Assert.All(evidence, item => Assert.False(item.Workspace.DirtyAfter));
    }

    [Fact]
    public void Restart_takeover_of_one_attempt_uses_a_new_fenced_workspace()
    {
        var sha = new string('a', 40);
        var first = Workspace("attempt-restart", sha, [], 25100, fence: 1).Workspace;
        var takeover = Workspace("attempt-restart", sha, [], 25108, fence: 2).Workspace;

        Assert.NotEqual(first.AttemptRoot, takeover.AttemptRoot);
        Assert.NotEqual(
            first.ProcessEnvironment()["AGENT_REVIEW_NAMESPACE"],
            takeover.ProcessEnvironment()["AGENT_REVIEW_NAMESPACE"]);
    }

    [Fact]
    public async Task Review_child_process_does_not_inherit_unapproved_coding_credentials()
    {
        var sha = await SeedOriginAsync();
        const string variable = "AGENT_TEST_WRITE_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, "must-not-leak");
        try
        {
            var (workspace, _) = Workspace(
                "attempt-credentials", sha,
                [new ReviewCommandDto(
                    "credential-check", "evidence", PosixShell.RequirePath(),
                    ["-c", $"test -z \"${{{variable}:-}}\""])],
                25032);
            await workspace.PrepareAsync(null!, default);

            var evidence = await workspace.ExecutePlanAsync(default);

            Assert.Equal("Pass", evidence.Outcome);
            Assert.Equal(0, Assert.Single(evidence.Commands).ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Baseline_comparison_blocks_only_new_test_failures_and_names_them()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "if grep -q subject product.txt; then " +
            "printf '  Failed Product.ExistingFailure [1 ms]\\n  Failed Product.NewFailure [1 ms]\\n'; " +
            "else printf '  Failed Product.ExistingFailure [1 ms]\\n'; fi; exit 1");
        var (workspace, _) = Workspace(
            "attempt-new-failure",
            subjectSha,
            [command],
            26000,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("ProductFailure", evidence.Outcome);
        var commandEvidence = CandidateVerification(evidence);
        Assert.Equal(["Product.NewFailure"], commandEvidence.NewFailures);
        Assert.Equal(["Product.ExistingFailure"], commandEvidence.PreExistingFailures);
        Assert.True(commandEvidence.RetryPerformed);
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("block", verdict.Status);
        Assert.Contains("1 new failures: Product.NewFailure", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains("1 pre-existing failures: Product.ExistingFailure", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Baseline_comparison_blocks_only_new_vitest_failures_and_names_them()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "if grep -q subject product.txt; then " +
            "printf ' FAIL  src/math.spec.ts > arithmetic > existing failure\\n" +
            " FAIL  src/math.spec.ts > arithmetic > new failure\\n'; " +
            "else printf ' FAIL  src/math.spec.ts > arithmetic > existing failure\\n'; fi; exit 1");
        var (workspace, _) = Workspace(
            "attempt-new-vitest-failure",
            subjectSha,
            [command],
            26004,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("ProductFailure", evidence.Outcome);
        var commandEvidence = CandidateVerification(evidence);
        Assert.Equal(
            ["src/math.spec.ts > arithmetic > new failure"],
            commandEvidence.NewFailures);
        Assert.Equal(
            ["src/math.spec.ts > arithmetic > existing failure"],
            commandEvidence.PreExistingFailures);
    }

    [Fact]
    public async Task Baseline_comparison_classifies_shared_red_tests_as_pre_existing()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "printf '  Failed Product.ExistingFailure [1 ms]\\n'; exit 1");
        var (workspace, _) = Workspace(
            "attempt-pre-existing",
            subjectSha,
            [command],
            26008,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        var commandEvidence = CandidateVerification(evidence);
        Assert.Empty(commandEvidence.NewFailures!);
        Assert.Equal(["Product.ExistingFailure"], commandEvidence.PreExistingFailures);
        Assert.False(commandEvidence.RetryPerformed);
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("pass", verdict.Status);
        Assert.Contains("0 new failures", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Baseline_result_is_reused_for_same_repository_sha_and_command()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "printf '  Failed Product.ExistingFailure [1 ms]\\n'; exit 1");
        var first = Workspace(
            "attempt-cache-fill",
            subjectSha,
            [command],
            26016,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main").Workspace;
        await first.PrepareAsync(null!, default);
        var firstEvidence = await first.ExecutePlanAsync(default);
        await first.CleanupAsync();

        var second = Workspace(
            "attempt-cache-hit",
            subjectSha,
            [command],
            26024,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main").Workspace;
        await second.PrepareAsync(null!, default);
        var secondEvidence = await second.ExecutePlanAsync(default);

        Assert.False(CandidateVerification(firstEvidence).BaselineCacheHit);
        Assert.True(CandidateVerification(secondEvidence).BaselineCacheHit);
        Assert.Single(Directory.EnumerateFiles(second.BaselineCacheRoot, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Review_flaky_new_failure_gets_one_retry_and_is_quarantined_when_it_disappears()
    {
        const string failure =
            "AgentRunner.Tests.RemoteTaskRunnerRestartTests.Restarted_runner_follows_fake_job_and_delivers_completion_without_a_zombie_lease";
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var command = BaselineCommand(
            "if grep -q subject product.txt; then " +
            "if test ! -f \"$TMPDIR/retry-seen\"; then touch \"$TMPDIR/retry-seen\"; " +
            $"printf '  Failed {failure} [1 ms]\\n'; exit 1; fi; fi; exit 0");
        var (workspace, _) = Workspace(
            "attempt-flake",
            subjectSha,
            [command],
            26032,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);
        var assemblyDirectory = Path.Combine(workspace.RepositoryPath, ".git", "bin");
        Directory.CreateDirectory(assemblyDirectory);
        File.Copy(
            typeof(RemoteReviewWorkspaceTests).Assembly.Location,
            Path.Combine(assemblyDirectory, "AgentRunner.Tests.dll"));

        var evidence = await workspace.ExecutePlanAsync(default);

        Assert.Equal("Pass", evidence.Outcome);
        var commandEvidence = CandidateVerification(evidence);
        Assert.True(commandEvidence.RetryPerformed);
        Assert.Empty(commandEvidence.NewFailures!);
        Assert.Equal([failure], commandEvidence.FlakyQuarantinedFailures);
        Assert.Equal(0, commandEvidence.ExitCode);
        Assert.False(evidence.Workspace.DirtyAfter);
        Assert.Equal("FlakyQuarantine", Assert.Single(evidence.Verdicts).Classification);
    }

    [Fact]
    public async Task Baseline_failure_names_the_base_the_ref_and_the_command_it_used()
    {
        var (baselineSha, subjectSha) = await SeedSubjectBranchAsync();
        // AGT-2220 shape: the baseline command never completes normally. The
        // classification alone said nothing about which base it ran against.
        var command = new ReviewCommandDto(
            "verify-2",
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", "sleep 30"],
            TimeoutSeconds: 1,
            CompareToBaseline: true);
        var (workspace, _) = Workspace(
            "attempt-diagnosis",
            subjectSha,
            [command],
            26040,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/main");
        await workspace.PrepareAsync(null!, default);

        var exception = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        Assert.Equal("BaselineUnavailable", exception.Classification);
        var facts = ReviewInfrastructureDiagnosis.Parse(exception.Message);
        Assert.Equal(baselineSha, facts[ReviewInfrastructureDiagnosis.BaseKey]);
        Assert.Equal("refs/heads/main", facts[ReviewInfrastructureDiagnosis.RefKey]);
        Assert.Equal("verify-2", facts[ReviewInfrastructureDiagnosis.StepKey]);
        Assert.Contains(
            "sleep 30",
            facts[ReviewInfrastructureDiagnosis.CommandKey],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfetchable_integration_ref_is_reported_with_an_unresolved_base()
    {
        var (_, subjectSha) = await SeedSubjectBranchAsync();
        var (workspace, _) = Workspace(
            "attempt-missing-ref",
            subjectSha,
            [BaselineCommand("exit 1")],
            26048,
            resultRef: "refs/heads/task/new-failure",
            integrationRef: "refs/heads/retired-integration-line");
        await workspace.PrepareAsync(null!, default);

        var exception = await Assert.ThrowsAsync<ReviewInfrastructureException>(
            () => workspace.ExecutePlanAsync(default));

        Assert.Equal("BaselineUnavailable", exception.Classification);
        var facts = ReviewInfrastructureDiagnosis.Parse(exception.Message);
        Assert.Equal(
            ReviewInfrastructureDiagnosis.UnresolvedBase,
            facts[ReviewInfrastructureDiagnosis.BaseKey]);
        Assert.Equal(
            "refs/heads/retired-integration-line",
            facts[ReviewInfrastructureDiagnosis.RefKey]);
    }

    private static ReviewCommandDto BaselineCommand(string shell)
        => new(
            "verify-2",
            "build-tests",
            PosixShell.RequirePath(),
            ["-c", shell],
            CompareToBaseline: true);

    private string CreateReviewDirectory(string name, DateTime lastWriteTimeUtc)
    {
        var path = Path.Combine(_reviewRoot, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        return path;
    }

    private (RemoteReviewWorkspace Workspace, ReviewSubjectDto Subject) Workspace(
        string attemptId,
        string sha,
        IReadOnlyList<ReviewCommandDto> commands,
        int portBase,
        long fence = 1,
        string? resultRef = null,
        string? integrationRef = null,
        IReadOnlyList<ReviewPreparationCommandDto>? preparation = null,
        IReadOnlyList<string>? preserveGlobs = null,
        string? codexCliBin = null,
        string? claudeCliBin = null)
    {
        var repositoryId = TaskServerClient.RepositoryIdentity(_origin)!;
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-" + attemptId,
            "run-" + attemptId,
            repositoryId,
            _origin,
            sha,
            resultRef ?? "refs/heads/main",
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(
                commands,
                commands.Select(command => command.Aspect).ToArray(),
                IntegrationRef: integrationRef,
                Preparation: preparation,
                PreserveGlobs: preserveGlobs),
            DateTime.UtcNow);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            fence,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            "active",
            $"review-{attemptId}-f{fence}",
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
            CliBin = "unused",
            CliArgs = "",
            CodexCliBin = codexCliBin ?? "codex",
            ClaudeCliBin = claudeCliBin ?? "claude",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
        };
        return (new RemoteReviewWorkspace(options, subject, lease, _ => { }), subject);
    }

    private async Task<(string BaselineSha, string SubjectSha)> SeedSubjectBranchAsync()
    {
        var baselineSha = await SeedOriginAsync();
        var seed = Path.Combine(_root, "seed");
        var branch = await GitAsync(seed, "branch", "--list", "task/new-failure");
        if (branch.StdOut.Length == 0)
        {
            await GitAsync(seed, "checkout", "-b", "task/new-failure");
            await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "subject product");
            await GitAsync(seed, "add", "product.txt");
            await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "-m", "subject");
            await GitAsync(seed, "push", "origin", "task/new-failure");
        }
        var subjectSha = (await GitAsync(seed, "rev-parse", "task/new-failure")).StdOut.Trim();
        return (baselineSha, subjectSha);
    }

    private async Task<string> SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        if (Directory.Exists(_origin))
            return (await GitAsync(_origin, "rev-parse", "refs/heads/main")).StdOut.Trim();
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "immutable product");
        await GitAsync(seed, "add", "product.txt");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "result");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private async Task<string> SeedOriginWithFilesAsync(IReadOnlyDictionary<string, string> files)
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(seed, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            if (!OperatingSystem.IsWindows()
                && relativePath.Equals("frontend/fixture-cli/index.js", StringComparison.Ordinal))
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(seed, "product.txt"), "immutable product");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "result");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private static string ArtifactText(
        IReadOnlyList<ReviewArtifactEvidenceDto> artifacts,
        string digest)
    {
        var artifact = Assert.Single(artifacts, item => item.Sha256 == digest);
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(artifact.ContentBase64!));
    }

    private static ReviewCommandEvidenceDto CandidateVerification(ReviewExecutionEvidence evidence)
        => Assert.Single(evidence.Commands, item =>
            item is { Phase: "verification", WorkspaceRole: "candidate" });

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
