using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2863: a detached review worker deliberately survives a review-daemon
/// restart, so a replacement daemon can report a verdict that an older
/// agent-host build produced. These tests pin the three consequences: the
/// adoption names both releases, a mismatch produces exactly one operator
/// notice, and the opt-in release drain holds claims until the superseded
/// attempt is done - without ever ending the adopted worker.
/// </summary>
public sealed class ReviewWorkerReleaseProvenanceTests : IDisposable
{
    private const string SupersededRelease = "20260917T1010Z-v0.5.0-tmpguard-fcdb68b24";
    private const string SupersededBinary =
        "/opt/agent-host/releases/20260917T1010Z-v0.5.0-tmpguard-fcdb68b24/agent-host";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-worker-release-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Adoption_records_the_worker_release_and_the_daemon_release()
    {
        var server = new ReviewPlane();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateAdoptableSlotAsync(state, SupersededRelease);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(logs, line =>
            line.Contains("adopting persisted review attempt=attempt-1", StringComparison.Ordinal)
            && line.Contains($"worker-release={SupersededRelease}", StringComparison.Ordinal)
            && line.Contains(
                $"daemon-release={RunnerReleaseIdentity.Current}",
                StringComparison.Ordinal));

        // The verdict itself carries the provenance, so the grade file and the
        // card can name the build without re-reading a host-local log.
        var worker = Assert.Single(server.Reports).Environment.Worker;
        Assert.NotNull(worker);
        Assert.Equal(SupersededRelease, worker!.WorkerReleaseId);
        Assert.Equal(SupersededBinary, worker.WorkerBinaryPath);
        Assert.Equal(RunnerReleaseIdentity.Current, worker.DaemonReleaseId);
        Assert.Equal(
            $"graded by release {SupersededRelease}, current {RunnerReleaseIdentity.Current}",
            ReviewWorkerProvenancePolicy.SupersededNotice(worker));
    }

    /// <summary>
    /// The launching daemon runs its own executable, so the worker's release is
    /// the daemon's release. Stamping it at launch keeps an attempt attributable
    /// even if the worker's own identity record is never re-read.
    /// </summary>
    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task A_launched_worker_is_stamped_with_the_launching_daemon_release()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        var workspace = Path.Combine(_root, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspace);
        var slot = state.Create(Claim(), workspace);

