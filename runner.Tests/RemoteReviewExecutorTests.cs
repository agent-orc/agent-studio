using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteReviewExecutorTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _reviewRoot = Path.Combine(
        Path.GetTempPath(),
        "remote-review-executor-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Graceful_shutdown_reports_interrupted_work_before_cleanup()
    {
        var handler = new InterruptedArtifactHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        using var shutdown = new CancellationTokenSource();
        var state = new ReviewStateStore(options.StateDir);
        var execution = new RemoteReviewExecutor(options, client, state, logs.Add)
            .RunClaimedAsync(Claim(), shutdown.Token);

        await handler.ArtifactRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.CancelAsync();

        Assert.Equal(3, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        var report = Assert.Single(handler.Reports);
        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal("ExecutorRestarted", report.FailureClassification);
        Assert.Contains("daemon stopped during workspace preparation", report.Summary, StringComparison.Ordinal);
        Assert.Contains("Lost work extent: 0 of 0 review commands completed", report.Summary, StringComparison.Ordinal);
        Assert.Equal(17, report.Fence);
        Assert.Equal(23, report.AuthorityEpoch);

        var cleanup = Assert.Single(handler.Cleanups);
        Assert.True(cleanup.WorkspaceRemoved);
        Assert.Null(cleanup.FailureClassification);
        Assert.Contains(
            logs,
            line => line.Contains(
                "classification=ExecutorRestarted",
                StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_reviewRoot, "review-attempt-1-f17")));
        Assert.Empty(state.LoadAll());
    }

    public void Dispose()
    {
        try { Directory.Delete(_reviewRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Atomic_worker_state_write_is_cleanly_abandoned_after_cleanup_removes_directory()
    {
        var stateDirectory = Path.Combine(_reviewRoot, "removed-attempt-state");
        Directory.CreateDirectory(stateDirectory);
        var target = Path.Combine(stateDirectory, "review-result.json");
        Directory.Delete(stateDirectory, recursive: true);

        var written = await DurableReviewProcess.WriteAtomicAsync(target, "{}");

        Assert.False(written);
    }

    [Fact]
    public async Task Reattach_with_durable_result_reports_it_without_starting_a_worker()
    {
        var handler = new InterruptedArtifactHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var workspacePath = Path.Combine(_reviewRoot, "review-attempt-1-f17", "repository");
        var slot = state.Create(Claim(), workspacePath);
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

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(3, exitCode); // The fixture acknowledges every report as ReviewInfra.
        Assert.Equal("Pass", Assert.Single(handler.Reports).Outcome);
        Assert.DoesNotContain(logs, line => line.Contains(
            "detached review worker started",
            StringComparison.Ordinal));
        Assert.Empty(state.LoadAll());
    }

    /// <summary>
    /// Scenario test for AGT-2750: a replacement daemon re-adopts a persisted
    /// review slot whose worker survived a <c>PrivateTmp=true</c> restart but
    /// lost its <c>/tmp</c> mount. <see cref="DetachedWorkerTmpMountGuard"/>
    /// (exercised here with the exact captured mountinfo shape) is what
    /// <see cref="DurableReviewProcess.VerifyLive"/> would report as the
    /// not-live reason; this proves the existing non-adoptable settlement path
    /// turns that straight into a fenced <c>ReviewInfra</c>/<c>ExecutorRestarted</c>
    /// report the moment the daemon reconciles the slot, instead of a live
    /// worker being left to run to a doomed conclusion and getting graded as a
    /// product failure an hour later.
    /// </summary>
    [Fact]
    public async Task Torn_down_tmp_mount_settles_immediately_as_review_infra()
    {
        var mountInfo = new[]
        {
            "656 654 0:59 " +
            "/tmp/systemd-private-dedae28ed8ee49ab9d9a88405a36b280-agent-runner-review.service-WhdhIS/tmp//deleted " +
            "/tmp rw,nosuid,nodev shared:326 - tmpfs tmpfs rw,size=1048576k",
        };
        Assert.True(DetachedWorkerTmpMountGuard.TmpMountWasTornDown(mountInfo, out var reason));

        var handler = new InterruptedArtifactHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var workspacePath = Path.Combine(_reviewRoot, "review-attempt-1-f17", "repository");
        var slot = state.Create(Claim(), workspacePath) with { Phase = "running" };
        state.Save(slot);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReportNonAdoptableAsync(slot, reason, CancellationToken.None);

        Assert.Equal(3, exitCode); // The fixture acknowledges every report as ReviewInfra.
        var report = Assert.Single(handler.Reports);
        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal("ExecutorRestarted", report.FailureClassification);
        Assert.Contains("deleted", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NewTestFailures", report.Summary, StringComparison.Ordinal);
        Assert.Empty(state.LoadAll());
    }

    [Fact]
    public async Task Report_submission_retries_transient_failures_with_the_same_durable_result()
    {
        var handler = new ReportSequenceHandler(transientFailures: 2);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateCompletedSlotAsync(state);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            ReportRetryDelayOverride = _ => TimeSpan.Zero,
        };

        var exitCode = await executor.ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, handler.ReportAttempts);
        Assert.Equal(1, handler.CleanupAttempts);
        Assert.Equal(2, logs.Count(line => line.Contains("review-report-pending", StringComparison.Ordinal)));
        Assert.Contains(logs, line => line.Contains("submissionAttempts=3", StringComparison.Ordinal));
        Assert.Empty(state.LoadAll());
    }

    [Fact]
    public async Task Product_failure_exit_deletes_the_cleaned_slot_record()
    {
        var handler = new ReportSequenceHandler(reportOutcome: "ProductFailure");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateCompletedSlotAsync(state);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Empty(state.LoadAll());
        Assert.Contains(logs, line =>
            line.Contains("review slot state deleted", StringComparison.Ordinal)
            && line.Contains("terminalOutcome=ProductFailure", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "task-not-found", "TaskNotFound")]
    [InlineData(HttpStatusCode.NotFound, "not-found", "TaskNotFound")]
    [InlineData(HttpStatusCode.Conflict, "Superseded", "Superseded")]
    public async Task Permanent_report_rejection_is_classified_and_reaps_the_orphaned_slot(
        HttpStatusCode status,
        string errorCode,
        string classification)
    {
        var handler = new ReportSequenceHandler(terminalStatus: status, terminalCode: errorCode);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateCompletedSlotAsync(state);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(1, handler.ReportAttempts);
        Assert.Equal(0, handler.CleanupAttempts);
        Assert.Contains(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal)
            && line.Contains($"classification={classification}", StringComparison.Ordinal)
            && line.Contains("cleanup=removed", StringComparison.Ordinal));
        Assert.Empty(state.LoadAll());
        Assert.False(Directory.Exists(Path.GetDirectoryName(slot.WorkspacePath)));
    }

    [Fact]
    public async Task Slot_hygiene_counts_report_pending_age_without_refreshing_its_start_time()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateCompletedSlotAsync(state);
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        state.Save(slot with
        {
            Phase = "report-pending",
            ReportPendingSinceUtc = now.AddMinutes(-90),
            ReportSubmissionAttempts = 4,
        });

        var snapshot = state.GetHygieneSnapshot(now);

        Assert.Equal(1, snapshot.Total);
        Assert.Equal(1, snapshot.ReportPending);
        Assert.Equal(TimeSpan.FromMinutes(90), snapshot.OldestReportPendingAge);
        Assert.Equal(0, snapshot.TerminalCleanupPending);
    }

    [Fact]
    public async Task Slot_hygiene_loads_legacy_state_and_uses_updated_time_as_age_fallback()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        var slot = state.Create(Claim(), Path.Combine(_reviewRoot, "legacy", "repository"));
        var legacyUpdatedAt = new DateTime(2026, 8, 10, 10, 30, 0, DateTimeKind.Utc);
        var statePath = Path.Combine(state.Root, "attempt-1.review-slot.json");
        var legacy = JsonSerializer.SerializeToNode(slot with { Phase = "report-pending" }, Json)!
            .AsObject();
        legacy["updatedAtUtc"] = legacyUpdatedAt;
        foreach (var field in new[]
                 {
                     "createdAtUtc",
                     "reportPendingSinceUtc",
                     "reportSubmissionAttempts",
                     "lastReportSubmissionAtUtc",
                     "lastReportStatusCode",
                     "lastReportErrorCode",
                     "lastReportError",
                     "terminalClassification",
                 })
        {
            legacy.Remove(field);
        }
        await File.WriteAllTextAsync(statePath, legacy.ToJsonString(Json));

        var loaded = Assert.Single(state.LoadAll());
        var snapshot = state.GetHygieneSnapshot(legacyUpdatedAt.AddMinutes(90));

        Assert.Null(loaded.CreatedAtUtc);
        Assert.Null(loaded.ReportPendingSinceUtc);
        Assert.Equal(0, loaded.ReportSubmissionAttempts);
        Assert.Equal(1, snapshot.ReportPending);
        Assert.Equal(TimeSpan.FromMinutes(90), snapshot.OldestReportPendingAge);
    }

    private async Task<PersistedReviewSlot> CreateCompletedSlotAsync(ReviewStateStore state)
    {
        var workspacePath = Path.Combine(_reviewRoot, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspacePath);
        var slot = state.Create(Claim(), workspacePath);
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
        return slot;
    }

    private RunnerOptions Options() => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "review-runner",
        RunnerName = "review-runner",
        Hostname = "review-host",
        BackendName = "test",
        Role = "review",
        WorkDir = _reviewRoot,
        ReviewWorkDir = _reviewRoot,
        StateDir = Path.Combine(_reviewRoot, "state"),
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
    };

    private static ReviewClaimResponse Claim()
    {
        var now = new DateTime(2026, 8, 2, 15, 0, 0, DateTimeKind.Utc);
        var attempt = new ReviewAttemptDto(
            "attempt-1",
            "subject-1",
            "AGT-2471",
            1,
            "leased",
            "review-runner",
            "review-host",
            17,
            now,
            null,
            null,
            null,
            null);
        var subject = new ReviewSubjectDto(
            "subject-1",
            "AGT-2471",
            "run-1",
            "example/repository",
            null,
            new string('a', 40),
            null,
            "bundle",
            new string('b', 64),
            "coding-host",
            "policy-v1",
            new ReviewPlanDto([], []),
            now);
        var lease = new ReviewLeaseDto(
            "lease-1",
            attempt.AttemptId,
            subject.SubjectId,
            "review-runner",
            "instance-1",
            "review-host",
            17,
            now,
            now.AddMinutes(2),
            "active",
            "review-attempt-1-f17",
            25000,
            23);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }

    private sealed class InterruptedArtifactHandler : HttpMessageHandler
    {
        public TaskCompletionSource ArtifactRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ReviewReportRequest> Reports { get; } = [];
        public List<ReviewCleanupRequest> Cleanups { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.EndsWith("/artifacts/bundle/content", StringComparison.Ordinal))
            {
                ArtifactRequested.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancelled artifact request must not complete.");
            }

            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                Reports.Add(await ReadAsync<ReviewReportRequest>(request));
                return JsonResponse(new ReviewReportDto(
                    "report-1",
                    "attempt-1",
                    "subject-1",
                    "ReviewInfra",
                    "ExecutorRestarted",
                    "retry required",
                    new string('c', 64),
                    DateTime.UtcNow,
                    true,
                    "4-auto-review"));
            }

            if (path.EndsWith("/cleanup", StringComparison.Ordinal))
            {
                Cleanups.Add(await ReadAsync<ReviewCleanupRequest>(request));
                return JsonResponse(new ReviewCleanupResponse(
                    "cleaned",
                    "attempt-1",
                    DateTime.UtcNow,
                    true));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected test request"),
            };
        }

        private static async Task<T> ReadAsync<T>(HttpRequestMessage request)
            => JsonSerializer.Deserialize<T>(
                   await request.Content!.ReadAsStringAsync(),
                   Json)
               ?? throw new InvalidDataException($"Request body was not valid {typeof(T).Name} JSON.");

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json)),
            };
    }

    private sealed class ReportSequenceHandler(
        int transientFailures = 0,
        HttpStatusCode? terminalStatus = null,
        string? terminalCode = null,
        string reportOutcome = "Pass") : HttpMessageHandler
    {
        public int ReportAttempts { get; private set; }
        public int CleanupAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                ReportAttempts++;
                if (terminalStatus is { } rejected)
                    return Task.FromResult(ApiError(rejected, terminalCode ?? "rejected"));
                if (ReportAttempts <= transientFailures)
                    return Task.FromResult(ApiError(HttpStatusCode.ServiceUnavailable, "temporary-overload"));
                return Task.FromResult(JsonResponse(new ReviewReportDto(
                    "report-1",
                    "attempt-1",
                    "subject-1",
                    reportOutcome,
                    null,
                    "review passed",
                    new string('c', 64),
                    DateTime.UtcNow,
                    false,
                    "5-human-review")));
            }

            if (path.EndsWith("/cleanup", StringComparison.Ordinal))
            {
                CleanupAttempts++;
                return Task.FromResult(JsonResponse(new ReviewCleanupResponse(
                    "cleaned",
                    "attempt-1",
                    DateTime.UtcNow,
                    false)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage ApiError(HttpStatusCode status, string code)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    code,
                    message = "synthetic report failure",
                    detail = (string?)null,
                }, Json)),
            };

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json)),
            };
    }
}
