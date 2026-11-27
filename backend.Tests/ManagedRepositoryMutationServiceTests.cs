using System.Diagnostics;
using AgentStudio.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pre-existing-change guard and post-write change detection of the managed
/// repository write path against a real temporary repository (AGT-3217).
/// </summary>
public sealed class ManagedRepositoryMutationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "managed-repository-mutation-" + Guid.NewGuid().ToString("N"));

    public ManagedRepositoryMutationServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "tracked"));
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "test");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_root, "docs", "tracked", "workbench.json"), "{\n  \"id\": \"tracked\"\n}\n");
        RunGit("add", ".");
        RunGit("commit", "-q", "-m", "seed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Git can still hold a handle on Windows; the per-process temp root sweeps it.
            Trace.TraceWarning("Test repository cleanup deferred: {0}", ex.Message);
        }
    }

    [Fact]
    public void Execute_RefusesATargetInsideAnUntrackedFolder()
    {
        var folder = Path.Combine(_root, "docs", "omgai-dev");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), "<h1>Draft</h1>\n");
        var descriptor = Path.Combine(folder, "workbench.json");
        const string draft = "{\n  \"id\": \"omgai-dev\"\n}\n";
        File.WriteAllText(descriptor, draft);
        var head = RunGit("rev-parse", "HEAD");
        var wrote = false;

        var result = Service().Execute(
            "Project", _root, "test-untracked", "test: write", ["docs/omgai-dev/workbench.json"],
            () =>
            {
                wrote = true;
                File.WriteAllText(descriptor, "{}");
            });

        Assert.False(result.Success);
        Assert.False(wrote);
        Assert.Contains("Refusing to overwrite pre-existing changes: docs/omgai-dev/workbench.json", result.Error);
        Assert.Equal(draft, File.ReadAllText(descriptor));
        Assert.Equal(head, RunGit("rev-parse", "HEAD"));
        Assert.Equal("?? docs/omgai-dev/", RunGit("status", "--porcelain=v1").Trim());
    }

    [Fact]
    public void Execute_WritesAndCommitsATargetInATrackedCleanFolder()
    {
        var descriptor = Path.Combine(_root, "docs", "tracked", "workbench.json");
        var head = RunGit("rev-parse", "HEAD");

        var result = Service().Execute(
            "Project", _root, "test-tracked", "test: write tracked", ["docs/tracked/workbench.json"],
            () => File.WriteAllText(descriptor, "{\n  \"id\": \"tracked\",\n  \"key\": \"PRO-W1\"\n}\n"));

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.True(result.DidCommit);
        Assert.NotEqual(head, RunGit("rev-parse", "HEAD"));
        Assert.Equal("test: write tracked", RunGit("log", "-1", "--format=%s").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain=v1"));
    }

    [Fact]
    public void Execute_DetectsAFileTheWriteCreatesInANewFolder()
    {
        var created = Path.Combine(_root, "docs", "fresh", "workbench.json");

        var result = Service().Execute(
            "Project", _root, "test-new-folder", "test: create", ["docs/fresh/workbench.json"],
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(created)!);
                File.WriteAllText(created, "{}\n");
            });

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.True(result.DidCommit);
        Assert.Equal("docs/fresh/workbench.json",
            RunGit("show", "--name-only", "--pretty=format:", "HEAD").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain=v1"));
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    public void IndentedJsonFor_PreservesLineEndingsAndFinalNewline(string newLine, bool finalNewline)
    {
        var path = Path.Combine(_root, "docs", "tracked", "workbench.json");
        var original = string.Join(newLine, "{", "  \"id\": \"tracked\"", "}") + (finalNewline ? newLine : "");
        File.WriteAllText(path, original);
        var node = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        node["key"] = "PRO-W1";

        var written = RepositoryTextLayout.IndentedJsonFor(path, node);

        var expected = string.Join(newLine, "{", "  \"id\": \"tracked\",", "  \"key\": \"PRO-W1\"", "}")
            + (finalNewline ? newLine : "");
        Assert.Equal(expected, written);
    }

    [Fact]
    public void IndentedJsonFor_ANewFileGetsLfAndAFinalNewline()
    {
        var written = RepositoryTextLayout.IndentedJsonFor(
            Path.Combine(_root, "docs", "missing.json"),
            new System.Text.Json.Nodes.JsonObject { ["id"] = "new" });

        Assert.Equal("{\n  \"id\": \"new\"\n}\n", written);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\n", "\r\n", true)]
    [InlineData("a\nb\nc\r\n", "\n", true)]
    [InlineData("a\r\nb", "\r\n", false)]
    [InlineData("{}", "\n", false)]
    [InlineData("", "\n", true)]
    public void Detect_UsesTheDominantLineEndingAndTheFinalNewline(string text, string newLine, bool finalNewline)
    {
        Assert.Equal(new RepositoryTextLayout(newLine, finalNewline), RepositoryTextLayout.Detect(text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_DecisionReceiptWriteCarriesTheOperationTrailerAndIsGateNeutral(bool alterTitle)
    {
        // AGT-3703: a server decision receipt on the integration branch must be
        // recognizable as gate-neutral at acceptance, and only while it changes
        // nothing beyond the receipt and its lifecycle bookkeeping.
        var descriptor = Path.Combine(_root, "docs", "tracked", "workbench.json");
        var parent = RunGit("rev-parse", "HEAD").Trim();
        var title = alterTitle ? ",\n  \"title\": \"Changed\"" : "";
        var result = Service().Execute(
            "Project", _root, ManagedRepositoryMutationService.DecisionReceiptOperationPrefix + "tracked",
            "chore(workbench): request revision for tracked", ["docs/tracked/workbench.json"],
            () => File.WriteAllText(descriptor,
                "{\n  \"id\": \"tracked\"" + title + ",\n  \"status\": \"decision-pending\",\n"
                + "  \"lifecycleHistory\": [{\"state\": \"review-requested\"}],\n"
                + "  \"decision\": {\"outcome\": \"rework\", \"state\": \"pending\"}\n}\n"));

        Assert.True(result.Success, result.Error);
        var head = RunGit("rev-parse", "HEAD").Trim();
        Assert.Contains(ManagedRepositoryMutationService.DecisionReceiptCommitTrailer,
            RunGit("log", "-1", "--format=%B"));
        Assert.Equal(!alterTitle, Git().IsManagedDecisionReceiptCommit(_root, head, parent));
        Assert.False(Git().IsManagedDocumentKeyCommit(_root, head, parent));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Execute_ProjectBranchCommitReplaysAfterReconstructionWithoutPublishingLaterHead()
    {
        // Real Git boundary, run separately from the deterministic replay tests.
        var remote = Path.Combine(_root, ".git", "origin.git");
        RunGit("init", "--bare", "-q", remote);
        RunGit("remote", "add", "origin", remote);
        RunGit("checkout", "-q", "-b", "release/docs");
        var config = PushConfig();
        var firstQueue = new WorkspaceArtifactPushQueue(config);
        var result = new ManagedRepositoryMutationService(Git(), pushQueue: firstQueue).Execute(
            "Project", _root, "decision", "docs: record decision", ["docs/tracked/workbench.json"],
            () => File.WriteAllText(Path.Combine(_root, "docs", "tracked", "workbench.json"), "{\"decision\":true}\n"),
            requireCleanCheckoutAndPush: true);
        Assert.True(result.Success, result.Error);
        Assert.True(result.PushQueued);
        Assert.Equal("release/docs", Assert.Single(firstQueue.PendingRequests()).TargetBranch);

        File.WriteAllText(Path.Combine(_root, "later.txt"), "not approved for this publication\n");
        RunGit("add", "later.txt");
        RunGit("commit", "-q", "-m", "test: unrelated later commit");
        var laterHead = RunGit("rev-parse", "HEAD").Trim();
        var reconstructedQueue = new WorkspaceArtifactPushQueue(config);
        using var restarted = new WorkspaceArtifactPushWorker(reconstructedQueue,
            NullLogger<WorkspaceArtifactPushWorker>.Instance, config);
        await restarted.SweepPendingAsync(default);

        Assert.Equal(result.CommitSha, RunGit($"--git-dir={remote}", "rev-parse", "refs/heads/release/docs").Trim());
        Assert.Equal(laterHead, RunGit("rev-parse", "HEAD").Trim());
        Assert.NotEqual(laterHead, result.CommitSha);
        Assert.Empty(reconstructedQueue.PendingRequests());
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Restart_RecoversMarkedCommitWhenTheProcessLostTheShaHandoff()
    {
        var remote = Path.Combine(_root, ".git", "origin.git");
        RunGit("init", "--bare", "-q", remote);
        RunGit("remote", "add", "origin", remote);
        RunGit("checkout", "-q", "-b", "release/docs");
        var config = PushConfig();
        var queue = new WorkspaceArtifactPushQueue(config);
        var intent = Assert.IsType<WorkspaceArtifactPushIntent>(queue.Prepare(
            new WorkspaceArtifactPushRequest(_root, "decision", "release/docs", Project: "Project")));
        File.WriteAllText(Path.Combine(_root, "docs", "tracked", "workbench.json"), "{}\n");
        RunGit("add", "docs/tracked/workbench.json");
        RunGit("commit", "-q", "-m", $"docs: record decision\n\n{intent.Marker}");
        var approved = RunGit("rev-parse", "HEAD").Trim();
        RunGit("commit", "-q", "--allow-empty", "-m", "test: unrelated later commit");
        Assert.Empty(queue.PendingRequests());

        var reconstructed = new WorkspaceArtifactPushQueue(config);
        using var restarted = new WorkspaceArtifactPushWorker(reconstructed,
            NullLogger<WorkspaceArtifactPushWorker>.Instance, config);
        await restarted.SweepPendingAsync(default);

        Assert.Equal(approved, RunGit($"--git-dir={remote}", "rev-parse", "refs/heads/release/docs").Trim());
        Assert.Empty(reconstructed.PendingIntents());
        Assert.Empty(reconstructed.PendingRequests());
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Execute_OutboxFailureRefusesTheCommitAndRestoresTheBoundedWrite()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "push-outbox"), "not a directory");
        var queue = new WorkspaceArtifactPushQueue(PushConfig());
        var previousHead = RunGit("rev-parse", "HEAD").Trim();
        var result = new ManagedRepositoryMutationService(Git(), pushQueue: queue).Execute(
            "Project", _root, "decision", "docs: record decision", ["docs/tracked/workbench.json"],
            () => File.WriteAllText(Path.Combine(_root, "docs", "tracked", "workbench.json"), "{}\n"),
            requireCleanCheckoutAndPush: true);

        Assert.False(result.Success);
        Assert.False(result.DidCommit);
        Assert.Equal(previousHead, RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain=v1"));
        Assert.False(result.PushQueued);
        Assert.Contains("push obligation could not be persisted", result.Error);
        Assert.False(queue.Reader.TryRead(out _));
    }

    private IConfiguration PushConfig() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["WorkspaceArtifacts:PushOutboxPath"] = Path.Combine(_root, ".git", "push-outbox"),
        ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
    }).Build();

    private ManagedRepositoryMutationService Service() =>
        new(Git(), logger: NullLogger<ManagedRepositoryMutationService>.Instance);

    private GitService Git()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}


/// <summary>Reconstructs the queue and worker without a host process or live repository.</summary>
public sealed class ManagedRepositoryPushOutboxTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "managed-push-outbox-" + Guid.NewGuid().ToString("N"));
    private WorkspaceArtifactPushRequest Approved(string branch = "release/docs", char sha = 'a') =>
        new(Path.Combine(_state, "project"), "decision", branch, new string(sha, 40), "Project");
    private IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["WorkspaceArtifacts:PushOutboxPath"] = Path.Combine(_state, "outbox"),
        ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
        ["WorkspaceArtifacts:PushOutboxSweepSeconds"] = "60",
    }).Build();
    private WorkspaceArtifactPushQueue Queue() => new(Config());
    private WorkspaceArtifactPushWorker Worker(WorkspaceArtifactPushQueue queue, FakeOrigin origin,
        TimeProvider? clock = null, AgentStudio.State.SupervisorAdvisoryStore? advisories = null) =>
        new(queue, NullLogger<WorkspaceArtifactPushWorker>.Instance, Config(), null, advisories,
            clock ?? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(), origin.Run);

    [Fact]
    public void Restart_ReconstructsUndrainedExactProjectBranchObligation()
    {
        var approved = Approved();
        Assert.True(Queue().Enqueue(approved));

        // Discard the entire first queue, including its undrained channel.
        var restarted = Queue();
        Assert.True(Directory.Exists(Path.Combine(_state, "outbox")),
            "The approved push obligation must survive outside the first queue channel.");
        Assert.Single(Directory.GetFiles(Path.Combine(_state, "outbox"), "*.json"));
        Assert.Equal(approved, Assert.Single(restarted.PendingRequests()));
        Assert.False(restarted.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CutAfterCommitBeforeShaHandoff_RecoversOnlyTheMarkedCommit()
    {
        var approved = Approved();
        var intent = Assert.IsType<WorkspaceArtifactPushIntent>(Queue().Prepare(approved with { Sha = null }));
        Assert.StartsWith("Agent-Studio-Push-Intent: ", intent.Marker);
        var origin = new FakeOrigin { MarkedCommits = approved.Sha! };
        using var worker = Worker(Queue(), origin);
        await worker.SweepPendingAsync(default);

        Assert.Contains(origin.Commands, args => args[0] == "log" && args.Contains($"--grep={intent.Marker}"));
        Assert.Equal($"{approved.Sha}:refs/heads/{approved.TargetBranch}", Assert.Single(origin.Pushes));
        Assert.Empty(Queue().PendingIntents());
        Assert.Empty(Queue().PendingRequests());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ambiguous")]
    public async Task UnresolvedIntent_NeverAuthorizesAHeadPush(string evidence)
    {
        Queue().Prepare(Approved() with { Sha = null });
        var origin = new FakeOrigin
        {
            MarkedCommits = evidence == "" ? "" : new string('a', 40) + "\n" + new string('b', 40),
        };
        using var worker = Worker(Queue(), origin);
        await worker.SweepPendingAsync(default);

        Assert.Empty(origin.Pushes);
        Assert.Single(Queue().PendingIntents());
        Assert.Empty(Queue().PendingRequests());
    }

    [Fact]
    public void Outbox_DeduplicatesOnlyTheRepositoryBranchAndShaTuple()
    {
        var queue = Queue();
        var approved = Approved();
        Assert.True(queue.Enqueue(approved));
        Assert.True(queue.Enqueue(approved with { JobId = "another-operation" }));
        Assert.True(queue.Enqueue(Approved("other-branch")));
        Assert.True(queue.Enqueue(Approved(sha: 'b')));
        Assert.True(queue.Enqueue(approved with { RepositoryRoot = Path.Combine(_state, "other-project") }));
        Assert.Equal(4, Queue().PendingRequests().Count);
    }

    [Fact]
    public async Task Restart_ReplaysTheApprovedShaEvenWhenTrackingRefSaysAlreadyCurrent()
    {
        var approved = Approved();
        Queue().Enqueue(approved);
        var restarted = Queue();
        var origin = new FakeOrigin();
        using var worker = Worker(restarted, origin);

        await worker.SweepPendingAsync(default);

        Assert.Equal($"{approved.Sha}:refs/heads/{approved.TargetBranch}", Assert.Single(origin.Pushes));
        Assert.Empty(Queue().PendingRequests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CutAfterRemotePushBeforeAcknowledgement_ProvesContainmentOnReplay(bool remoteAdvanced)
    {
        var approved = Approved();
        var queue = Queue();
        queue.Enqueue(approved);
        using var cut = new CancellationTokenSource();
        var origin = new FakeOrigin { AfterPush = cut.Cancel };
        using (var interrupted = Worker(queue, origin))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.ProcessAsync(approved, cut.Token));
        Assert.Single(Queue().PendingRequests());
        if (remoteAdvanced)
        {
            origin.Tip = new string('b', 40);
            origin.ContainsApproved = true;
        }
        origin.AfterPush = null;
        origin.Pushes.Clear();
        origin.Commands.Clear();
        using var replay = Worker(Queue(), origin);
        await replay.SweepPendingAsync(default);

        Assert.Empty(origin.Pushes);
        Assert.Contains(origin.Commands, args => args[0] == "ls-remote");
        if (remoteAdvanced)
        {
            Assert.Contains(origin.Commands, args => args[0] == "fetch" && args.Contains("--no-write-fetch-head"));
            Assert.Contains(origin.Commands, args => args.SequenceEqual(
                new[] { "merge-base", "--is-ancestor", approved.Sha!, origin.Tip! }));
        }
        Assert.Empty(Queue().PendingRequests());
    }

    [Fact]
    public async Task UnrelatedRemoteTip_DoesNotDischargeTheReceiptOrPublishHead()
    {
        var approved = Approved();
        Queue().Enqueue(approved);
        var origin = new FakeOrigin { Tip = new string('b', 40), RejectPush = true };
        var advisories = new AgentStudio.State.SupervisorAdvisoryStore();
        using var worker = Worker(Queue(), origin, advisories: advisories);

        await worker.SweepPendingAsync(default);

        Assert.Equal(3, origin.Pushes.Count);
        Assert.All(origin.Pushes, spec => Assert.Equal($"{approved.Sha}:refs/heads/{approved.TargetBranch}", spec));
        Assert.Single(Queue().PendingRequests());
        Assert.Equal("workspace-repository-push-failed", Assert.Single(advisories.Snapshot(approved.RepositoryRoot, "Project")).Topic);
    }

    [Fact]
    public async Task PeriodicSweep_RetriesRetainedFailureWithoutNewMutation()
    {
        var approved = Approved();
        Queue().Enqueue(approved);
        var origin = new FakeOrigin { Offline = true };
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        origin.AfterPush = () =>
        {
            if (origin.Pushes.Count == 3) exhausted.TrySetResult();
            if (!origin.Offline) published.TrySetResult();
        };
        using var worker = Worker(Queue(), origin, clock);
        await worker.StartAsync(default);
        try
        {
            await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(Queue().PendingRequests());
            origin.Offline = false;
            clock.Advance(TimeSpan.FromSeconds(60));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(approved.Sha, origin.Tip);
        }
        finally
        {
            await worker.StopAsync(default);
        }
        Assert.Equal(4, origin.Pushes.Count);
    }

    [Fact]
    public async Task RepositoryGate_PreventsOverlapBetweenReplayAndDirectPush()
    {
        var approved = Approved();
        Queue().Enqueue(approved);
        var origin = new FakeOrigin();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        origin.BeforePush = async () => { entered.TrySetResult(); await release.Task; };
        using var first = Worker(Queue(), origin);
        using var second = Worker(Queue(), origin);
        var replay = first.SweepPendingAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var direct = second.ProcessAsync(approved, default);
        Assert.False(direct.IsCompleted);
        release.SetResult();
        await Task.WhenAll(replay, direct);
        Assert.Single(origin.Pushes);
        Assert.Empty(Queue().PendingRequests());
    }

    [Fact]
    public void PersistenceFailure_DoesNotSignalTheVolatileChannel()
    {
        Directory.CreateDirectory(_state);
        File.WriteAllText(Path.Combine(_state, "outbox"), "not a directory");
        var queue = Queue();
        Assert.False(queue.Enqueue(Approved()));
        Assert.False(queue.Reader.TryRead(out _));
    }

    private sealed class FakeOrigin
    {
        internal bool Offline;
        internal bool RejectPush;
        internal bool ContainsApproved;
        internal string? Tip;
        internal string MarkedCommits = "";
        internal Action? AfterPush;
        internal Func<Task>? BeforePush;
        internal List<string> Pushes { get; } = [];
        internal List<string[]> Commands { get; } = [];

        internal async Task<WorkspaceArtifactPushWorker.PushGitResult> Run(
            string _, IReadOnlyList<string> args, TimeSpan __, CancellationToken ___)
        {
            Commands.Add(args.ToArray());
            if (args[0] == "log") return Result(0, MarkedCommits);
            if (args[0] == "ls-remote")
                return Result(Offline ? 1 : 0, Tip == null ? "" : $"{Tip}\t{args[^1]}\n");
            if (args[0] == "fetch") return Result(0);
            if (args[0] == "merge-base") return Result(ContainsApproved ? 0 : 1);
            if (args[0] == "rev-list") return Result(0, "0"); // Deliberately stale local tracking evidence.
            Assert.Equal("push", args[0]);
            if (BeforePush != null) await BeforePush();
            Pushes.Add(args[^1]);
            var failed = Offline || RejectPush;
            if (!failed) Tip = args[^1].Split(':')[0];
            AfterPush?.Invoke();
            return Result(failed ? 1 : 0);
        }

        private static WorkspaceArtifactPushWorker.PushGitResult Result(int code, string output = "") =>
            new(code, output, code == 0 ? "" : "offline or non-fast-forward", GitProcessFailureKind.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, true);
    }
}
