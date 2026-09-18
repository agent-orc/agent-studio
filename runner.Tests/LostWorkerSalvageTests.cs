using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2870: on 18.09.2026 a detached coding worker aborted after 88 minutes.
/// The daemon released the attempt to Ready and left the worktree on disk, so
/// the next claim could only push the nearly finished delivery to a
/// <c>quarantine/.../unknown-generation/fence-unknown</c> ref and start over from
/// the integration branch. The daemon that detects the loss holds the slot, and
/// therefore the attempt id and the fence: it salvages under that generation's
/// own ref, before it releases the lease.
/// </summary>
public sealed class LostWorkerSalvageTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"lost-worker-salvage-{Guid.NewGuid():N}");

    // ---- pure policy --------------------------------------------------------

    [Theory]
    [InlineData(true, false, true, LostWorkerRecoveryAction.Salvage)]
    [InlineData(true, false, false, LostWorkerRecoveryAction.Quarantine)]
    [InlineData(false, false, true, LostWorkerRecoveryAction.None)]
    [InlineData(true, true, true, LostWorkerRecoveryAction.None)]
    public void Policy_salvages_whenever_the_lost_attempt_can_be_attributed(
        bool worktreeExists,
        bool readOnlyCheckout,
        bool hasFencedGeneration,
        LostWorkerRecoveryAction expected)
    {
        Assert.Equal(
            expected,
            LostWorkerRecoveryPolicy.Decide(worktreeExists, readOnlyCheckout, hasFencedGeneration));
    }

    // ---- crash evidence -----------------------------------------------------

    [Fact]
    public void Crash_evidence_keeps_the_last_diagnostic_lines_and_drops_agent_output()
    {
        var workerDirectory = Path.Combine(_root, "worker");
        Directory.CreateDirectory(workerDirectory);
        var lines = new List<string>();
        for (var i = 1; i <= 12; i++)
            lines.Add(Log(i, "stdout", $"{{\"type\":\"assistant\",\"turn\":{i}}}"));
        lines.Add(Log(13, "stderr", "terminate called after throwing an instance of 'PAL_SEHException'"));
        lines.Add(Log(14, "system", "[runner] worker cgroup attach failed"));
        File.WriteAllLines(Path.Combine(workerDirectory, "output.jsonl"), lines);

        var evidence = WorkerCrashEvidenceReader.Read(workerDirectory, maxLines: 3);

        Assert.Equal(2, evidence.Lines.Count);
        Assert.Contains("PAL_SEHException", evidence.Lines[0]);
        Assert.Equal("[runner] worker cgroup attach failed", evidence.CrashLine);
        Assert.DoesNotContain(evidence.Lines, line => line.Contains("assistant", StringComparison.Ordinal));

        var described = evidence.Describe("attempt-1", "process verification failed");
        Assert.Contains(described, line => line.Contains("worker-lost attempt=attempt-1", StringComparison.Ordinal));
        Assert.Contains(described, line => line.Contains("PAL_SEHException", StringComparison.Ordinal));
    }

    [Fact]
    public void Crash_evidence_of_a_worker_that_logged_nothing_is_empty_not_an_exception()
    {
        var evidence = WorkerCrashEvidenceReader.Read(Path.Combine(_root, "absent"));

        Assert.Empty(evidence.Lines);
        Assert.Null(evidence.CrashLine);
        Assert.Null(evidence.Pressure);
    }

    [Fact]
    public void Cgroup_counters_name_the_task_ceiling_the_worker_died_at()
    {
        var cgroup = Path.Combine(_root, "worker-cgroup");
        Directory.CreateDirectory(cgroup);
        File.WriteAllText(Path.Combine(cgroup, "pids.max"), "307\n");
        File.WriteAllText(Path.Combine(cgroup, "pids.peak"), "307\n");
        File.WriteAllText(Path.Combine(cgroup, "pids.events"), "max 125\n");
        File.WriteAllText(Path.Combine(cgroup, "memory.events"), "low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\n");

        var pressure = WorkerCgroup.ReadPressure(cgroup);

        Assert.NotNull(pressure);
        Assert.True(pressure!.HitTaskCeiling);
        Assert.Equal(307, pressure.TasksMax);
        Assert.Equal(125, pressure.ForksRefused);
        Assert.Contains("pidsEventsMax=125", pressure.Describe());
        Assert.Contains("memoryEventsOomKill=0", pressure.Describe());
    }

    [Fact]
    public void Cgroup_counters_a_kernel_does_not_publish_stay_unknown()
    {
        var cgroup = Path.Combine(_root, "worker-cgroup-bare");
        Directory.CreateDirectory(cgroup);
        File.WriteAllText(Path.Combine(cgroup, "pids.max"), "max\n");

        var pressure = WorkerCgroup.ReadPressure(cgroup);

        Assert.NotNull(pressure);
        Assert.False(pressure!.HitTaskCeiling);
        Assert.Contains("pidsMax=unknown", pressure.Describe());
        Assert.Contains("pidsEventsMax=unknown", pressure.Describe());
    }

    // ---- salvage before release --------------------------------------------

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task A_lost_worker_is_salvaged_under_its_own_generation_before_the_lease_is_released()
    {
        PlatformGate.RequiresPosixShell();
        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var options = Options(origin);
        var lease = new RunLeaseInfoDto(
            "AGT-LOST-SALVAGE",
            options.RunnerId,
            options.RunnerName,
            options.Hostname,
            Environment.ProcessId,
            options.BackendName,
            "lease-lost-salvage",
            2,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            "attempt-lost-salvage",
            1);
        var server = new SalvageObservingServer(lease, origin);
        using var http = new HttpClient(server)
        {
            BaseAddress = new Uri("http://task-server"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new TaskServerClient(http, options.RunnerId, options: options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var logs = new ConcurrentQueue<string>();
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var daemonTask = daemon.RunAsync(stop.Token);
        Process? worker = null;

        try
        {
            var slot = await WaitForDirtyWorktreeAsync(options.StateDir, stop.Token);
            worker = Process.GetProcessById(slot.ProcessId!.Value);
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(stop.Token);

            var release = await server.Released.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // The release names the loss and the exact generation-scoped ref the
            // work was published under - not a quarantine ref.
            Assert.Equal("worker-lost", release.Outcome);
            Assert.NotNull(release.SalvageBranch);
            Assert.StartsWith(
                $"agent-studio/salvage/{options.RunnerId}/{lease.TaskKey}/attempt-lost-salvage/fence-2/",
                release.SalvageBranch,
                StringComparison.Ordinal);
            Assert.DoesNotContain("quarantine", release.SalvageBranch, StringComparison.Ordinal);
            Assert.DoesNotContain("unknown-generation", release.SalvageBranch, StringComparison.Ordinal);
            Assert.Equal(40, release.SalvageCommitSha!.Length);

            // Ordering is the point: the ref already existed on origin when the
            // release arrived, so a claim racing the release can no longer find
            // an unattributable worktree.
            Assert.True(
                release.SalvageRefOnOriginAtRelease,
                "the salvage ref was not on origin when the release was received");
            Assert.Equal(
                release.SalvageCommitSha,
                await ResolveAsync(origin, release.SalvageBranch!));

            // The salvaged commit carries the work the killed worker had already
            // written into its worktree.
            var salvaged = await GitOutputAsync(
                origin, "show", $"{release.SalvageCommitSha}:salvaged.txt");
            Assert.Contains("work in progress", salvaged, StringComparison.Ordinal);

            Assert.Contains(logs, line => line.Contains("worker-lost-salvaged", StringComparison.Ordinal));
        }
        finally
        {
            await stop.CancelAsync();
            try { await daemonTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            if (worker is { HasExited: false })
                worker.Kill(entireProcessTree: true);
        }
    }

    private RunnerOptions Options(string origin) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-lost-salvage",
        RunnerName = "Runner lost salvage",
        Hostname = "fixture-host",
        BackendName = "fake-task-server",
        GitRemote = origin,
        GitPushRemote = origin,
        WorkDir = Path.Combine(_root, "work"),
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        ExecEngine = RunnerOptions.ExecEngineLegacy,
        CliBin = PosixShell.RequirePath(),
        // Writes the "nearly finished delivery" into the worktree, then waits to
        // be killed like the observed worker was.
        CliArgs = "-c \"printf 'work in progress\\n' > salvaged.txt; sleep 120\"",
        TtlSeconds = 300,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 300,
        HostMaxParallelism = 1,
        PollSeconds = 1,
        ServerRequestTimeoutSeconds = 5,
        IdleWatchdogMinutes = 2,
    };

    private static async Task<PersistedRunnerSlot> WaitForDirtyWorktreeAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var slot = new RunnerStateStore(stateDirectory).LoadAll().SingleOrDefault();
            if (slot?.ProcessId is > 0
                && File.Exists(Path.Combine(slot.WorktreePath, "salvaged.txt")))
                return slot;
            await Task.Delay(50, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task CreateOriginAsync(string origin, string seed)
    {
        Directory.CreateDirectory(_root);
        await GitAsync(_root, "init", "--bare", origin);
        await GitAsync(_root, "init", seed);
        await GitAsync(seed, "config", "user.name", "Runner Test");
        await GitAsync(seed, "config", "user.email", "runner@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
        await GitAsync(seed, "add", "README.md");
        await GitAsync(seed, "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        await GitAsync(origin, "symbolic-ref", "HEAD", "refs/heads/main");
    }

    private static string Log(long sequence, string stream, string text)
        => JsonSerializer.Serialize(
            new { sequence, timestamp = DateTime.UtcNow, stream, text },
            Json);

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            ct: CancellationToken.None);
        Assert.True(
            result.Success,
            $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
    }

    private static async Task<string> GitOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            ct: CancellationToken.None);
        Assert.True(
            result.Success,
            $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
        return result.StdOut;
    }

    private static async Task<string> ResolveAsync(string origin, string branch)
        => (await GitOutputAsync(origin, "rev-parse", branch)).Trim();

    public void Dispose()
        => ResilientDirectory.TryDelete(_root);

    private sealed record ObservedRelease(
        string? Outcome,
        string? SalvageBranch,
        string? SalvageCommitSha,
        string? Detail,
        bool SalvageRefOnOriginAtRelease);

    /// <summary>
    /// Fake Task Server that claims exactly one card and records what the
    /// release carried, including whether the named salvage ref was already on
    /// origin at the moment the release arrived.
    /// </summary>
    private sealed class SalvageObservingServer(RunLeaseInfoDto lease, string origin) : HttpMessageHandler
    {
        private int _claimCount;

        public TaskCompletionSource<ObservedRelease> Released { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == $"/api/tasks/{lease.TaskKey}/files/prompt.md")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Write the delivery and wait."),
                };
            }

            object? response;
            switch (path)
            {
                case "/api/clients/register":
                    response = new ClientRegisterResponse("fixture-client", "Runner lost salvage", "service");
                    break;
                case "/api/clients/fixture-client/runner-git-capability":
                    response = new { };
                    break;
                case "/api/runner/project-chat/claim":
                    response = new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty);
                    break;
                case "/api/runner/claim":
                    response = Interlocked.Increment(ref _claimCount) == 1
                        ? new RunnerClaimResponse(
                            RunnerClaimStatus.Claimed,
                            lease.TaskKey,
                            lease.TaskKey,
                            "Fixture project",
                            lease)
                        : new RunnerClaimResponse(RunnerClaimStatus.Empty);
                    break;
                case "/api/runner/lease/renew":
                    response = new RunLeaseResponse("Renewed", true, lease);
                    break;
                case "/api/runner/logs":
                    response = new LogIngestResponse(lease.TaskKey, 0);
                    break;
                case "/api/runner/lease/release":
                    response = await ObserveReleaseAsync(request, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected fake Task Server request: {request.Method} {path}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, Json)),
            };
        }

        private async Task<RunLeaseResponse> ObserveReleaseAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<RunLeaseReleaseRequest>(body, Json)!;
            var published = !string.IsNullOrWhiteSpace(release.SalvageBranch)
                            && (await ProcessRunner.RunAsync(
                                "git",
                                ["rev-parse", "--verify", release.SalvageBranch!],
                                origin,
                                ct: CancellationToken.None)).Success;
            Released.TrySetResult(new ObservedRelease(
                release.Outcome,
                release.SalvageBranch,
                release.SalvageCommitSha,
                release.Detail,
                published));
            return new RunLeaseResponse("Released", false, lease);
        }
    }
}
