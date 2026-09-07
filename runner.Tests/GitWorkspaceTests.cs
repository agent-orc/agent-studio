using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-runner-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _workDir;

    public GitWorkspaceTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _workDir = Path.Combine(_root, "runner");
    }

    [Fact]
    public async Task New_project_clone_sets_fetch_and_push_urls_from_registry()
    {
        await SeedOriginAsync();
        var logs = new List<string>();
        var workspace = CreateProjectWorkspace(_origin, logs.Add);

        await workspace.PrepareAsync(CancellationToken.None);

        Assert.Equal("refs/heads/main", workspace.IntegrationBranchRef);
        Assert.Equal(_origin,
            (await GitAsync(workspace.SharedRepoPath, "remote", "get-url", "origin")).StdOut);
        Assert.Equal(_origin,
            (await GitAsync(workspace.SharedRepoPath, "remote", "get-url", "--push", "origin")).StdOut);
        Assert.Equal(_origin,
            (await GitAsync(workspace.SharedRepoPath, "config", "--get-all", "remote.origin.pushurl")).StdOut);
        Assert.Contains(logs, line => line ==
            $"git-remote-configured projectId=PROJ-016 source=project-registry " +
            $"fetchUrl={_origin} pushUrl={_origin}");
    }

    [Fact]
    public async Task Existing_project_clone_repairs_wrong_push_url_from_registry()
    {
        await SeedOriginAsync();
        var first = CreateProjectWorkspace(_origin);
        await first.PrepareAsync(CancellationToken.None);
        await first.TeardownAsync("Done", CancellationToken.None);
        await GitAsync(first.SharedRepoPath, "config", "--replace-all", "remote.origin.url",
            "https://github.com/agent-orc/stale-fetch.git");
        await GitAsync(first.SharedRepoPath, "config", "--add", "remote.origin.url",
            "https://github.com/agent-orc/another-stale-fetch.git");
        await GitAsync(first.SharedRepoPath, "config", "--replace-all", "remote.origin.pushurl",
            "git@github.com-agentstudio:agent-orc/agent-studio.git");
        await GitAsync(first.SharedRepoPath, "config", "--add", "remote.origin.pushurl",
            "https://github.com/agent-orc/another-fallback.git");

        var refreshed = CreateProjectWorkspace(_origin);
        await refreshed.PrepareAsync(CancellationToken.None);

        Assert.Equal(_origin,
            (await GitAsync(refreshed.SharedRepoPath, "remote", "get-url", "origin")).StdOut);
        Assert.Equal(_origin,
            (await GitAsync(refreshed.SharedRepoPath, "config", "--get-all", "remote.origin.url")).StdOut);
        Assert.Equal(_origin,
            (await GitAsync(refreshed.SharedRepoPath, "remote", "get-url", "--push", "origin")).StdOut);
        Assert.Equal(_origin,
            (await GitAsync(refreshed.SharedRepoPath, "config", "--get-all", "remote.origin.pushurl")).StdOut);
    }

    [Fact]
    public async Task Project_without_registry_url_creates_no_clone_and_reports_not_remote_capable()
    {
        var workspace = CreateProjectWorkspace(repositoryUrl: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workspace.PrepareAsync(CancellationToken.None));

        Assert.Contains("not remote-capable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry has no repository URL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(workspace.ProjectCachePath));
        Assert.False(Directory.Exists(workspace.SharedRepoPath));
    }

    [Fact]
    public async Task Project_claim_without_id_still_never_inherits_host_fallback()
    {
        var workspace = new GitWorkspace(new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = _origin,
            GitPushRemote = "git@github.com-agentstudio:agent-orc/agent-studio.git",
            WorkDir = _workDir,
            BaseBranch = "main",
            CliBin = "test",
            CliArgs = "",
        }, "QS-31", _ => { }, isProjectClone: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workspace.PrepareAsync(CancellationToken.None));

        Assert.Contains("not remote-capable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(workspace.ProjectCachePath));
    }

    [Fact]
    public async Task Project_preflight_creates_shared_clone_verifies_urls_and_proves_push()
    {
        await SeedOriginAsync();
        var result = await GitWorkspace.PreflightProjectAsync(
            PreflightOptions(), "PROJ-042", _origin, "main", _ => { }, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(_origin, result.FetchUrl);
        Assert.Equal(_origin, result.PushUrl);
        var repo = Path.Combine(_workDir, "PROJ-042", "repo");
        Assert.True(Directory.Exists(Path.Combine(repo, ".git")));
        Assert.Equal(_origin, (await GitAsync(repo, "remote", "get-url", "origin")).StdOut);
        Assert.Equal(_origin, (await GitAsync(repo, "remote", "get-url", "--push", "origin")).StdOut);
        var probeRefs = await GitAsync(_origin, "for-each-ref", "--format=%(refname)",
            "refs/heads/runner/runner-test/delivery-preflight-*");
        Assert.Empty(probeRefs.StdOut);
    }

    [Fact]
    public async Task Project_preflight_fails_when_registered_clone_cannot_be_created()
    {
        var missing = Path.Combine(_root, "no-access", "origin.git");
        var result = await GitWorkspace.PreflightProjectAsync(
            PreflightOptions(), "PROJ-042", missing, "main", _ => { }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("clone failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_workDir, "PROJ-042", "repo", ".git")));
    }

    // Linux-only 02.08. (AGT-2472): the rejection is produced by an executable
    // POSIX pre-receive hook; Windows has no executable bit for git to honour.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Project_preflight_fails_when_origin_rejects_the_write_probe()
    {
        PlatformGate.LinuxOnly("the origin rejects through an executable POSIX pre-receive hook");

        await SeedOriginAsync();
        var hook = Path.Combine(_origin, "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, """
            #!/bin/sh
            echo "permission denied by test origin" >&2
            exit 1
            """);
        File.SetUnixFileMode(hook,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await GitWorkspace.PreflightProjectAsync(
            PreflightOptions(), "PROJ-042", _origin, "main", _ => { }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("write probe failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Project_preflight_fails_when_the_delivery_target_branch_does_not_exist()
    {
        await SeedOriginAsync();

        var result = await GitWorkspace.PreflightProjectAsync(
            PreflightOptions(), "PROJ-042", _origin, "develop", _ => { }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("target branch 'develop' does not exist", result.Detail, StringComparison.OrdinalIgnoreCase);
        var probeRefs = await GitAsync(_origin, "for-each-ref", "--format=%(refname)",
            "refs/heads/runner/runner-test/delivery-preflight-*");
        Assert.Empty(probeRefs.StdOut);
    }

    [Fact]
    public async Task Teardown_commits_and_pushes_uncommitted_changes_before_removal()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        var authoritativeMain = (await GitAsync(
            _origin, "rev-parse", "refs/heads/main")).StdOut;
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt"), "valuable work");

        var result = await workspace.TeardownAsync("Unknown", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Equal("runner/runner-test/AGT-2147", result.Branch);
        Assert.Equal(authoritativeMain, (await GitAsync(
            _origin, "rev-parse", "refs/heads/main")).StdOut);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal("valuable work", (await GitAsync(_origin,
            "show", $"refs/heads/{result.Branch}:work.txt")).StdOut);
        Assert.Equal("wip(runner): salvage before teardown - outcome Unknown",
            (await GitAsync(_origin, "log", "-1", "--format=%s", $"refs/heads/{result.Branch}")).StdOut);
    }

    [Theory]
    [InlineData("runner/runner-test/AGT-2147", true)]
    [InlineData("runner/runner-test/AGT-2147-collision-local-remote", true)]
    [InlineData("main", false)]
    [InlineData("develop", false)]
    [InlineData("refs/heads/main", false)]
    [InlineData("runner/runner-test/AGT-9999", false)]
    public void Salvage_target_policy_allows_only_the_card_branch_and_its_collision_refs(
        string targetBranch,
        bool expected)
    {
        Assert.Equal(
            expected,
            GitWorkspace.IsCardScopedSalvageTarget(
                "runner/runner-test/AGT-2147",
                targetBranch));
    }

    [Fact]
    public async Task Teardown_pushes_an_unpushed_local_commit_before_removal()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "committed.txt"), "local commit");
        await GitAsync(workspace.RepoPath, "add", "--all");
        await GitAsync(workspace.RepoPath, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "local work");
        var localHead = (await GitAsync(workspace.RepoPath, "rev-parse", "HEAD")).StdOut;

        var result = await workspace.TeardownAsync("Done", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal(localHead,
            (await GitAsync(_origin, "rev-parse", $"refs/heads/{result.Branch}")).StdOut);
    }

    [Fact]
    public async Task Teardown_removes_a_clean_worktree_without_creating_a_salvage_branch()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);

        var result = await workspace.TeardownAsync("Done", CancellationToken.None);

        Assert.False(result.SecuredWork);
        Assert.False(string.IsNullOrWhiteSpace(result.ResultSha));
        Assert.Equal((await GitAsync(_origin, "rev-parse", "refs/heads/main")).StdOut, result.ResultSha);
        Assert.False(Directory.Exists(workspace.RepoPath));
        var branch = await RunGitAsync(_origin, "show-ref", "--verify", "--quiet",
            "refs/heads/runner/runner-test/AGT-2147");
        Assert.False(branch.Success);
    }

    // Linux-only 02.08. (AGT-2472): WorktreeProcessReaper finds the offending
    // processes through /proc/<pid>/cwd and is a no-op elsewhere.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Teardown_kills_processes_with_cwd_in_worktree_before_removal()
    {
        PlatformGate.LinuxOnly("the worktree process reaper scans /proc/<pid>/cwd");

        await SeedOriginAsync();
        var logs = new List<string>();
        var workspace = CreateWorkspace(logs.Add);
        await workspace.PrepareAsync(CancellationToken.None);
        var processTask = ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "sleep 300 & wait"],
            workingDirectory: workspace.RepoPath,
            isolateProcessGroup: true);
        for (var attempt = 0;
             attempt < 100 && WorktreeProcessReaper.FindByCwd(workspace.RepoPath).Count == 0;
             attempt++)
            await Task.Delay(20);
        Assert.NotEmpty(WorktreeProcessReaper.FindByCwd(workspace.RepoPath));

        await workspace.TeardownAsync("Done", CancellationToken.None);
        var process = await processTask;

        Assert.NotEqual(0, process.ExitCode);
        Assert.False(Directory.Exists(workspace.RepoPath));
        var reapStart = logs.FindIndex(line => line.Contains("worktree-process-reap-started", StringComparison.Ordinal));
        var teardownDone = logs.FindIndex(line => line.Contains("worktree-teardown-completed", StringComparison.Ordinal));
        Assert.True(reapStart >= 0 && teardownDone > reapStart);
    }

    [Fact]
    public async Task Teardown_keeps_the_worktree_when_the_salvage_push_fails()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt"), "must survive");
        var offlineOrigin = _origin + ".offline";
        Directory.Move(_origin, offlineOrigin);

        var error = await Assert.ThrowsAsync<WorktreeSalvageException>(
            () => workspace.TeardownAsync("Unknown", CancellationToken.None));

        Assert.Equal(workspace.RepoPath, error.WorktreePath);
        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal("must survive", await File.ReadAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt")));
        Directory.Move(offlineOrigin, _origin);
    }

    [Fact]
    public async Task Teardown_rejects_push_to_wrong_repo_and_keeps_recovery_state()
    {
        await SeedOriginAsync();
        var wrongOrigin = Path.Combine(_root, "wrong-origin.git");
        await RunGitAsync(_root, "init", "--bare", wrongOrigin);
        var workspace = CreateProjectWorkspace(_origin);
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(workspace.RepoPath, "result.txt", "wrong destination", "valuable result");
        var localHead = (await GitAsync(workspace.RepoPath, "rev-parse", "HEAD")).StdOut;
        await GitAsync(workspace.SharedRepoPath, "config", "--replace-all", "remote.origin.pushurl", wrongOrigin);

        var error = await Assert.ThrowsAsync<WorktreeSalvageException>(
            () => workspace.TeardownAsync("Unknown", CancellationToken.None));

        Assert.Equal(workspace.RepoPath, error.WorktreePath);
        Assert.Equal("runner/runner-test/QS-30", error.Branch);
        Assert.Equal(localHead, error.LocalCommitSha);
        Assert.Contains(
            "registered project repository",
            error.InnerException?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal(localHead, (await GitAsync(wrongOrigin, "rev-parse", $"refs/heads/{error.Branch}")).StdOut);
        var expectedRepoRef = await RunGitAsync(
            _origin, "show-ref", "--verify", "--quiet", $"refs/heads/{error.Branch}");
        Assert.False(expectedRepoRef.Success);
    }

    [Fact]
    public async Task Correct_push_with_missing_sentinel_builds_verified_out_of_band_completion()
    {
        await SeedOriginAsync();
        var workspace = CreateProjectWorkspace(_origin);
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(workspace.RepoPath, "result.txt", "correct destination", "valuable result");
        var localHead = (await GitAsync(workspace.RepoPath, "rev-parse", "HEAD")).StdOut;

        var result = await workspace.TeardownAsync("Unknown", CancellationToken.None);

        Assert.Equal(
            new RemoteDeliveryProof(
                _origin,
                "refs/heads/runner/runner-test/QS-30",
                localHead),
            result.DeliveryProof);
        var request = RemoteTaskRunner.BuildVerifiedOutOfBandRequest(
            new RunOutcome(RunOutcomeKind.Unknown, "terminal sentinel missing"),
            ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
                "run-test",
                ExecutionAttemptKind.Coding,
                FinalAssistantOutput: "Committed the requested work.",
                ExitCode: 0)),
            result,
            workspace.BaseSha,
            "runner-test");
        Assert.NotNull(request);
        Assert.Contains(localHead, request!.Summary);
        Assert.Contains(
            request.Deliverables!,
            item => item.Path == $"refs/heads/runner/runner-test/QS-30@{localHead}");
    }

    [Fact]
    public async Task Durable_handoff_keeps_worktree_until_matching_ack_and_publishes_immutable_ref()
    {
        await SeedOriginAsync();
        const string runId = "run_test";
        const long fence = 17;
        var workspace = CreateWorkspace(sourceRunAttemptId: runId, fencingToken: fence);
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(workspace.RepoPath, "result.txt", "durable", "durable result");

        var secured = await workspace.SecureForHandoffAsync(
            "Done", runId, CancellationToken.None);

        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal(
            FencedGitRefs.ImmutableResult(runId, fence, secured.ResultSha!),
            secured.ImmutableResultRef);
        Assert.Equal(
            secured.ResultSha,
            (await GitAsync(_origin, "rev-parse", secured.ImmutableResultRef!)).StdOut);
        var expectedDigest = new string('a', 64);
        var wrongAck = new ResultHandoffAck(
            runId,
            5,
            new string('b', 64),
            "acknowledged",
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(30),
            false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.TeardownAfterHandoffAsync(
                secured, wrongAck, runId, expectedDigest, CancellationToken.None));
        Assert.True(Directory.Exists(workspace.RepoPath));

        await workspace.TeardownAfterHandoffAsync(
            secured,
            wrongAck with { EnvelopeDigest = expectedDigest },
            runId,
            expectedDigest,
            CancellationToken.None);

        Assert.False(Directory.Exists(workspace.RepoPath));
    }

    [Fact]
    public async Task Durable_handoff_refuses_cleanup_when_worktree_changes_after_envelope_publication()
    {
        await SeedOriginAsync();
        const string runId = "run_post_handoff_change";
        var workspace = CreateWorkspace(sourceRunAttemptId: runId, fencingToken: 18);
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(workspace.RepoPath, "result.txt", "durable", "durable result");
        var secured = await workspace.SecureForHandoffAsync(
            "Done", runId, CancellationToken.None);
        var digest = new string('a', 64);
        var acknowledgement = new ResultHandoffAck(
            runId,
            5,
            digest,
            "acknowledged",
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(30),
            false);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.RepoPath, "late-work.txt"),
            "must not be discarded");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.TeardownAfterHandoffAsync(
                secured,
                acknowledgement,
                runId,
                digest,
                CancellationToken.None));

        Assert.Contains("changed after handoff", error.Message);
        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal(
            "must not be discarded",
            await File.ReadAllTextAsync(Path.Combine(workspace.RepoPath, "late-work.txt")));
    }

    [Fact]
    public async Task Attempt_generations_publish_distinct_fenced_refs_without_moving_the_card_ref()
    {
        await SeedOriginAsync();
        var first = CreateWorkspace(
            sourceRunAttemptId: "run_generation_a",
            fencingToken: 21);
        await first.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(first.RepoPath, "generation.txt", "first", "first generation");
        var firstResult = await first.TeardownAsync(
            "Done",
            "run_generation_a",
            CancellationToken.None);

        var second = CreateWorkspace(
            sourceRunAttemptId: "run_generation_b",
            fencingToken: 22);
        var pickupBranch = await second.PrepareAsync(CancellationToken.None);
        Assert.Equal("main", pickupBranch);
        Assert.False(File.Exists(Path.Combine(second.RepoPath, "generation.txt")));
        await CommitFileAsync(second.RepoPath, "generation.txt", "second", "second generation");
        var secondResult = await second.TeardownAsync(
            "Done",
            "run_generation_b",
            CancellationToken.None);

        Assert.NotEqual(firstResult.Branch, secondResult.Branch);
        Assert.Contains("/run_generation_a/fence-21/", firstResult.Branch);
        Assert.Contains("/run_generation_b/fence-22/", secondResult.Branch);
        Assert.Equal(
            FencedGitRefs.ImmutableResult(
                "run_generation_a",
                21,
                firstResult.ResultSha!),
            firstResult.ImmutableResultRef);
        Assert.Equal(
            FencedGitRefs.ImmutableResult(
                "run_generation_b",
                22,
                secondResult.ResultSha!),
            secondResult.ImmutableResultRef);
        Assert.Equal(
            "first",
            (await GitAsync(
                _origin,
                "show",
                $"refs/heads/{firstResult.Branch}:generation.txt")).StdOut);
        Assert.Equal(
            "second",
            (await GitAsync(
                _origin,
                "show",
                $"refs/heads/{secondResult.Branch}:generation.txt")).StdOut);
        Assert.False((await RunGitAsync(
            _origin,
            "show-ref",
            "--verify",
            "--quiet",
            "refs/heads/runner/runner-test/AGT-2147")).Success);
    }

    [Fact]
    public async Task Fenced_teardown_publishes_the_salvage_fence_before_removing_the_worktree()
    {
        await SeedOriginAsync();
        const string runId = "run_delivery_failure_recovery";
        var logs = new List<string>();
        var workspace = CreateWorkspace(
            logs.Add,
            sourceRunAttemptId: runId,
            fencingToken: 24);
        await workspace.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(
            workspace.RepoPath,
            "recoverable.txt",
            "recover from this fence",
            "recoverable result");

        var result = await workspace.TeardownAsync(
            "Blocked",
            runId,
            CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Contains($"/{runId}/fence-24/", result.Branch);
        Assert.Equal(
            result.ResultSha,
            (await GitAsync(
                _origin,
                "rev-parse",
                $"refs/heads/{result.Branch}")).StdOut);
        Assert.False(Directory.Exists(workspace.RepoPath));
        var fencePublished = logs.FindIndex(line => line.Contains(
            "worktree-salvage-reconciled kind=generation-scoped",
            StringComparison.Ordinal));
        var teardownCompleted = logs.FindIndex(line => line.Contains(
            "worktree-teardown-completed",
            StringComparison.Ordinal));
        Assert.True(fencePublished >= 0 && teardownCompleted > fencePublished);
    }

    [Fact]
    public async Task Fenced_clean_teardown_still_publishes_the_salvage_fence_before_removal()
    {
        await SeedOriginAsync();
        const string runId = "run_clean_delivery_recovery";
        var workspace = CreateWorkspace(
            sourceRunAttemptId: runId,
            fencingToken: 25);
        await workspace.PrepareAsync(CancellationToken.None);

        var result = await workspace.TeardownAsync(
            "NoOp",
            runId,
            CancellationToken.None);

        Assert.False(result.SecuredWork);
        Assert.Contains($"/{runId}/fence-25/", result.Branch);
        Assert.Equal(
            result.ResultSha,
            (await GitAsync(
                _origin,
                "rev-parse",
                $"refs/heads/{result.Branch}")).StdOut);
        Assert.False(Directory.Exists(workspace.RepoPath));
    }

    [Fact]
    public async Task Fenced_out_generation_publishes_only_a_quarantine_ref()
    {
        await SeedOriginAsync();
        const string runId = "run_stale_generation";
        var workspace = CreateWorkspace(
            sourceRunAttemptId: runId,
            fencingToken: 31);
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.RepoPath, "stale.txt"),
            "preserve but never deliver");

        var result = await workspace.TeardownToQuarantineAsync(
            "LeaseLoss",
            runId,
            CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.StartsWith(
            "agent-studio/quarantine/runner-test/AGT-2147/" +
            $"{runId}/fence-31/",
            result.Branch);
        Assert.Null(result.ImmutableResultRef);
        Assert.Equal("quarantined", result.Reconciliation?.Kind);
        Assert.Equal(
            "preserve but never deliver",
            (await GitAsync(
                _origin,
                "show",
                $"refs/heads/{result.Branch}:stale.txt")).StdOut);
        Assert.Empty((await GitAsync(
            _origin,
            "for-each-ref",
            "--format=%(refname)",
            $"refs/heads/agent-studio/results/{runId}/*")).StdOut);
    }

    [Fact]
    public async Task Requeue_resumes_and_relinks_the_existing_salvage_branch()
    {
        await SeedOriginAsync();
        var first = CreateWorkspace();
        await first.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.RepoPath, "work.txt"), "first run");
        var salvaged = await first.TeardownAsync("Unknown", CancellationToken.None);

        var requeue = CreateWorkspace();
        var sourceBranch = await requeue.PrepareAsync(CancellationToken.None);
        Assert.Equal(salvaged.Branch, sourceBranch);
        Assert.Equal("refs/heads/main", requeue.IntegrationBranchRef);
        Assert.Equal("first run", await File.ReadAllTextAsync(Path.Combine(requeue.RepoPath, "work.txt")));

        var result = await requeue.TeardownAsync("Done", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Equal(salvaged.Branch, result.Branch);
        Assert.Equal(salvaged.CommitSha, result.CommitSha);
    }

    [Fact]
    public async Task Retained_local_ahead_tip_fast_forwards_canonical_salvage_ref_and_starts_pickup()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "local.txt", "local ahead", "local ahead");
        var localHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;

        var pickup = CreateWorkspace();
        var source = await pickup.PrepareAsync(CancellationToken.None);

        Assert.Equal(branch, source);
        Assert.Equal(localHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal("local ahead", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "local.txt")));
    }

    [Fact]
    public async Task Retained_tip_creates_missing_canonical_ref_and_uses_it_as_authoritative_base()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "retained.txt", "retained history", "retained side");
        var retainedHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;

        var pickup = CreateWorkspace();
        var source = await pickup.PrepareAsync(CancellationToken.None);

        Assert.Equal(branch, source);
        Assert.Equal(retainedHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal(retainedHead, (await GitAsync(pickup.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal("retained history", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "retained.txt")));
        Assert.Equal("local-ahead", pickup.PickupReconciliation?.Kind);
        Assert.Equal(retainedHead, pickup.PickupReconciliation?.AuthoritativeBaseSha);
    }

    [Fact]
    public async Task Retained_remote_ahead_tip_uses_remote_tip_as_authoritative_base_without_republishing_local()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        var localHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;
        var remoteHead = await PublishRemoteCommitAsync(branch, "remote.txt", "remote ahead", "remote ahead");

        var pickup = CreateWorkspace();
        var source = await pickup.PrepareAsync(CancellationToken.None);

        Assert.Equal(branch, source);
        Assert.NotEqual(localHead, remoteHead);
        Assert.Equal(remoteHead, (await GitAsync(pickup.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal("remote ahead", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "remote.txt")));
    }

    [Fact]
    public async Task Divergent_retained_tip_is_published_to_deterministic_collision_ref_and_pickup_is_idempotent()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "local.txt", "valuable local history", "local side");
        await File.WriteAllTextAsync(Path.Combine(retained.RepoPath, "dirty.txt"), "valuable dirty work");
        var remoteHead = await PublishRemoteCommitAsync(branch, "remote.txt", "canonical remote history", "remote side");
        var logs = new List<string>();

        var pickup = CreateWorkspace(logs.Add);
        var source = await pickup.PrepareAsync(CancellationToken.None);
        var collisionRefs = await CollisionRefsAsync(branch);

        Assert.Equal(branch, source);
        var collisionRef = Assert.Single(collisionRefs);
        var collisionHead = (await GitAsync(_origin, "rev-parse", collisionRef)).StdOut;
        Assert.Equal(remoteHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal("canonical remote history", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "remote.txt")));
        Assert.Equal("valuable local history", (await GitAsync(_origin, "show", $"{collisionRef}:local.txt")).StdOut);
        Assert.Equal("valuable dirty work", (await GitAsync(_origin, "show", $"{collisionRef}:dirty.txt")).StdOut);
        Assert.Contains(logs, line =>
            line.Contains("worktree-salvage-reconciled kind=divergent", StringComparison.Ordinal)
            && line.Contains($"canonicalSha={remoteHead}", StringComparison.Ordinal)
            && line.Contains($"recoveryRef={collisionRef}", StringComparison.Ordinal)
            && line.Contains($"recoverySha={collisionHead}", StringComparison.Ordinal));

        var repeated = CreateWorkspace();
        await repeated.PrepareAsync(CancellationToken.None);

        Assert.Equal(remoteHead, (await GitAsync(repeated.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal(collisionRefs, await CollisionRefsAsync(branch));
        Assert.Equal("valuable local history", (await GitAsync(_origin, "show", $"{collisionRef}:local.txt")).StdOut);
    }

    [Fact]
    public async Task Clean_divergent_retained_tip_preserves_both_histories_and_starts_from_canonical_tip()
    {
        await SeedOriginAsync();
        const string branch = "runner/runner-test/AGT-2147";
        await GitAsync(_origin, "branch", branch, "main");
        var retained = CreateWorkspace();
        await retained.PrepareAsync(CancellationToken.None);
        await CommitFileAsync(retained.RepoPath, "clean-local.txt", "clean local history", "clean local side");
        Assert.Equal(string.Empty, (await GitAsync(retained.RepoPath, "status", "--porcelain")).StdOut);
        var localHead = (await GitAsync(retained.RepoPath, "rev-parse", "HEAD")).StdOut;
        var remoteHead = await PublishRemoteCommitAsync(
            branch, "clean-remote.txt", "clean canonical history", "clean remote side");

        var pickup = CreateWorkspace();
        await pickup.PrepareAsync(CancellationToken.None);

        var collisionRef = Assert.Single(await CollisionRefsAsync(branch));
        Assert.Equal(localHead, (await GitAsync(_origin, "rev-parse", collisionRef)).StdOut);
        Assert.Equal(remoteHead, (await GitAsync(_origin, "rev-parse", $"refs/heads/{branch}")).StdOut);
        Assert.Equal(remoteHead, (await GitAsync(pickup.RepoPath, "rev-parse", "HEAD")).StdOut);
        Assert.Equal("clean local history", (await GitAsync(_origin, "show", $"{collisionRef}:clean-local.txt")).StdOut);
        Assert.Equal("clean canonical history", await File.ReadAllTextAsync(Path.Combine(pickup.RepoPath, "clean-remote.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GitWorkspace CreateWorkspace(
        Action<string>? log = null,
        string? sourceRunAttemptId = null,
        long? fencingToken = null)
        => new(new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = _origin,
            WorkDir = _workDir,
            StateDir = Path.Combine(_workDir, ".runner-state"),
            BaseBranch = "main",
            CliBin = "test",
            CliArgs = "",
        },
            "AGT-2147",
            log ?? (_ => { }),
            sourceRunAttemptId: sourceRunAttemptId,
            fencingToken: fencingToken);

    private GitWorkspace CreateProjectWorkspace(string? repositoryUrl, Action<string>? log = null)
        => new(new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = Path.Combine(_root, "fallback-fetch.git"),
            GitPushRemote = "git@github.com-agentstudio:agent-orc/agent-studio.git",
            WorkDir = _workDir,
            BaseBranch = "main",
            CliBin = "test",
            CliArgs = "",
        }, "QS-30", log ?? (_ => { }), "PROJ-016", repositoryUrl, "main");

    private RunnerOptions PreflightOptions() => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = _workDir,
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
    };

    private async Task CommitFileAsync(string repo, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, path), content);
        await GitAsync(repo, "add", "--all");
        await GitAsync(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", message);
    }

    private async Task<string> PublishRemoteCommitAsync(string branch, string path, string content, string message)
    {
        var publisher = Path.Combine(_root, "publisher-" + Guid.NewGuid().ToString("N"));
        await GitAsync(_root, "clone", _origin, publisher);
        await GitAsync(publisher, "checkout", "-B", "publish", $"origin/{branch}");
        await CommitFileAsync(publisher, path, content, message);
        await GitAsync(publisher, "push", "origin", $"HEAD:refs/heads/{branch}");
        return (await GitAsync(publisher, "rev-parse", "HEAD")).StdOut;
    }

    private async Task<List<string>> CollisionRefsAsync(string branch)
    {
        var refs = await GitAsync(_origin, "for-each-ref", "--format=%(refname)",
            $"refs/heads/{branch}-collision-*");
        return refs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private async Task SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await RunGitAsync(workingDirectory, args);
        Assert.True(result.Success,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
        return new ProcessResult(result.ExitCode, result.StdOut.Trim(), result.StdErr.Trim());
    }

    private static Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] args)
        => ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory);
}
