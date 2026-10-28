using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>AGT-3171 DP-S8: the host agent's post adapter.</summary>
public sealed class PostWorkDaemonTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-work-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Linux_defaults_to_a_digest_pinned_container_and_Windows_to_the_process_executor()
    {
        var linux = PostWorkHostOptions.From(Env(("RUNNER_POST_WORK_IMAGE", "registry/post@sha256:abc")), windows: false);
        Assert.Equal(PostWorkExecutorKinds.Container, linux.Executor);
        Assert.Equal("none", linux.Network);
        Assert.Throws<ArgumentException>(() =>
            PostWorkHostOptions.From(Env(("RUNNER_POST_WORK_IMAGE", "registry/post:latest")), windows: false));
        Assert.Equal(PostWorkExecutorKinds.Process, PostWorkHostOptions.From(Env(), windows: true).Executor);
        Assert.Throws<ArgumentException>(() =>
            PostWorkHostOptions.From(Env(("RUNNER_POST_WORK_EXECUTOR", "container")), windows: true));
        Assert.Equal(PostWorkExecutorKinds.Process,
            PostWorkHostOptions.From(Env(("RUNNER_POST_WORK_EXECUTOR", "process")), windows: false).Executor);
    }

    [Fact]
    public void Container_command_is_fresh_bounded_by_the_grant_and_never_mounts_the_host_socket()
    {
        var argv = PostWorkContainerCommand.Run(Lease(), new PostWorkStep("lint", "npm", ["run", "lint"], 60),
            "registry/post@sha256:abc", "none", "/state/post-work/pwa_1-3");

        Assert.Equal("run", argv[0]);
        Assert.Contains("--rm", argv);
        Assert.Contains("--read-only", argv);
        Assert.Equal("1.0", argv[argv.ToList().IndexOf("--cpus") + 1]);
        Assert.Equal("2g", argv[argv.ToList().IndexOf("--memory") + 1]);
        Assert.Equal("none", argv[argv.ToList().IndexOf("--network") + 1]);
        Assert.Contains("agent-studio.post-work.attempt=pwa_1", argv);
        Assert.DoesNotContain(argv, item => item.Contains("docker.sock", StringComparison.Ordinal));
        Assert.Equal(["registry/post@sha256:abc", "npm", "run", "lint"], argv.TakeLast(4));
    }

    [Theory]
    [InlineData(new[] { "passed", "passed" }, 2, "decided", "steps-passed")]
    [InlineData(new[] { "passed", "failed" }, 3, "decided", "step-failed:s1")]
    [InlineData(new[] { "timed-out" }, 2, "deferred", "step-timed-out:s0")]
    [InlineData(new[] { "executor-unavailable" }, 1, "blocked", "executor-unavailable:s0")]
    [InlineData(new string[0], 0, "decided", "no-steps")]
    [InlineData(new[] { "passed" }, 2, "deferred", "steps-incomplete")]
    public void Proposal_reports_evidence_and_leaves_the_decision_to_the_server(
        string[] statuses, int planned, string outcome, string reason)
    {
        var evidence = statuses.Select((status, index) =>
            new PostWorkStepEvidence("s" + index, status, 0, 10, null, null)).ToArray();
        var report = PostWorkProposalPolicy.Propose(evidence, planned, PostWorkExecutorKinds.Container, true, Now);
        Assert.Equal(outcome, report.ProposedOutcome);
        Assert.Equal(reason, report.Reason);
        Assert.Equal(outcome == "deferred", report.RetryAt is not null);
    }

    [Fact]
    public async Task Completed_attempt_reports_evidence_and_clears_its_persisted_claim()
    {
        var authority = new FakeAuthority();
        var executor = new FakeExecutor();
        var daemon = Daemon(authority, executor);

        var report = await daemon.ExecuteAsync(Subject(2), Lease(), CancellationToken.None);

        Assert.Equal("steps-passed", report!.Reason);
        Assert.Equal(2, authority.Reports.Single().Report.Evidence.Count);
        Assert.Equal(3, authority.Reports.Single().Authority.Fence);
        Assert.Null(PostWorkDaemon.ReadPersistedClaim(_root));
        Assert.Equal(1, executor.Cleanups);
    }

    [Fact]
    public async Task Lease_loss_stops_the_steps_and_suppresses_the_late_report()
    {
        var authority = new FakeAuthority { RefuseRenewal = true };
        var executor = new FakeExecutor { StepDuration = TimeSpan.FromSeconds(30) };
        var daemon = Daemon(authority, executor);
        daemon.RenewIntervalOverride = TimeSpan.FromMilliseconds(50);

        var report = await daemon.ExecuteAsync(Subject(2), Lease(), CancellationToken.None);

        Assert.Null(report);
        Assert.Empty(authority.Reports);
        Assert.Equal(1, executor.Cleanups);
        Assert.Null(PostWorkDaemon.ReadPersistedClaim(_root));
    }

    [Fact]
    public async Task Refused_report_after_a_newer_fence_is_inert()
    {
        var authority = new FakeAuthority { RefuseReport = "stale-fence" };
        var report = await Daemon(authority, new FakeExecutor()).ExecuteAsync(Subject(1), Lease(), CancellationToken.None);
        Assert.Null(report);
    }

    private PostWorkDaemon Daemon(FakeAuthority authority, FakeExecutor executor)
        => new(authority, executor, "post-runner", () => "instance", _root, 60, TimeSpan.FromMilliseconds(10), _ => { });

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
        => key => values.FirstOrDefault(item => item.Key == key).Value;

    private static PostWorkLease Lease()
        => new("pwa_1", "pws_1", "pwl_1", "host", "post-runner", "instance", "host", 3, 1, Now, Now.AddMinutes(2), 1, 2);

    private static PostWorkSubject Subject(int steps)
        => new("pws_1", "pw_key", "project", "AGT-1", "lane-1", "run-1", [], [],
            Enumerable.Range(0, steps).Select(index => new PostWorkStep("s" + index, "true", [], 60)).ToArray(),
            "sha256:x", "host", "running", "host", "cohort", 3, 1, null, null, 0, null, null, null, null, null,
            "eligible", [], null, Now, Now);

    private sealed class FakeAuthority : IPostWorkAuthorityPort
    {
        public bool RefuseRenewal { get; init; }
        public string? RefuseReport { get; init; }
        public List<SubmitPostWorkReportRequest> Reports { get; } = [];

        public Task<long> AllocationVersionAsync(CancellationToken ct) => Task.FromResult(1L);

        public Task<PostWorkClaimResponse> ClaimAsync(PostWorkClaimRequest request, CancellationToken ct)
            => Task.FromResult(new PostWorkClaimResponse("empty"));

        public Task<PostWorkLease> RenewAsync(string attemptId, PostWorkRenewRequest request, CancellationToken ct)
            => RefuseRenewal
                ? throw new TaskServerException(409, "lost", "post-work-lease-lost")
                : Task.FromResult(Lease());

        public Task<PostWorkSubject> ReportAsync(string attemptId, SubmitPostWorkReportRequest request, CancellationToken ct)
        {
            if (RefuseReport is not null) throw new TaskServerException(409, "refused", RefuseReport);
            Reports.Add(request);
            return Task.FromResult(Subject(0));
        }
    }

    private sealed class FakeExecutor : IPostWorkStepExecutor
    {
        public TimeSpan StepDuration { get; init; } = TimeSpan.Zero;
        public int Cleanups { get; private set; }
        public string Kind => PostWorkExecutorKinds.Container;

        public async Task<PostWorkStepEvidence> RunAsync(
            PostWorkLease lease, PostWorkStep step, string workDirectory, CancellationToken ct)
        {
            Assert.True(File.Exists(Path.Combine(workDirectory, "subject.json")));
            Assert.NotNull(PostWorkDaemon.ReadPersistedClaim(Path.GetDirectoryName(Path.GetDirectoryName(workDirectory))!));
            await Task.Delay(StepDuration, ct);
            return new PostWorkStepEvidence(step.StepId, "passed", 0, 1, null, null);
        }

        public Task<bool> CleanupAsync(PostWorkLease lease, string workDirectory, CancellationToken ct)
        {
            Cleanups++;
            return Task.FromResult(true);
        }
    }
}
