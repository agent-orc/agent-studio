// clock-independent: runtime log timestamps are parsed data; only their differences are asserted.
using AgentRunner.WorkContainers;
using Xunit;

namespace AgentRunner.Tests.WorkContainers;

public class WorkContainerExecutorTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"wc-exec-{Guid.NewGuid():N}");
    private static readonly HostMemory Memory = new(56L << 30, 62L << 30);

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, true);
    }

    private WorkContainerSettings Settings => WorkContainerPolicyTests.Settings() with { StateDirectory = _state };

    [Fact]
    public async Task Over_budget_launch_fails_before_any_runtime_call()
    {
        var runtime = new FakeRuntime();
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);

        var receipt = await executor.RunAsync(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 21), CancellationToken.None);

        Assert.Equal("denied", receipt.Outcome);
        Assert.StartsWith("profile-ceiling", receipt.Reason);
        Assert.Empty(runtime.Calls);
        Assert.True(File.Exists(Path.Combine(_state, "receipts", "attempt-42-f7.json")));
    }

    [Fact]
    public async Task Live_reservations_deny_a_launch_without_creating_anything()
    {
        var runtime = new FakeRuntime();
        runtime.Containers.Add(new ManagedWorkContainer(
            new ManagedWorkResource("container", "agent-work-other-f1", "other", "lease-other", 1, true), 18, "running"));
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);

        var receipt = await executor.RunAsync(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 3), CancellationToken.None);

        Assert.Equal("denied", receipt.Outcome);
        Assert.StartsWith("host-grant-exhausted", receipt.Reason);
        Assert.DoesNotContain(runtime.Calls, call => call.StartsWith("create", StringComparison.Ordinal) || call.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_attempt_flushes_its_log_before_removal_and_verifies_cleanup()
    {
        var runtime = new FakeRuntime { ImagePresent = false, ExitCode = 0, Log = string.Join('\n',
            "2026-10-28T10:00:00.100000000Z AGENT_PHASE materialize",
            "2026-10-28T10:00:00.200000000Z AGENT_CACHE repo miss",
            "2026-10-28T10:00:01.000000000Z AGENT_SHA_VERIFIED " + WorkContainerPolicyTests.Sha,
            "2026-10-28T10:00:01.050000000Z AGENT_CACHE npm hit",
            "2026-10-28T10:00:01.100123456Z AGENT_PHASE first-command",
            "2026-10-28T10:00:03.100123456Z AGENT_PHASE exit 0") };
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);

        var receipt = await executor.RunAsync(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(4, 4, 3), CancellationToken.None);

        Assert.Equal("succeeded", receipt.Outcome);
        Assert.True(receipt.ShaVerified);
        Assert.True(receipt.CleanupVerified);
        Assert.Equal("miss", receipt.Cache["repo"]);
        Assert.Equal("hit", receipt.Cache["npm"]);
        Assert.Equal(1000.1, receipt.Timings.MaterializeMs!.Value, 1);
        Assert.Equal(2000, receipt.Timings.CommandMs!.Value, 1);
        Assert.NotNull(receipt.Timings.ImagePullMs);
        Assert.Contains("AGENT_PHASE exit 0", File.ReadAllText(receipt.LogPath!));
        var order = runtime.Calls.Select(call => call.Split(' ')[0]).ToList();
        Assert.True(order.IndexOf("pull") < order.IndexOf("create-volume"));
        Assert.True(order.IndexOf("logs") < order.IndexOf("rm"));
        Assert.Empty(runtime.Containers);
        Assert.Empty(runtime.Volumes);
    }

    [Fact]
    public async Task Brokered_token_uses_tmpfs_injection_and_is_redacted_before_log_persistence()
    {
        var token = "opaque-" + Guid.NewGuid().ToString("N");
        var runtime = new FakeRuntime { Log = "2026-10-28T10:00:00Z accidental=" + token };
        var credentials = new FakeCredentials(token);
        var attempt = WorkContainerPolicyTests.Attempt() with
            { RepositorySource = "https://github.com/example/one.git" };
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory, credentials: credentials);
        var receipt = await executor.RunAsync(attempt, WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 2), CancellationToken.None);

        Assert.Equal("succeeded", receipt.Outcome);
        Assert.Equal("revoked", receipt.CredentialRevocationStatus);
        Assert.Equal("container-tmpfs-removed", receipt.SecretDeletionStatus);
        Assert.Equal(token, runtime.InjectedToken);
        Assert.DoesNotContain(token, File.ReadAllText(receipt.LogPath!), StringComparison.Ordinal);
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_state, "receipts", "attempt-42-f7.json")), StringComparison.Ordinal);
        Assert.Contains("omitted-credential-boundary", File.ReadAllText(receipt.LogPath!));
        Assert.DoesNotContain(runtime.Calls, call => call.Contains(token, StringComparison.Ordinal));
        Assert.True(credentials.Revoked);
    }

    [Fact]
    public async Task Sha_mismatch_is_reported_and_still_cleaned_up()
    {
        var runtime = new FakeRuntime { ExitCode = WorkContainerLaunchPlanner.ShaMismatchExitCode };
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);

        var receipt = await executor.RunAsync(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 2), CancellationToken.None);

        Assert.Equal("sha-mismatch", receipt.Outcome);
        Assert.True(receipt.CleanupVerified);
    }

    [Fact]
    public async Task Lease_loss_removes_the_running_container_after_flushing_its_log()
    {
        var runtime = new FakeRuntime { BlockWait = true };
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);
        using var lease = new CancellationTokenSource();

        var running = executor.RunAsync(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 2), lease.Token);
        await runtime.Started.Task;
        lease.Cancel();
        var receipt = await running;

        Assert.Equal("stopped", receipt.Outcome);
        Assert.True(receipt.CleanupVerified);
        Assert.Empty(runtime.Containers);
    }

    [Fact]
    public async Task Residue_after_removal_is_reported_as_unverified_cleanup()
    {
        var runtime = new FakeRuntime { RemovalFails = true };
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);

        var receipt = await executor.RunAsync(WorkContainerPolicyTests.Attempt(), WorkContainerPolicyTests.Digest,
            new WorkContainerGrant(1, 1, 2), CancellationToken.None);

        Assert.False(receipt.CleanupVerified);
        Assert.Contains("container:agent-work-attempt-42-f7", receipt.CleanupResidue);
    }

    [Fact]
    public async Task Restart_reconciliation_never_starts_and_removes_everything_but_the_live_fence()
    {
        var runtime = new FakeRuntime();
        void Add(string attempt, long fence, bool running)
        {
            var name = $"agent-work-{attempt}-f{fence}";
            runtime.Containers.Add(new(new("container", name, attempt, $"lease-{attempt}", fence, running), 2, running ? "running" : "exited"));
            runtime.Volumes.Add(new("volume", name, attempt, $"lease-{attempt}", fence, false));
        }
        Add("fenced", 3, true);
        Add("exited", 1, false);
        Add("live", 1, true);
        Add("unknown", 1, true);
        var executor = new WorkContainerExecutor(runtime, Settings, () => Memory);

        var actions = await executor.ReconcileAsync("agent-runner-01",
        [
            new WorkAttemptAuthority("fenced", "lease-fenced", 4, true),
            new WorkAttemptAuthority("exited", "lease-exited", 1, true),
            new WorkAttemptAuthority("live", "lease-live", 1, true),
        ], CancellationToken.None);

        Assert.DoesNotContain(runtime.Calls, call => call.StartsWith("start", StringComparison.Ordinal));
        Assert.Equal(["agent-work-live-f1"], runtime.Containers.Select(item => item.Resource.Name));
        Assert.Equal(["agent-work-live-f1"], runtime.Volumes.Select(item => item.Name));
        Assert.Equal(3, actions.Count(action => action is { Kind: "container", Action: "FlushAndRemove" }));
        Assert.True(File.Exists(Path.Combine(_state, "logs", "orphan-agent-work-fenced-f3.log")));
    }

    [Fact]
    public void Log_markers_and_runtime_inventory_parse_for_docker_and_podman()
    {
        var markers = WorkContainerExecutor.ParseMarkers("2026-10-28T10:00:00.123456789Z AGENT_PHASE first-command\nnoise");
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567), markers.FirstCommand);

        var docker = CliWorkContainerRuntime.ParseInventory(
            """{"Names":"agent-work-a-f2","State":"running","Labels":"agent-studio.attempt=a,agent-studio.fence=2,agent-studio.lease=l,agent-studio.grant-half-cores=3"}""",
            """{"Name":"agent-work-a-f2","Labels":"agent-studio.attempt=a,agent-studio.fence=2"}""");
        var podman = CliWorkContainerRuntime.ParseInventory(
            """{"Names":["agent-work-a-f2"],"State":"created","Labels":{"agent-studio.attempt":"a","agent-studio.fence":"2","agent-studio.grant-half-cores":"3"}}""",
            "");

        Assert.Equal(3, docker.ReservedHalfCores);
        Assert.Equal("a", docker.Volumes.Single().AttemptId);
        Assert.True(docker.Containers.Single().Resource.Running);
        Assert.Equal(3, podman.ReservedHalfCores);
        Assert.Equal(2, podman.Containers.Single().Resource.Fence);
    }

    internal sealed class FakeRuntime : IWorkContainerRuntime
    {
        public List<string> Calls { get; } = [];
        public List<ManagedWorkContainer> Containers { get; } = [];
        public List<ManagedWorkResource> Volumes { get; } = [];
        public bool ImagePresent { get; init; } = true;
        public int? ExitCode { get; init; } = 0;
        public string Log { get; init; } = "2026-10-28T10:00:00Z AGENT_PHASE exit 0";
        public bool BlockWait { get; init; }
        public bool RemovalFails { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? InjectedToken { get; private set; }
        private readonly Lock _gate = new();

        private void Call(string text) { lock (_gate) Calls.Add(text); }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ImagePresentAsync(string image, CancellationToken ct) { Call($"image-inspect {image}"); return Task.FromResult(ImagePresent); }
        public Task<WorkRuntimeResult> PullAsync(string image, CancellationToken ct) { Call($"pull {image}"); return Task.FromResult(WorkRuntimeResult.Success); }

        public Task<WorkRuntimeResult> CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Call($"create-volume {name}");
            Volumes.Add(new("volume", name, labels[WorkContainerLaunchPlanner.AttemptLabel], labels[WorkContainerLaunchPlanner.LeaseLabel],
                long.Parse(labels[WorkContainerLaunchPlanner.FenceLabel]), false));
            return Task.FromResult(WorkRuntimeResult.Success);
        }

        public Task<WorkRuntimeResult> CreateContainerAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Call("create-container");
            string Label(string name) => args.First(value => value.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];
            Containers.Add(new(new("container", args[args.ToList().IndexOf("--name") + 1], Label(WorkContainerLaunchPlanner.AttemptLabel),
                Label(WorkContainerLaunchPlanner.LeaseLabel), long.Parse(Label(WorkContainerLaunchPlanner.FenceLabel)), false),
                int.Parse(Label(WorkContainerLaunchPlanner.GrantLabel)), "created"));
            return Task.FromResult(WorkRuntimeResult.Success);
        }

        public Task<WorkRuntimeResult> StartAsync(string container, CancellationToken ct)
        {
            Call($"start {container}");
            Started.TrySetResult();
            return Task.FromResult(WorkRuntimeResult.Success);
        }

        public Task<WorkRuntimeResult> InjectGitTokenAsync(string container, string token, CancellationToken ct)
        {
            InjectedToken = token;
            Call("inject-secret");
            return Task.FromResult(WorkRuntimeResult.Success);
        }

        public async Task<int?> WaitAsync(string container, CancellationToken ct)
        {
            Call($"wait {container}");
            if (BlockWait) await Task.Delay(Timeout.Infinite, ct);
            return ExitCode;
        }

        public Task<WorkRuntimeResult> LogsAsync(string container, CancellationToken ct) { Call($"logs {container}"); return Task.FromResult(new WorkRuntimeResult(true, Log)); }

        public Task<WorkRuntimeResult> RemoveContainerAsync(string container, CancellationToken ct)
        {
            Call($"rm {container}");
            if (!RemovalFails) Containers.RemoveAll(item => item.Resource.Name == container);
            return Task.FromResult(WorkRuntimeResult.Success);
        }

        public Task<WorkRuntimeResult> RemoveVolumeAsync(string volume, CancellationToken ct)
        {
            Call($"volume-rm {volume}");
            if (!RemovalFails) Volumes.RemoveAll(item => item.Name == volume);
            return Task.FromResult(WorkRuntimeResult.Success);
        }

        public Task<ManagedWorkInventory> ListManagedAsync(string hostId, string? attemptId, CancellationToken ct)
            => Task.FromResult(new ManagedWorkInventory(
                Containers.Where(item => attemptId is null || item.Resource.AttemptId == attemptId).ToArray(),
                Volumes.Where(item => attemptId is null || item.AttemptId == attemptId).ToArray()));
    }

    private sealed class FakeCredentials(string token) : IWorkContainerCredentialSource
    {
        public bool Revoked { get; private set; }
        public Task<WorkCredential> AcquireAsync(WorkContainerAttempt attempt, CancellationToken ct)
            => Task.FromResult(new WorkCredential("issuance", token, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
        public Task<WorkCredential?> RenewAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
            => Task.FromResult<WorkCredential?>(null);
        public Task<string> RevokeAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
        {
            Revoked = true;
            return Task.FromResult("revoked");
        }
    }
}