        var process = DurableReviewProcess.Start(options, slot);
        try
        {
            Assert.Equal(RunnerReleaseIdentity.Current, process.ReleaseId);
            Assert.Equal(RunnerReleaseIdentity.CurrentBinaryPath, process.BinaryPath);

            // The worker records the same identity for itself, so a replacement
            // daemon can recover it from an unstamped slot record.
            var identityPath = Path.Combine(slot.WorkerDirectory, "review-worker.json");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(identityPath) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(File.Exists(identityPath), "the detached worker never recorded its identity");
            var identity = JsonSerializer.Deserialize<DetachedReviewIdentity>(
                await File.ReadAllTextAsync(identityPath),
                Json);
            Assert.Equal(RunnerReleaseIdentity.Current, identity!.ReleaseId);
            Assert.Equal(RunnerReleaseIdentity.CurrentBinaryPath, identity.BinaryPath);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task A_slot_from_before_this_provenance_recovers_the_release_from_the_worker()
    {
        var state = new ReviewStateStore(Path.Combine(_root, "state"));
        var slot = await CreateAdoptableSlotAsync(state, workerReleaseId: null);
        await File.WriteAllTextAsync(
            Path.Combine(slot.WorkerDirectory, "review-worker.json"),
            JsonSerializer.Serialize(
                new DetachedReviewIdentity(
                    4242,
                    DateTime.UtcNow,
                    slot.WorkspacePath,
                    SupersededRelease,
                    SupersededBinary),
                Json));

        var recovered = DurableReviewProcess.WithWorkerProvenance(slot);

        Assert.Equal(SupersededRelease, recovered.WorkerReleaseId);
        Assert.Equal(SupersededBinary, recovered.WorkerBinaryPath);
    }

    [Fact]
    public void A_slot_with_no_worker_record_reports_an_unknown_release_rather_than_the_daemons()
    {
        var state = new ReviewStateStore(Path.Combine(_root, "state"));
        var workspace = Path.Combine(_root, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspace);
        var slot = state.Create(Claim(), workspace);

        var provenance = DurableReviewProcess.Provenance(
            DurableReviewProcess.WithWorkerProvenance(slot));

        Assert.Equal(ReviewWorkerProvenancePolicy.Unknown, provenance.WorkerReleaseId);
        Assert.Equal(ReviewWorkerProvenancePolicy.Unknown, provenance.WorkerBinaryPath);
        Assert.Equal(RunnerReleaseIdentity.Current, provenance.DaemonReleaseId);
        // An unprovable release must not be reported as a mismatch.
        Assert.Null(ReviewWorkerProvenancePolicy.SupersededNotice(provenance));
    }

    [Fact]
    public async Task A_release_mismatch_notices_the_superseded_worker_and_still_claims()
    {
        var server = new ReviewPlane();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        await CreateAdoptableSlotAsync(state, SupersededRelease);
        var logs = new ConcurrentQueue<string>();

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var run = new RemoteReviewDaemon(options, client, logs.Enqueue, Admitting).RunAsync(shutdown.Token);
        while (server.ClaimAttempts == 0) await Task.Delay(20, shutdown.Token);
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var notice = Assert.Single(
            logs,
            line => line.Contains("review worker release superseded", StringComparison.Ordinal));
        Assert.Contains($"attempt={Attempt.AttemptId}", notice, StringComparison.Ordinal);
        Assert.Contains(
            $"graded by release {SupersededRelease}, current {RunnerReleaseIdentity.Current}",
            notice,
            StringComparison.Ordinal);
        Assert.Contains($"worker-binary={SupersededBinary}", notice, StringComparison.Ordinal);
        // Default behaviour is unchanged: the daemon reports the mismatch and
        // keeps claiming beside the adopted attempt.
        Assert.True(server.ClaimAttempts > 0);
    }

    [Fact]
    public async Task Release_drain_waits_for_the_superseded_attempt_before_it_claims()
    {
        var server = new ReviewPlane(blockReport: true);
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options(releaseDrain: true);
        var state = new ReviewStateStore(options.StateDir);
        await CreateAdoptableSlotAsync(state, SupersededRelease);
        var logs = new ConcurrentQueue<string>();

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var run = new RemoteReviewDaemon(options, client, logs.Enqueue, Admitting).RunAsync(shutdown.Token);

        // The adopted attempt is mid-report, so the drain must hold admission.
        await server.WaitForReportAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(5), shutdown.Token);
        Assert.Equal(0, server.ClaimAttempts);
        Assert.Contains(logs, line =>
            line.Contains("review slot admission closed", StringComparison.Ordinal)
            && line.Contains("release drain", StringComparison.Ordinal)
            && line.Contains(SupersededRelease, StringComparison.Ordinal));

        // Finishing the adopted attempt - never killing it - reopens claims.
        server.ReleaseReport();
        while (server.ClaimAttempts == 0) await Task.Delay(20, shutdown.Token);
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(server.Reports);
        Assert.Contains(logs, line =>
            line.Contains("review slot admission reopened", StringComparison.Ordinal));
    }

    private static HostTelemetrySample Admitting(
        int activeSlots,
        TaskServerConnectivitySnapshot? _)
        => new(
            DateTime.UtcNow,
            CpuPercent: 1.0,
            Load1: 0.0,
            Load5: 0.0,
            Load15: 0.0,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: null,
            IoWaitPercent: null,
            CpuCores: Environment.ProcessorCount,
            ActiveSlots: activeSlots);

    /// <summary>
    /// A handed-off slot with a durable terminal result and no live process: the
    /// exact shape a replacement daemon adopts after a planned restart.
    /// </summary>
    private async Task<PersistedReviewSlot> CreateAdoptableSlotAsync(
        ReviewStateStore state,
        string? workerReleaseId)
    {
        var workspace = Path.Combine(_root, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspace);
        var slot = state.Create(Claim(), workspace);
        await File.WriteAllTextAsync(
            Path.Combine(slot.WorkerDirectory, "review-result.json"),
            JsonSerializer.Serialize(
                new DetachedReviewResult(
                    new ReviewExecutionEvidence(
                        "Pass",
                        new ReviewWorkspaceProofDto(
                            "example/repository",
                            new string('a', 40),
                            new string('a', 40),
                            "main",
                            false,
                            false,
                            new string('b', 64),
                            "review-attempt-1-f17"),
                        [],
                        [],
                        []),
                    null,
                    null,
                    DateTime.UtcNow),
                Json));
        return state.Save(slot with
        {
            Phase = "handed-off",
            WorkerReleaseId = workerReleaseId,
            WorkerBinaryPath = workerReleaseId is null ? null : SupersededBinary,
        });
    }

