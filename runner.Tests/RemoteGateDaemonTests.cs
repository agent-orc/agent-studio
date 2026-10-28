using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

[Trait("Category", "MachineBound")]
[Trait("Category", "ReviewFlaky")]
public sealed class RemoteGateDaemonTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("exit 0", null)]
    [InlineData("exit 3", GateFailureClasses.ProductFailure)]
    [InlineData("sleep 5", GateFailureClasses.ExecutionTimeout)]
    public async Task Executor_materializes_exact_sha_and_reports_command_result(
        string command, string? expectedFailure)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", command);
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);
        var daemon = new RemoteGateDaemon(fixture.Options, client, _ => { });

        Assert.True(await daemon.ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        var report = Assert.Single(server.Reports);
        Assert.Equal(expectedFailure, report.Report.FailureClassification);
        Assert.Equal(expectedFailure is null ? GateStates.Passed
            : expectedFailure == GateFailureClasses.ProductFailure ? GateStates.ProductFailed
            : GateStates.InfraFailed, report.Report.Outcome);
        Assert.Equal(sha, report.Report.TestedSha);
        Assert.Matches("^[0-9a-f]{40}$", report.Report.TestedTree);
        Assert.False(report.Report.DirtyBefore);
        Assert.Equal("clean", report.Report.CleanupStatus);
        Assert.Equal(fixture.Lease.Fence, report.Authority.Fence);
        Assert.Single(report.Report.Commands);
        Assert.False(Directory.Exists(Path.Combine(fixture.Options.ReviewWorkDir,
            fixture.Lease.ResourceNamespace)));
    }

    [Fact]
    public async Task Executor_runs_a_verification_command_in_its_declared_subdirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "test -f ../proof.txt",
            workingSubdir: "frontend");
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);

        Assert.True(await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        Assert.Equal(GateStates.Passed, Assert.Single(server.Reports).Report.Outcome);
    }

    [Fact]
    public async Task Missing_declared_snapshot_reports_infrastructure_failure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/missing", "exit 0");
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);

        Assert.True(await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        var report = Assert.Single(server.Reports).Report;
        Assert.Equal(GateFailureClasses.SnapshotUnavailable, report.FailureClassification);
        Assert.Empty(report.Commands);
        Assert.Equal("clean", report.CleanupStatus);
    }

    [Fact]
    public async Task Overall_deadline_reports_execution_timeout()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "sleep 5",
            commandTimeout: 10, overallTimeout: 2);
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);

        Assert.True(await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        Assert.Equal(GateFailureClasses.ExecutionTimeout,
            Assert.Single(server.Reports).Report.FailureClassification);
    }

    [Fact]
    public async Task Shutdown_preserves_the_claim_for_restart_recovery_without_reporting_tool_failure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "sleep 30",
            commandTimeout: 60, overallTimeout: 90);
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);
        Directory.CreateDirectory(fixture.Options.StateDir);
        var claimFile = Path.Combine(fixture.Options.StateDir, "gate-claim.json");
        await File.WriteAllTextAsync(claimFile, JsonSerializer.Serialize(
            new GateClaimResponse("claimed", subject, fixture.Attempt, fixture.Lease)));
        var daemon = new RemoteGateDaemon(fixture.Options, client, _ => { });
        using var shutdown = new CancellationTokenSource();

        var execution = daemon.ExecuteAsync(subject, fixture.Attempt, fixture.Lease, shutdown.Token);
        await server.RunningEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        shutdown.Cancel();

        Assert.False(await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(claimFile));
        Assert.Empty(server.Reports);
        Assert.DoesNotContain(GateStates.Reporting, server.Phases);
        Assert.DoesNotContain(GateStates.Cleaning, server.Phases);

        await daemon.RecoverPreviousClaimAsync(default);
        Assert.False(File.Exists(claimFile));
        Assert.Single(server.Containments);
        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task Cleanup_failure_is_reported_and_retains_the_persisted_claim()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "exit 0");
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);
        var daemon = new RemoteGateDaemon(fixture.Options, client, _ => { },
            async (workspace, attemptId) =>
            {
                Assert.True(await workspace.CleanupAsync(attemptId));
                return false;
            });

        Assert.False(await daemon.ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        Assert.Equal("failed", Assert.Single(server.Reports).Report.CleanupStatus);
    }

    [Fact]
    public async Task Restart_discards_a_persisted_claim_only_after_terminal_cleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "exit 0");
        var completed = fixture.Attempt with { State = GateStates.Passed, CleanedAt = DateTime.UtcNow };
        var server = new GateServer(subject, completed, fixture.Lease);
        using var client = fixture.Client(server);
        Directory.CreateDirectory(fixture.Options.StateDir);
        var claimFile = Path.Combine(fixture.Options.StateDir, "gate-claim.json");
        await File.WriteAllTextAsync(claimFile, JsonSerializer.Serialize(
            new GateClaimResponse("claimed", subject, fixture.Attempt, fixture.Lease)));

        await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .RecoverPreviousClaimAsync(default);

        Assert.False(File.Exists(claimFile));
        Assert.Equal(1, server.StatusReads);
        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task Restart_reaps_active_namespace_and_confirms_fenced_containment()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "exit 0");
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease);
        using var client = fixture.Client(server);
        Directory.CreateDirectory(fixture.Options.StateDir);
        var claimFile = Path.Combine(fixture.Options.StateDir, "gate-claim.json");
        await File.WriteAllTextAsync(claimFile, JsonSerializer.Serialize(
            new GateClaimResponse("claimed", subject, fixture.Attempt, fixture.Lease)));
        var namespaceRoot = Path.Combine(fixture.Options.ReviewWorkDir,
            fixture.Lease.ResourceNamespace);
        Directory.CreateDirectory(namespaceRoot);
        await File.WriteAllTextAsync(Path.Combine(namespaceRoot, "orphan.txt"), "owned");

        await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .RecoverPreviousClaimAsync(default);

        Assert.False(File.Exists(claimFile));
        Assert.False(Directory.Exists(namespaceRoot));
        var containment = Assert.Single(server.Containments);
        Assert.Equal(fixture.Lease.Fence, containment.Fence);
        Assert.Equal(fixture.Lease.ResourceNamespace, containment.ResourceNamespace);
        Assert.True(containment.NoProcesses);
        Assert.True(containment.WorkspaceRemoved);
    }

    [Fact]
    public async Task Bundle_subject_downloads_the_exact_bundle_and_reports_its_host_proof()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var bundle = fixture.CreateBundle(sha);
        var subject = fixture.BundleSubject(sha, bundle, "test -f proof.txt");
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease) { Bundle = bundle };
        using var client = fixture.Client(server);

        Assert.True(await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        var report = Assert.Single(server.Reports).Report;
        Assert.Equal(GateStates.Passed, report.Outcome);
        Assert.Equal(1, server.BundleReads);
        Assert.Equal(new GateSubjectProof(subject.PlanHash, subject.PolicyHash, sha,
            subject.Bundle!.Sha256, bundle.LongLength), report.Proof);
        Assert.Null(GateRules.ProofViolation(subject, report));
    }

    public static TheoryData<string> PreCommandFailures => new()
    {
        "checksum", "size", "sha", "plan",
    };

    [Theory]
    [MemberData(nameof(PreCommandFailures))]
    public async Task Wrong_bundle_or_subject_identity_fails_before_any_verify_command(string defect)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var bundle = fixture.CreateBundle(sha);
        var marker = Path.Combine(fixture.Root, "command-ran");
        var subject = fixture.BundleSubject(sha, bundle, $"touch '{marker}'");
        var otherSha = new string('f', 64);
        subject = defect switch
        {
            "checksum" => subject with { Bundle = new GateBundleLocator(otherSha, bundle.LongLength,
                GateBundleLimits.Locator(otherSha)) },
            "size" => subject with { Bundle = subject.Bundle! with { SizeBytes = bundle.LongLength + 1 } },
            "sha" => subject with { ExpectedSha = new string('e', 40) },
            _ => subject with { PlanHash = new string('d', 64) },
        };
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease) { Bundle = bundle };
        using var client = fixture.Client(server);

        Assert.True(await new RemoteGateDaemon(fixture.Options, client, _ => { })
            .ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default));

        var report = Assert.Single(server.Reports).Report;
        Assert.Equal(GateFailureClasses.SnapshotUnavailable, report.FailureClassification);
        Assert.Empty(report.Commands);
        Assert.False(File.Exists(marker));
        Assert.DoesNotContain(GateStates.Running, server.Phases);
        Assert.Equal(defect == "plan" ? 0 : 1, server.BundleReads);
    }

    [Fact]
    public async Task Lease_loss_stops_the_running_command_and_reports_lost_lease()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new GateFixture();
        var sha = fixture.CreateRepository();
        var subject = fixture.Subject(sha, "refs/heads/main", "sleep 30",
            commandTimeout: 60, overallTimeout: 90);
        var server = new GateServer(subject, fixture.Attempt, fixture.Lease) { FenceRenewals = true };
        using var client = fixture.Client(server);
        var daemon = new RemoteGateDaemon(fixture.Options, client, _ => { })
        {
            RenewInterval = TimeSpan.FromMilliseconds(200),
        };
        var watch = Stopwatch.StartNew();

        Assert.True(await daemon.ExecuteAsync(subject, fixture.Attempt, fixture.Lease, default)
            .WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(GateFailureClasses.LostLease, Assert.Single(server.Reports).Report.FailureClassification);
    }

    private sealed class GateFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-daemon-test-" + Guid.NewGuid().ToString("N"));
        private string Repository => Path.Combine(_root, "source");
        public string Root => _root;

        public RunnerOptions Options => new()
        {
            ServerUrl = "http://task-server",
            RunnerId = "gate-a",
            RunnerName = "gate-a",
            Hostname = "host-a",
            BackendName = "test",
            Role = "gate",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = Path.Combine(_root, "gate-work"),
            StateDir = Path.Combine(_root, "state"),
            BaseBranch = "main",
        };

        public GateAttempt Attempt => new("attempt-1", "subject-1", 1, GateStates.Claimed,
            "gate-a", "host-a", null, null, DateTime.UtcNow, DateTime.UtcNow,
            null, null, 7, DateTime.UtcNow.AddMinutes(3));

        public GateLease Lease => new("lease-1", "attempt-1", "gate-a", "instance-1",
            "host-a", 7, 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(3),
            "gate-attempt-1-f7", 30000);

        public string CreateRepository()
        {
            Directory.CreateDirectory(Repository);
            Git("init", "-b", "main", Repository);
            Git("-C", Repository, "config", "user.email", "gate@example.invalid");
            Git("-C", Repository, "config", "user.name", "Gate Test");
            File.WriteAllText(Path.Combine(Repository, "proof.txt"), "exact subject\n");
            Directory.CreateDirectory(Path.Combine(Repository, "frontend"));
            File.WriteAllText(Path.Combine(Repository, "frontend", ".keep"), "");
            // A unique commit keeps the host source cache from serving another test's bundle.
            File.WriteAllText(Path.Combine(Repository, "nonce.txt"), Guid.NewGuid().ToString("N"));
            Git("-C", Repository, "add", "proof.txt", "frontend/.keep", "nonce.txt");
            Git("-C", Repository, "commit", "-m", "fixture");
            return Git("-C", Repository, "rev-parse", "HEAD").Trim();
        }

        public GateSubject Subject(string sha, string resultRef, string command,
            int commandTimeout = 1, int overallTimeout = 15,
            string workingSubdir = "")
        {
            var url = Repository;
            return new GateSubject("subject-1", "AGT-test", "run-1",
                RepositoryIdentityContract.FromUrl(url)!, url, sha, resultRef,
                null, null, "plan-hash", "policy-hash", "version", "selection",
                new GatePlan("post-build-test-gate", 1,
                    [new GateCommand("verify", "/bin/sh", ["-c", command],
                        commandTimeout, workingSubdir)],
                    "", overallTimeout, [], 1024, "remove"),
                DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), 2);
        }

        private static readonly DateTime SubjectCreatedAt = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        public byte[] CreateBundle(string sha)
        {
            var path = Path.Combine(_root, "subject.bundle");
            Git("-C", Repository, "update-ref", "refs/heads/gate-subject", sha);
            Git("-C", Repository, "bundle", "create", path, "refs/heads/gate-subject");
            return File.ReadAllBytes(path);
        }

        public GateSubject BundleSubject(string sha, byte[] bundle, string command)
        {
            var plan = new GatePlan("post-build-test-gate", 1,
                [new GateCommand("verify", "/bin/sh", ["-c", command], 10)], "", 30, [], 1024, "always");
            var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bundle));
            return new GateSubject("subject-1", "job-1", "merge:1", "product", null, sha, null, null, null,
                GateRules.PlanHash(plan), "policy-hash", "3", "selection", plan, SubjectCreatedAt,
                SubjectCreatedAt.AddMinutes(5), 2, Kind: GateSubjectKinds.Merge,
                Bundle: new GateBundleLocator(digest, bundle.LongLength, GateBundleLimits.Locator(digest)));
        }

        public TaskServerClient Client(GateServer server)
        {
            var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
            return new TaskServerClient(http, Options.RunnerId, usesDurableTaskServer: true,
                options: Options, runnerInstanceId: "instance-1");
        }

        private static string Git(params string[] args)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git")
                { RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(error);
            return output;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class GateServer(GateSubject subject, GateAttempt attempt, GateLease lease)
        : HttpMessageHandler
    {
        public List<SubmitGateReportRequest> Reports { get; } = [];
        public List<GateContainmentRequest> Containments { get; } = [];
        public List<string> Phases { get; } = [];
        public TaskCompletionSource RunningEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int StatusReads { get; private set; }
        public byte[]? Bundle { get; init; }
        public int BundleReads { get; private set; }
        public bool FenceRenewals { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/v1/gates/bundles/", StringComparison.Ordinal) && Bundle is not null)
            {
                BundleReads++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bundle) };
            }
            if (FenceRenewals && path.EndsWith("/renew", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new ApiErrorBody("stale-gate-fence",
                        "Gate lease is no longer active."), Json), Encoding.UTF8, "application/json"),
                };
            if (path == "/api/v1/gates/subjects/subject-1")
            {
                StatusReads++;
                return Ok(new GateStatus(subject, [attempt], null, 0, "host-a", attempt.State,
                    1, attempt.Deadline, null, null, false));
            }
            if (path.EndsWith("/phase", StringComparison.Ordinal))
            {
                var phase = (await JsonSerializer.DeserializeAsync<GatePhaseRequest>(
                    await request.Content!.ReadAsStreamAsync(ct), Json, ct))!;
                Phases.Add(phase.Phase);
                if (phase.Phase == GateStates.Running) RunningEntered.TrySetResult();
                return Ok(attempt with { State = GateStates.Cleaning });
            }
            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                Reports.Add((await JsonSerializer.DeserializeAsync<SubmitGateReportRequest>(
                    await request.Content!.ReadAsStreamAsync(ct), Json, ct))!);
                return Ok(new GateStatus(subject, [attempt], Reports[^1].Report, 0,
                    null, Reports[^1].Report.Outcome, 1, null, subject.ExpectedSha,
                    Reports[^1].Report.Outcome, true));
            }
            if (path.EndsWith("/renew", StringComparison.Ordinal)) return Ok(lease);
            if (path.EndsWith("/containment", StringComparison.Ordinal))
            {
                Containments.Add((await JsonSerializer.DeserializeAsync<GateContainmentRequest>(
                    await request.Content!.ReadAsStreamAsync(ct), Json, ct))!);
                return Ok(new GateStatus(subject, [attempt], null, 0, null, GateStates.Queued,
                    1, null, null, null, false));
            }
            throw new InvalidOperationException($"Unexpected gate request: {path}");
        }

        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed record ApiErrorBody(string Code, string Message);
}