    private RunnerOptions Options(bool releaseDrain = false) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "review-runner",
        RunnerName = "review-runner",
        Hostname = "review-host",
        BackendName = "test",
        Role = "review",
        WorkDir = Path.Combine(_root, "coding-work"),
        ReviewWorkDir = _root,
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        ClaudeCliBin = "test",
        TtlSeconds = 120,
        // An hour of heartbeat silence: every renewal is an adoption check.
        HeartbeatSeconds = 3600,
        HandoffLeaseTtlSeconds = 300,
        PollSeconds = 1,
        HostMaxParallelism = 2,
        IdleWatchdogMinutes = 60,
        ServerRequestTimeoutSeconds = 120,
        ReviewReleaseDrain = releaseDrain,
    };

    private static ReviewAttemptDto Attempt => new(
        "attempt-1", "subject-1", "AGT-2863", 1, "leased",
        "review-runner", "review-host", 17, ReviewPlane.Now, null, null, null, null);

    private static ReviewClaimResponse Claim()
    {
        var subject = new ReviewSubjectDto(
            "subject-1", "AGT-2863", "run-1", "example/repository", null,
            new string('a', 40), null, "bundle", new string('b', 64),
            "coding-host", "policy-v1", new ReviewPlanDto([], []), ReviewPlane.Now);
        return new ReviewClaimResponse("claimed", Attempt, subject, ReviewPlane.Lease(17));
    }

    /// <summary>
    /// Minimal review plane: it honours the persisted adoption authority, has no
    /// queued work, and can hold the terminal report open so a test can observe
    /// the daemon while an adopted attempt is still running.
    /// </summary>
    private sealed class ReviewPlane(bool blockReport = false) : HttpMessageHandler
    {
        internal static readonly DateTime Now = new(2026, 9, 17, 17, 46, 0, DateTimeKind.Utc);

        private readonly TaskCompletionSource _reportEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReport =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimAttempts;

        public ConcurrentQueue<ReviewReportRequest> ReportQueue { get; } = new();
        public IReadOnlyList<ReviewReportRequest> Reports => ReportQueue.ToArray();
        public int ClaimAttempts => Volatile.Read(ref _claimAttempts);
        public Task WaitForReportAsync() => _reportEntered.Task;
        public void ReleaseReport() => _releaseReport.TrySetResult();

        internal static ReviewLeaseDto Lease(long fence)
            => new(
                "lease-1", "attempt-1", "subject-1", "review-runner", "instance-1",
                "review-host", fence, Now, DateTime.UtcNow.AddMinutes(30), "active",
                $"review-attempt-1-f{fence}", 25000, 23);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
            {
                var advertisement = await ReadAsync<CapabilityAdvertisementRequest>(request);
                return JsonResponse(new RunnerCapabilitySnapshotDto(
                    advertisement.RunnerId, "review-runner", "review-host",
                    advertisement.InstanceId, "test", TaskServerProtocol.Current, "active",
                    Now, Now,
                    new RemoteHostAdmissionDto("review-host", "open", null, null, null, null),
                    [],
                    null));
            }
            if (request.Method == HttpMethod.Put
                && path.StartsWith("/api/v1/runners/", StringComparison.Ordinal))
            {
                var registration = await ReadAsync<RegisterRunnerRequest>(request);
                return JsonResponse(new RunnerDto(
                    "review-runner", registration.Name, registration.HostId,
                    registration.InstanceId, registration.RunnerVersion,
                    registration.ProtocolVersion, "active", Now, Now));
            }
            if (path.EndsWith("/review-claims", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _claimAttempts);
                return JsonResponse(new ReviewClaimResponse("empty", Message: "No queued review."));
            }
            if (request.Method == HttpMethod.Get
                && path.StartsWith("/api/v1/reviews/attempts/", StringComparison.Ordinal))
            {
                return JsonResponse(Attempt);
            }
            if (path.EndsWith("/lease/renew", StringComparison.Ordinal))
            {
                var renew = await ReadAsync<ReviewLeaseRenewRequest>(request);
                return JsonResponse(Lease(renew.Fence));
            }
            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                var report = await ReadAsync<ReviewReportRequest>(request);
                ReportQueue.Enqueue(report);
                _reportEntered.TrySetResult();
                if (blockReport) await _releaseReport.Task.WaitAsync(cancellationToken);
                return JsonResponse(new ReviewReportDto(
                    "report-1", "attempt-1", "subject-1", report.Outcome,
                    report.FailureClassification, "accepted", new string('c', 64),
                    Now, false, "5-human-review"));
            }
            if (path.EndsWith("/cleanup", StringComparison.Ordinal))
                return JsonResponse(new ReviewCleanupResponse("cleaned", "attempt-1", Now, false));

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected review plane request: {path}"),
            };
        }

        private static async Task<T> ReadAsync<T>(HttpRequestMessage request)
            => JsonSerializer.Deserialize<T>(await request.Content!.ReadAsStringAsync(), Json)
               ?? throw new InvalidDataException($"Request body was not valid {typeof(T).Name} JSON.");

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json)),
            };
    }
}
