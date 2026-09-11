extern alias Runner;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using System.Diagnostics;
using AgentStudio.TestSupport;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

// The standalone runner's wire types, aliased so its re-declared WireModels
// (RunLeaseAcquireRequest, LogIngestRequest, ArtifactIngestRequest, ...) never
// collide with the backend's own same-named server records, which this test
// project imports globally. The whole point of the test is that these two
// independently-declared shapes are wire-compatible.
using Contract = AgentStudio.TaskServer.Contracts;
using RClient = Runner::AgentRunner.TaskServerClient;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RHeartbeat = Runner::AgentRunner.RunLeaseHeartbeatRequest;
using RRelease = Runner::AgentRunner.RunLeaseReleaseRequest;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RTelemetry = Runner::AgentRunner.HostTelemetrySample;
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;
using RGitCapability = Runner::AgentRunner.RunnerGitCapabilityRequest;
using RLogIngest = Runner::AgentRunner.LogIngestRequest;
using RCliLine = Runner::AgentRunner.CliOutputLine;
using RArtifactIngest = Runner::AgentRunner.ArtifactIngestRequest;
using RArtifact = Runner::AgentRunner.RunnerArtifactUpload;
using RComplete = Runner::AgentRunner.ExternalCompletionRequest;
using RDeliverable = Runner::AgentRunner.ExternalDeliverable;
using RRemoteComplete = Runner::AgentRunner.RemoteRunCompletionRequest;
using ROptions = Runner::AgentRunner.RunnerOptions;
using RTaskRunner = Runner::AgentRunner.RemoteTaskRunner;
using RDurableAgentProcess = Runner::AgentRunner.DurableAgentProcess;
using RRemoteCompletionResponse = Runner::AgentRunner.RemoteRunCompletionResponse;
using RReviewDaemon = Runner::AgentRunner.RemoteReviewDaemon;
using RReviewStateStore = Runner::AgentRunner.ReviewStateStore;
using RReviewWorkspace = Runner::AgentRunner.RemoteReviewWorkspace;

namespace AgentStudio.Tests;

/// <summary>
/// RM-5 acceptance, proven in-process: the standalone runner's <b>real</b>
/// <see cref="RClient"/> drives one task through the whole remote lifecycle
/// against the <b>real</b> Task Server endpoints hosted by
/// <see cref="WebApplicationFactory{TEntryPoint}"/> — fenced lease → heartbeat →
/// prompt fetch → log ship → artifact upload → out-of-band completion → release.
///
/// <para>
/// This closes the gap the code review flagged: the transport hinges on the
/// RM-3/RM-4 endpoints and record shapes matching <c>runner/WireModels.cs</c>,
/// which unit tests on either side cannot prove alone. Here the runner's own
/// HTTP client + its independently-declared wire records round-trip through the
/// server's serialization and endpoint logic, and the task physically re-enters
/// the board in <c>5-human-review</c> with the runner's logs and evidence — the
/// acceptance shape ("result + logs appear in the local board"), minus only the
/// physical Hetzner host, which is a deployment detail the runbook covers.
/// </para>
///
/// <para>
/// The project is named <c>agent-runner-01</c> (the acceptance project) but has
/// no saved runner mode, so the in-process <c>TaskRunnerService</c> stays in
/// manual mode and never races the runner for the seeded card.
/// </para>
/// </summary>
// MachineBound: real server/API, git processes, persisted timestamps, and host
// scheduling make these end-to-end cases intentionally machine-dependent.
[Trait("Category", "MachineBound")]
[Trait("Category", "ReviewFlaky")]
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class RemoteRunnerEndToEndTests : IDisposable
{
    private static readonly JsonSerializerOptions ApiJson = CreateApiJson();
    private const string ProjectName = "agent-runner-01";
    private const string RunnerId = "agent-runner-e2e";
    private const string TaskKey = "AGT-RUNNER-E2E";

    private readonly string _workspace;
    private readonly string _watchPath;

    public RemoteRunnerEndToEndTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-runner-e2e-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Runner_drives_one_task_end_to_end_through_the_server_api()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote runner smoke",
            "Do the remote thing and stop.");
        Directory.CreateDirectory(Path.Combine(_watchPath, "docs", "concepts"));
        File.WriteAllText(
            Path.Combine(_watchPath, "docs", "concepts", "remote-runner.md"),
            "# Remote runner\n");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;

        // 0a. Register the runner identity. The server's X-Client-Id boundary
        //     rejects every write from an unregistered id with 401, so the runner
        //     adopts the server-assigned id before it touches the lease.
        var clientId = await client.RegisterAsync("agent-runner-01 remote", "service", ct);
        Assert.False(string.IsNullOrWhiteSpace(clientId));
        Assert.Equal(clientId, client.ClientId);

        // 0b. Prompt fetch. The runner reads prompt.md over the API (code itself
        //     arrives via git origin, out of scope for an in-process test).
        var prompt = await client.ReadTaskFileAsync(TaskKey, "prompt.md", ct);
        Assert.Equal("Do the remote thing and stop.", prompt);

        // 1. Acquire the fenced lease → this runner may proceed, with a token.
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "claude"), ct);
        Assert.True(lease.Granted, $"lease not granted: {lease.Outcome} {lease.Message}");
        Assert.NotNull(lease.Lease);
        Assert.True(lease.Lease!.FencingToken > 0);
        var leaseId = lease.Lease.LeaseId;
        var token = lease.Lease.FencingToken;
        var noveltyMarker = new Contract.ProtocolNoveltyTelemetry(
            "codex",
            "0.7.0",
            "future.frame",
            1,
            1,
            new string('a', 64)).ToMarker();

        // 2. Heartbeat renews the lease with the fencing token.
        var renew = await client.RenewLeaseAsync(new RHeartbeat(
            TaskKey, leaseId, token, RunnerId, null,
            lease.Lease.AttemptId, lease.Lease.AuthorityEpoch, "e2e-renew-1"), ct);
        Assert.True(renew.Granted, $"renew not granted: {renew.Outcome} {renew.Message}");

        // 3. Ship CLI output — appended to the task's durable logs/cli-output.log.
        var logResp = await client.IngestLogsAsync(new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "remote runner: starting task"),
            new RCliLine(DateTime.UtcNow, "stdout", "● Read docs/concepts/remote-runner.md"),
            new RCliLine(DateTime.UtcNow, "system", noveltyMarker),
            new RCliLine(DateTime.UtcNow, "stdout", "remote runner: [[TASK_DONE]]"),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: leaseId,
            FencingToken: token,
            AttemptId: lease.Lease.AttemptId,
            Fence: token,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "e2e-logs-1"), ct);
        Assert.NotNull(logResp);
        Assert.Equal(4, logResp!.Appended);

        // 4. Upload evidence — decoded under the task's results/ folder.
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("evidence bytes"));
        var artResp = await client.UploadArtifactsAsync(new RArtifactIngest(TaskKey,
        [
            new RArtifact("runner-evidence--real.txt", content),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: leaseId,
            FencingToken: token,
            AttemptId: lease.Lease.AttemptId,
            Fence: token,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "e2e-artifacts-1"), ct);
        Assert.NotNull(artResp);
        Assert.Equal(1, artResp!.Uploaded);
        Assert.Contains("results/runner-evidence--real.txt", artResp.Files);

        // 5. Reconcile out-of-band: the card re-enters the local board.
        var completion = await client.CompleteAsync(TaskKey, new RComplete(
            "Ran on the remote runner; evidence uploaded.",
            [new RDeliverable(Path: "results/runner-evidence--real.txt", Note: "runner evidence")],
            Source: ProjectName), ct);
        Assert.NotNull(completion);
        Assert.Equal(TaskStates.HumanReview, completion!.TargetState);

        // 6. Release the lease so the slot is free for the next run.
        var release = await client.ReleaseLeaseAsync(new RRelease(
            TaskKey, leaseId, token, RunnerId, lease.Lease.AttemptId, lease.Lease.AuthorityEpoch, "e2e-release-1"), ct);
        Assert.Equal("Released", release.Outcome);

        // The board now shows the finished card in 5-human-review, carrying the
        // runner's shipped logs and uploaded evidence — RM-5 acceptance, proven
        // against the real API rather than asserted from the diff.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
        var moved = Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey);
        Assert.True(Directory.Exists(moved), "card did not move to 5-human-review");

        var cliLog = File.ReadAllText(Path.Combine(moved, "logs", "cli-output.log"));
        Assert.Contains("remote runner: starting task", cliLog);
        Assert.Contains("[[TASK_DONE]]", cliLog);
        var protocolDiagnostic = Assert.Single(
            RunnerEventSource.ReadRecords(new TaskInfo { Id = TaskKey, FolderPath = moved }),
            recorded => recorded.Code == "cli-frame-unknown");
        Assert.Equal("diagnostic", protocolDiagnostic.Kind);
        Assert.Equal("codex", protocolDiagnostic.Cli);
        Assert.Equal("warning", protocolDiagnostic.Severity);
        Assert.Contains("future.frame", protocolDiagnostic.Message, StringComparison.Ordinal);

        using var agentReadState = JsonDocument.Parse(File.ReadAllText(WikiAgentReadStore.StatePathFor(
            Path.Combine(_watchPath, "docs"), "concepts/remote-runner.md")));
        var agentReads = agentReadState.RootElement;
        using var movedTask = JsonDocument.Parse(File.ReadAllText(Path.Combine(moved, "task.json")));
        var displayTaskKey = movedTask.RootElement.GetProperty("key").GetString();
        Assert.Equal(1, agentReads.GetProperty("total").GetInt32());
        Assert.Equal(displayTaskKey, agentReads.GetProperty("recent")[0].GetProperty("taskKey").GetString());

        var evidence = File.ReadAllText(Path.Combine(moved, "results", "runner-evidence--real.txt"));
        Assert.Equal("evidence bytes", evidence);

        var status = File.ReadAllText(Path.Combine(moved, "status.md"));
        Assert.Contains(ProjectName, status);
    }

    [Fact]
    public async Task Health_probe_reports_reachable_against_the_live_server()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);

        // The connectivity preflight hits the server's open /healthz route. A live
        // server returns a null reason (reachable); the runbook's readiness check
        // (agent-host --health-check) and the run preflight both branch on this.
        var reason = await client.ProbeHealthAsync(CancellationToken.None);

        Assert.Null(reason);
    }

    [Fact]
    public async Task Recognized_remote_task_done_uses_regular_runner_completion_not_external_completion()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        SeedTask(TaskStates.Progress, TaskKey, "Remote done", "Make a trivial change.");
        File.Delete(Path.Combine(_watchPath, TaskStates.Progress, TaskKey, "status.md"));

        var summaryOneShot = new StubSummaryOneShot(false, true);
        using var factory = BuildFactory(summaryOneShot: summaryOneShot);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);

        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        await client.IngestLogsAsync(new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "Implemented and verified."),
            new RCliLine(DateTime.UtcNow, "stdout", "[[TASK_DONE]]"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-done-logs"), ct);

        var artifactUpload = await client.UploadArtifactsAsync(new RArtifactIngest(TaskKey,
        [
            new RArtifact(
                "deliverables.md",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("# Remote deliverables\n"))),
            new RArtifact(
                "nested/proof.txt",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("remote proof"))),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-done-artifacts",
            FinalizeResult: true), ct);
        Assert.NotNull(artifactUpload);
        Assert.Equal(2, artifactUpload!.Uploaded);
        Assert.True(artifactUpload.ResultDocumentGenerated, artifactUpload.ResultDocumentStatus);
        Assert.Equal("generated", artifactUpload.ResultDocumentStatus);
        Assert.Equal(2, summaryOneShot.Calls);
        Assert.Equal(
            ["results/deliverables.md", "results/nested/proof.txt"],
            artifactUpload.Files);
        var activeFolder = Assert.Single(Directory.GetDirectories(
            _watchPath, TaskKey, SearchOption.AllDirectories));
        var progressStatus = File.ReadAllText(Path.Combine(activeFolder, "status.md"));
        Assert.Contains("Done and verified by the remote result fixture.", progressStatus);
        Assert.DoesNotContain(TaskTransitionService.ResultScaffoldMarker, progressStatus);

        var completionRequest = new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            ExitCode: 0,
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-done-completion",
            BaseSha: "4136f00d4136f00d4136f00d4136f00d4136f00d",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!,
                lease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            IntegrationBranch: "refs/heads/main");

        var missingAuthority = await http.PostAsJsonAsync(
            "/api/runner/completion", completionRequest with { AttemptId = null }, ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, missingAuthority.StatusCode);
        var wrongLease = await http.PostAsJsonAsync(
            "/api/runner/completion", completionRequest with
            {
                LeaseId = "lease-from-another-holder",
                AttemptChainId = "lease-from-another-holder",
                IdempotencyKey = "remote-done-wrong-lease",
            }, ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, wrongLease.StatusCode);
        var wrongLeaseResult = await wrongLease.Content.ReadFromJsonAsync<RRemoteCompletionResponse>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.StaleFence.ToString(), wrongLeaseResult!.FailureClassification);

        var salvageIsNotResultAuthority = await http.PostAsJsonAsync(
            "/api/runner/completion", completionRequest with
            {
                ResultSha = null,
                SalvageCommitSha = "salvage-is-evidence-only",
                IdempotencyKey = "remote-done-missing-result-sha",
            }, ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, salvageIsNotResultAuthority.StatusCode);
        var missingResult = await salvageIsNotResultAuthority.Content.ReadFromJsonAsync<RRemoteCompletionResponse>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.Invalid.ToString(), missingResult!.FailureClassification);

        var completion = await client.CompleteRunAsync(completionRequest, ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        Assert.Equal(lease.Lease.AttemptId, completion.RunAttemptId);
        Assert.False(string.IsNullOrWhiteSpace(completion.ReviewAttemptId));
        Assert.False(string.IsNullOrWhiteSpace(completion.ReviewSubjectId));

        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, ct);
        Assert.Equal(resultSha, projection!.CurrentRunAttempt!.ResultSha);
        Assert.Equal(resultSha, projection.CurrentReviewSubject!.ExpectedResultSha);

        var duplicate = await client.CompleteRunAsync(completionRequest, ct);
        Assert.Equal(completion.ReviewAttemptId, duplicate!.ReviewAttemptId);
        Assert.Equal("duplicate delivery", duplicate.Message);

        var claimReviewResponse = await http.PostAsJsonAsync(
            $"/api/attempts/reviews/{completion.ReviewAttemptId}/claim",
            new ClaimReviewAttemptRequest(completion.ReviewAttemptId!, "review-worker", "review-host", "claim-review", 60), ct);
        claimReviewResponse.EnsureSuccessStatusCode();
        var claimReview = await claimReviewResponse.Content.ReadFromJsonAsync<AttemptWriteResult>(ApiJson, ct);
        Assert.NotNull(claimReview?.ReviewAttempt);

        var reviewWrite = new AttemptWriteReference(
            completion.ReviewAttemptId!,
            claimReview!.ReviewAttempt!.LastFence,
            claimReview.ReviewAttempt.AuthorityEpoch,
            "settle-review-mismatched-sha");
        var mismatchResponse = await http.PostAsJsonAsync(
            $"/api/attempts/reviews/{completion.ReviewAttemptId}/settle",
            new SettleReviewAttemptRequest(reviewWrite, "61306343", ReviewTerminalOutcome.Pass), ct);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, mismatchResponse.StatusCode);
        var mismatch = await mismatchResponse.Content.ReadFromJsonAsync<AttemptWriteResult>(ApiJson, ct);
        Assert.Equal(AttemptWriteStatus.SubjectMismatch, mismatch!.Status);
        Assert.Equal("immutable-result-mismatch", mismatch.ReviewAttempt!.FailureClassification);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, mismatch.ReviewAttempt.Outcome);

        var retryResponse = await http.PostAsJsonAsync(
            "/api/attempts/reviews",
            new CreateReviewAttemptRequest(
                TaskKey,
                mismatch.ReviewAttempt.RepositoryId,
                mismatch.ReviewAttempt.Subject.ExpectedResultSha,
                mismatch.ReviewAttempt.SourceRunAttemptId,
                mismatch.ReviewAttempt.Subject.TaskRequirementsHash,
                mismatch.ReviewAttempt.Subject.ReviewPolicyHash,
                mismatch.ReviewAttempt.Subject.EvidenceDigestInputs,
                "retry-review-same-subject",
                mismatch.ReviewAttempt.AttemptId), ct);
        retryResponse.EnsureSuccessStatusCode();
        var retry = await retryResponse.Content.ReadFromJsonAsync<AttemptWriteResult>(ApiJson, ct);
        Assert.NotEqual(mismatch.ReviewAttempt.AttemptId, retry!.ReviewAttempt!.AttemptId);
        Assert.Equal(mismatch.ReviewAttempt.Subject.SubjectId, retry.ReviewAttempt.Subject.SubjectId);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        Assert.True(Directory.Exists(moved));
        Assert.Equal(
            lease.Lease.AttemptId,
            ReviewSubjectStore.Read(moved)!.RunAttemptId);
        Assert.Equal(
            "refs/heads/main",
            ReviewSubjectStore.Read(moved)!.IntegrationBranch);

        var taskJson = File.ReadAllText(Path.Combine(moved, "task.json"));
        using (var completedJson = JsonDocument.Parse(taskJson))
        {
            Assert.Equal(
                "refs/heads/main",
                completedJson.RootElement.GetProperty("integrationBranch").GetString());
        }
        Assert.DoesNotContain("externalCompletion", taskJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LifecyclePhases.PostProcessingRunning, taskJson, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(moved, "lifecycle.json")));
        var timeline = File.ReadAllText(Path.Combine(moved, "logs", "timeline.jsonl"));
        Assert.Contains("agent_run_finished", timeline);
        Assert.Contains("resultSha", timeline);
        Assert.Contains("idempotencyKey", timeline);
        Assert.Contains($"\"runId\":\"{lease.Lease.AttemptId}\"", timeline, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"lane-completion:remote-done-completion\"", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("external_completion", timeline);
        Assert.Equal(1, timeline.Split("agent_run_finished", StringSplitOptions.None).Length - 1);
        var status = File.ReadAllText(Path.Combine(moved, "status.md"));
        Assert.Contains("Done and verified by the remote result fixture.", status);
        Assert.DoesNotContain(TaskTransitionService.ResultScaffoldMarker, status);
        Assert.Equal(
            "# Remote deliverables\n",
            File.ReadAllText(Path.Combine(moved, "results", "deliverables.md")));
        Assert.Equal(
            "remote proof",
            File.ReadAllText(Path.Combine(moved, "results", "nested", "proof.txt")));
        Assert.Equal(
            0,
            factory.Services.GetRequiredService<TaskTransitionService>().ResultScaffoldCreatedCount);
    }

    [Fact]
    public async Task In_repo_main_delivery_projects_receive_commits_tokens_and_sha_bound_test_evidence()
    {
        const string teKey = "TE-41";
        var origin = await SeedOriginAsync();
        var repo = Path.Combine(_workspace, "token-economy-repo");
        await GitAsync(_workspace, "clone", origin, repo);
        // -B, not -b: the clone already checked out a local "main" (the bare
        // origin's HEAD points at it), so creating it again must not fail.
        await GitAsync(repo, "checkout", "-B", "main", "origin/main");
        await GitAsync(repo, "config", "user.email", "test@example.invalid");
        await GitAsync(repo, "config", "user.name", "Test");
        var baseSha = (await GitAsync(repo, "rev-parse", "HEAD")).StdOut.Trim();
        await File.WriteAllTextAsync(Path.Combine(repo, "release.txt"), "v0.3.2");
        await GitAsync(repo, "add", "--all");
        await GitAsync(repo, "commit", "-m", "fix(TE-41): publish v0.3.2");
        var resultSha = (await GitAsync(repo, "rev-parse", "HEAD")).StdOut.Trim();
        // Reproduce TE: the task itself publishes main before completion.
        await GitAsync(repo, "push", "origin", "HEAD:main");

        var teWatchPath = Path.Combine(repo, ".orchestrator", "jobs");
        var progressFolder = Path.Combine(teWatchPath, "tasks", "000", teKey);
        Directory.CreateDirectory(progressFolder);
        await File.WriteAllTextAsync(Path.Combine(progressFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = teKey,
            key = teKey,
            title = "Release token economy",
            state = TaskStates.Progress,
            order = 1,
            agent = "codex",
            kind = TaskKinds.Task,
            cliType = "codex",
            model = "gpt-5.6-codex",
            provenance = new
            {
                branch = "task/TE-41",
                @base = (string?)null,
                transitions = new[]
                {
                    new
                    {
                        lane = TaskStates.Progress,
                        atUtc = DateTime.UtcNow,
                        workBranchHead = baseSha,
                    },
                },
            },
        }));
        await File.WriteAllTextAsync(Path.Combine(progressFolder, "prompt.md"), "Release and verify.");
        await File.WriteAllTextAsync(Path.Combine(progressFolder, "status.md"), "Result: pending.");

        using var factory = BuildFactory(
            repositoryPath: repo,
            primaryProjectName: "Token Economy",
            primaryWatchPath: teWatchPath);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync("Token Economy", "service", CancellationToken.None);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(teKey, RunnerId, "Token Economy", "hetzner-test", 4242, "codex"),
            CancellationToken.None);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        var immutableRef = Contract.FencedGitRefs.ImmutableResult(
            lease.Lease!.AttemptId!,
            lease.Lease.FencingToken,
            resultSha);
        await GitAsync(repo, "push", "origin", $"{resultSha}:{immutableRef}");
        var inspectedRange = factory.Services.GetRequiredService<GitService>()
            .InspectRemoteDeliveryCommitRange(
                repo,
                immutableRef,
                resultSha,
                "refs/heads/main",
                baseSha);
        Assert.True(inspectedRange.Success, inspectedRange.Warning);
        Assert.Equal(resultSha, Assert.Single(inspectedRange.Commits).Sha);
        var usageFrame = JsonSerializer.Serialize(new
        {
            type = "turn.completed",
            usage = new
            {
                input_tokens = 1200,
                cached_input_tokens = 800,
                output_tokens = 300,
                reasoning_output_tokens = 100,
            },
        });
        await client.IngestLogsAsync(new RLogIngest(teKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", usageFrame),
            new RCliLine(DateTime.UtcNow, "stdout", "[[TASK_DONE]]"),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "te-41-log"), CancellationToken.None);

        var postSteps = Path.Combine(progressFolder, "post-steps");
        Directory.CreateDirectory(postSteps);
        await File.WriteAllTextAsync(
            Path.Combine(postSteps, "build-test-gate-1.log"),
            "verdict=Ok exit=0 signal=n/a durationMs=1000\n"
            + "gateId=post-build-test-gate failureKind=None failureFingerprint=n/a\n"
            + "gateRunId=te-gate startedAtUtc=2026-08-11T14:30:00Z completedAtUtc=2026-08-11T14:31:00Z\n"
            + $"repository=token-economy expectedSha={resultSha} testedSha={resultSha}\n"
            + "reason=All selected commands passed.\n");

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            teKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: "Token Economy",
            ExitCode: 0,
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: origin,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "te-41-completion",
            BaseSha: baseSha,
            ImmutableResultRef: immutableRef,
            ArtifactManifestDigest: new string('a', 64),
            IntegrationBranch: "refs/heads/main"), CancellationToken.None);

        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var moved = progressFolder;
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var task = scanner.FindJob(teKey, teWatchPath);
        Assert.NotNull(task);
        Assert.Equal(resultSha, Assert.Single(task!.Commits).Sha);

        using var taskJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(moved, "task.json")));
        var tokenSummary = taskJson.RootElement.GetProperty("tokenSummary");
        Assert.Equal(1, tokenSummary.GetProperty("Calls").GetInt32());
        Assert.Equal(1200, tokenSummary.GetProperty("InputTokens").GetInt64());
        Assert.Equal(300, tokenSummary.GetProperty("OutputTokens").GetInt64());
        Assert.Equal(800, tokenSummary.GetProperty("CacheReadTokens").GetInt64());

        var evidence = factory.Services
            .GetRequiredService<TestRunService>()
            .BuildLookup([task])[task.TaskKey];
        Assert.Equal("proven", evidence.EvidenceState);
        Assert.Equal("perfect", evidence.MatchQuality);
        Assert.Equal(resultSha, Assert.Single(evidence.Sources).Commit);

        // Reproduce the historical TE-41 card, then prove the bounded boot
        // sweep restores both receipts from its fenced review subject and log.
        var jsonPath = Path.Combine(moved, "task.json");
        var historical = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!.AsObject();
        historical["commits"] = new JsonArray();
        historical["commit"] = null;
        historical["tokenSummary"] = null;
        await File.WriteAllTextAsync(jsonPath, historical.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        var legacySubject = ReviewSubjectStore.Read(moved);
        Assert.NotNull(legacySubject);
        ReviewSubjectStore.Write(moved, legacySubject! with
        {
            Version = 2,
            BaseSha = null,
        });
        scanner.InvalidateCache();
        var historicalTask = scanner.FindJob(teKey, teWatchPath);
        Assert.Contains(baseSha, RemoteCompletionAttributionSweep.BaseCandidates(
            historicalTask!,
            ReviewSubjectStore.Read(moved)!));
        var sweep = new RemoteCompletionAttributionSweep(
            scanner,
            factory.Services.GetRequiredService<GitService>(),
            factory.Services.GetRequiredService<TaskMutationService>(),
            factory.Services.GetRequiredService<RemoteTokenReceiptService>(),
            Path.Combine(_workspace, "te-backfill-report.json"),
            DateTimeOffset.UtcNow,
            NullLogger<RemoteCompletionAttributionSweep>.Instance);

        var report = sweep.RunOnce();

        Assert.Equal(1, report.RepairedCommitTasks);
        Assert.Equal(1, report.RepairedTokenTasks);
        Assert.Equal(0, report.UnresolvedTasks);
        var repaired = scanner.FindJob(teKey, teWatchPath);
        Assert.Equal(resultSha, Assert.Single(repaired!.Commits).Sha);
        using var repairedJson = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(2300, repairedJson.RootElement
            .GetProperty("tokenSummary")
            .GetProperty("TotalTokens")
            .GetInt64());
        var repairedEvidence = factory.Services
            .GetRequiredService<TestRunService>()
            .BuildLookup([repaired])[repaired.TaskKey];
        Assert.Equal("proven", repairedEvidence.EvidenceState);
    }

    [Fact]
    public async Task Remote_result_finalization_exhaustion_is_typed_degraded_before_scaffold_fallback()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        SeedTask(TaskStates.Progress, TaskKey, "Remote summary gap", "Make a trivial change.");
        File.Delete(Path.Combine(_watchPath, TaskStates.Progress, TaskKey, "status.md"));

        var summaryOneShot = new StubSummaryOneShot(false);
        using var factory = BuildFactory(summaryOneShot: summaryOneShot);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        await client.IngestLogsAsync(new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "Implemented and verified."),
            new RCliLine(DateTime.UtcNow, "stdout", "[[TASK_DONE]]"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-missing-summary-logs"), ct);
        var artifactUpload = await client.UploadArtifactsAsync(new RArtifactIngest(TaskKey,
        [
            new RArtifact(
                "proof.txt",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("remote proof"))),
        ],
            RunnerId: lease.Lease.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-missing-summary-artifacts",
            FinalizeResult: true), ct);
        Assert.NotNull(artifactUpload);
        Assert.False(artifactUpload!.ResultDocumentGenerated);
        Assert.StartsWith("degraded:", artifactUpload.ResultDocumentStatus);
        Assert.Contains("summary fixture failed", artifactUpload.ResultDocumentStatus);
        Assert.Equal(3, summaryOneShot.Calls);
        var canonicalTaskKey = Assert.Single(factory.Services
            .GetRequiredService<ITaskScanner>()
            .ScanAllJobs(), item => item.Id == TaskKey).TaskKey;
        var summaryState = factory.Services
            .GetRequiredService<SummaryGenerationService>()
            .GetState(canonicalTaskKey);
        Assert.NotNull(summaryState);
        Assert.Equal(TaskSummaryStatus.Degraded, summaryState.Status);
        Assert.Equal(3, summaryState.Attempt);
        Assert.Equal(3, summaryState.MaxAttempts);
        var activeFolder = Assert.Single(Directory.GetDirectories(
            _watchPath, TaskKey, SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(activeFolder, "status.md")));

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            ExitCode: 0,
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-missing-summary-completion",
            BaseSha: "4136f00d4136f00d4136f00d4136f00d4136f00d",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!,
                lease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            IntegrationBranch: "refs/heads/main"), ct);

        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        var status = File.ReadAllText(Path.Combine(moved, "status.md"));
        Assert.Contains(TaskTransitionService.ResultScaffoldMarker, status);
        Assert.Equal("remote proof", File.ReadAllText(Path.Combine(moved, "results", "proof.txt")));
        Assert.Equal(
            1,
            factory.Services.GetRequiredService<TaskTransitionService>().ResultScaffoldCreatedCount);
    }

    [Fact]
    public async Task Coding_done_without_base_sha_requeues_with_the_salvage_fence_before_review_is_created()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string fenceBranch = "agent-studio/salvage/runner-e2e/AGT-RUNNER-E2E/run-1/fence-1/589c462f";
        SeedTask(TaskStates.Progress, TaskKey, "Remote done without envelope", "Make a trivial change.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);

        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            ExitCode: 0,
            SalvageBranch: fenceBranch,
            SalvageCommitSha: resultSha,
            SalvageBranchUrl: "https://example.invalid/fence/run-1",
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "remote-done-without-base-sha",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!,
                lease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ct);

        Assert.NotNull(completion);
        Assert.Equal("delivery-failed", completion!.Outcome);
        Assert.Equal(TaskStates.Ready, completion.TargetState);
        Assert.Contains("BaseSha", completion.Message, StringComparison.Ordinal);

        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, ct);
        Assert.NotNull(projection);
        Assert.Equal(AttemptLifecycleState.Failed, projection.CurrentRunAttempt!.State);
        Assert.Equal("delivery-failed", projection.CurrentRunAttempt.TerminalOutcome);
        Assert.Contains("result envelope", projection.CurrentRunAttempt.TerminalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(resultSha, projection.CurrentRunAttempt.ResultSha);
        Assert.Null(projection.CurrentRunAttempt.ResultEnvelope);
        Assert.Null(projection.CurrentReviewSubject);
        Assert.Empty(projection.ReviewAttempts);

        var ready = Path.Combine(_watchPath, TaskStates.Ready, TaskKey);
        Assert.True(Directory.Exists(ready));
        var status = File.ReadAllText(Path.Combine(ready, "status.md"));
        Assert.Contains("Delivery status: `delivery-failed`", status);
        Assert.Contains("Envelope attempt: 1/2", status);
        Assert.Contains(fenceBranch, status);
        Assert.Contains("automatically requeued to `2-ready`", status);
        Assert.DoesNotContain("cannot be materialized", status, StringComparison.OrdinalIgnoreCase);
        var retryPrompt = File.ReadAllText(Path.Combine(ready, "prompt.md"));
        Assert.Contains("Automatic remote delivery retry", retryPrompt);
        Assert.Contains(fenceBranch, retryPrompt);
        Assert.Contains("BaseSha", retryPrompt);
        using var taskJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(ready, "task.json")));
        var failureState = taskJson.RootElement.GetProperty("remoteDeliveryFailure");
        Assert.Equal("delivery-failed", failureState.GetProperty("status").GetString());
        Assert.Equal(1, failureState.GetProperty("consecutiveAttempts").GetInt32());
        Assert.Equal(fenceBranch, failureState.GetProperty("fenceBranch").GetString());
        var timeline = File.ReadAllText(Path.Combine(ready, "logs", "timeline.jsonl"));
        Assert.Contains("\"status\":\"delivery-failed\"", timeline);
        Assert.Contains("\"deliveryAction\":\"requeue\"", timeline);
        Assert.DoesNotContain("\"status\":\"done\"", timeline);
        Assert.Empty(factory.Services.GetRequiredService<AttemptAuthorityService>()
            .TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());
    }

    [Fact]
    public async Task Second_consecutive_envelope_failure_escalates_as_unverified_delivery()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string firstFence = "agent-studio/salvage/runner-e2e/AGT-RUNNER-E2E/run-1/fence-1/589c462f";
        const string secondFence = "agent-studio/salvage/runner-e2e/AGT-RUNNER-E2E/run-2/fence-2/589c462f";
        SeedTask(TaskStates.Progress, TaskKey, "Repeated envelope failure", "Make a trivial change.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);

        var firstLease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"),
            CancellationToken.None);
        Assert.True(firstLease.Granted);
        var first = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            firstLease.Lease!.LeaseId,
            firstLease.Lease.FencingToken,
            RunnerId,
            "Blocked",
            Reason: "Stable still runs the pre-fix binary.",
            Source: ProjectName,
            SalvageBranch: firstFence,
            SalvageCommitSha: resultSha,
            ResultSha: resultSha,
            AttemptChainId: firstLease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: firstLease.Lease.AttemptId,
            AuthorityEpoch: firstLease.Lease.AuthorityEpoch,
            IdempotencyKey: "first-envelope-failure",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                firstLease.Lease.AttemptId!,
                firstLease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CancellationToken.None);
        Assert.Equal(TaskStates.Ready, first!.TargetState);

        var release = await client.ReleaseLeaseAsync(new RRelease(
            TaskKey,
            firstLease.Lease.LeaseId,
            firstLease.Lease.FencingToken,
            RunnerId,
            firstLease.Lease.AttemptId,
            firstLease.Lease.AuthorityEpoch,
            "release-after-first-envelope-failure"), CancellationToken.None);
        Assert.Equal("Released", release.Outcome);

        var secondLease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"),
            CancellationToken.None);
        Assert.True(secondLease.Granted);
        var moved = await factory.Services.GetRequiredService<TaskTransitionService>().MoveAsync(
            TaskKey,
            TaskStates.Progress,
            _watchPath,
            CancellationToken.None,
            cause: "remote-runner:test-second-envelope-attempt",
            authorityWrite: new AttemptWriteReference(
                secondLease.Lease!.AttemptId!,
                secondLease.Lease.FencingToken,
                secondLease.Lease.AuthorityEpoch,
                "lane-claim:second-envelope-attempt"),
            suppressProductExecution: true);
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        var second = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            secondLease.Lease.LeaseId,
            secondLease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            SalvageBranch: secondFence,
            SalvageCommitSha: resultSha,
            ResultSha: resultSha,
            AttemptChainId: secondLease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: secondLease.Lease.AttemptId,
            AuthorityEpoch: secondLease.Lease.AuthorityEpoch,
            IdempotencyKey: "second-envelope-failure",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                secondLease.Lease.AttemptId!,
                secondLease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal("delivery-failed", second!.Outcome);
        Assert.Equal(TaskStates.Escalated, second.TargetState);
        var escalated = Path.Combine(_watchPath, TaskStates.Escalated, TaskKey);
        var status = File.ReadAllText(Path.Combine(escalated, "status.md"));
        Assert.Contains(firstFence, status);
        Assert.Contains(secondFence, status);
        Assert.Contains("Envelope attempt: 2/2", status);
        Assert.Contains("category `unverified-delivery`", status);
        using var taskJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(escalated, "task.json")));
        Assert.Equal(
            2,
            taskJson.RootElement
                .GetProperty("remoteDeliveryFailure")
                .GetProperty("consecutiveAttempts")
                .GetInt32());
        var decision = Assert.Single(ReviewDecisionLog.ReadAll(_workspace, ProjectName));
        Assert.Equal(ReviewDecisionKind.Escalate, decision.Kind);
        Assert.Contains("[unverified-delivery]", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_blocked_without_reason_goes_to_escalated_with_a_stated_diagnostic()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string baseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
        SeedTask(TaskStates.Progress, TaskKey, "Remote blocked", "Try the requested operation.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"),
            CancellationToken.None);
        Assert.True(lease.Granted);

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Blocked",
            Reason: null,
            Source: ProjectName,
            ResultSha: resultSha,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "blocked-without-reason",
            BaseSha: baseSha,
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!,
                lease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), CancellationToken.None);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.Escalated, completion!.TargetState);
        Assert.Contains("reported that it could not continue", completion.Message, StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine(_watchPath, TaskStates.Escalated, TaskKey);
        Assert.True(Directory.Exists(folder));
        var status = File.ReadAllText(Path.Combine(folder, "status.md"));
        Assert.Contains("agent-blocked", status);
        Assert.Contains("reported that it could not continue", status, StringComparison.OrdinalIgnoreCase);
        var decision = Assert.Single(ReviewDecisionLog.ReadAll(_workspace, ProjectName));
        Assert.Equal(ReviewDecisionKind.Escalate, decision.Kind);
        Assert.Contains("[agent-blocked]", decision.Reason);
    }

    [Fact]
    public async Task Remote_claim_environment_failure_retries_twice_then_escalates_with_last_reason()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Clone failure budget", "Implement the task.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/website.git");

        for (var attempt = 1; attempt <= RemoteClaimFailureBudget.MaxAttempts; attempt++)
        {
            var request = new RClaim(
                RunnerId,
                ProjectName,
                "hetzner-test",
                4242,
                "remote-runner",
                IdempotencyKey: $"environment-claim-{attempt}");
            var claim = attempt == 1
                ? await ClaimWithSuccessfulPreflightAsync(client, request)
                : await client.ClaimAsync(request, CancellationToken.None);
            Assert.Equal(RClaimStatus.Claimed, claim.Status);

            var completion = await client.CompleteRunAsync(new RRemoteComplete(
                claim.TaskKey!,
                claim.Lease!.LeaseId,
                claim.Lease.FencingToken,
                RunnerId,
                "EnvironmentFailure",
                Reason: "clone failed: 403 agent-orc/website",
                Source: ProjectName,
                AttemptId: claim.Lease.AttemptId,
                AuthorityEpoch: claim.Lease.AuthorityEpoch,
                IdempotencyKey: $"environment-completion-{attempt}"), CancellationToken.None);
            await client.ReleaseLeaseAsync(new RRelease(
                claim.TaskKey!,
                claim.Lease.LeaseId,
                claim.Lease.FencingToken,
                RunnerId,
                claim.Lease.AttemptId,
                claim.Lease.AuthorityEpoch,
                $"environment-release-{attempt}"), CancellationToken.None);

            var expectedState = attempt < RemoteClaimFailureBudget.MaxAttempts
                ? TaskStates.Ready
                : TaskStates.Escalated;
            Assert.Equal(expectedState, completion!.TargetState);
            Assert.True(Directory.Exists(Path.Combine(_watchPath, expectedState, TaskKey)));
        }

        var escalated = Path.Combine(_watchPath, TaskStates.Escalated, TaskKey);
        var status = File.ReadAllText(Path.Combine(escalated, "status.md"));
        Assert.Contains("remote-claim-environment", status);
        Assert.Contains("3/3", status);
        Assert.Contains("clone failed: 403 agent-orc/website", status);

        var noFourthClaim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"),
            CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, noFourthClaim.Status);
    }

    [Fact]
    public async Task Remote_runner_reports_clone_failure_instead_of_releasing_an_unexplained_claim()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Clone failure handoff", "Implement the task.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/website.git");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"));

        var options = RunnerOptions("unused-after-clone-failure");
        var logs = new List<string>();
        var runner = new RTaskRunner(options, client, logs.Add);
        var missingOrigin = Path.Combine(_workspace, "missing-origin.git");

        var exit = await runner.RunClaimedAsync(
            claim.TaskKey!,
            claim.Lease!,
            CancellationToken.None,
            claim.ProjectId,
            missingOrigin,
            "main",
            claim.TaskKind);

        Assert.Equal(1, exit);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
        Assert.Contains(logs, line =>
            line.Contains("EnvironmentFailure", StringComparison.OrdinalIgnoreCase)
            || line.Contains("clone failed", StringComparison.OrdinalIgnoreCase));
        var taskJson = File.ReadAllText(Path.Combine(_watchPath, TaskStates.Ready, TaskKey, "task.json"));
        Assert.Contains("\"attempts\": 1", taskJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remote_runner_counts_unstartable_cli_as_pre_agent_environment_failure()
    {
        SeedTask(TaskStates.Ready, TaskKey, "CLI environment failure", "Implement the task.");
        var origin = await SeedOriginAsync();

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/cli-environment.git");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"));

        var logs = new List<string>();
        var runner = new RTaskRunner(
            RunnerOptions(Path.Combine(_workspace, "missing-cli")),
            client,
            logs.Add);

        var exit = await runner.RunClaimedAsync(
            claim.TaskKey!,
            claim.Lease!,
            CancellationToken.None,
            claim.ProjectId,
            origin,
            "main",
            claim.TaskKind);

        Assert.Equal(1, exit);
        var readyFolder = Path.Combine(_watchPath, TaskStates.Ready, TaskKey);
        Assert.True(
            Directory.Exists(readyFolder),
            $"Expected Ready after CLI launch failure. Existing lanes: " +
            $"{string.Join(", ", Directory.EnumerateDirectories(_watchPath).Select(Path.GetFileName))}. " +
            $"Runner log: {string.Join(" | ", logs)}");
        var taskJson = File.ReadAllText(Path.Combine(readyFolder, "task.json"));
        Assert.Contains("\"attempts\": 1", taskJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("environment preparation failed", taskJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, line =>
            line.Contains("EnvironmentFailure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Remote_done_without_fenced_result_sha_fails_closed_before_review()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote done", "Make a trivial change.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var error = await Assert.ThrowsAsync<Runner::AgentRunner.TaskServerException>(() =>
            client.CompleteRunAsync(new RRemoteComplete(
                TaskKey,
                lease.Lease!.LeaseId,
                lease.Lease.FencingToken,
                RunnerId,
                "Done",
                Source: ProjectName,
                ExitCode: 0,
                AttemptId: lease.Lease!.AttemptId,
                AuthorityEpoch: lease.Lease.AuthorityEpoch,
                IdempotencyKey: "missing-result"), ct));

        Assert.Equal(400, error.StatusCode);
        var retainedTask = Assert.Single(Directory.GetDirectories(
            _watchPath, TaskKey, SearchOption.AllDirectories));
        Assert.NotEqual(TaskStates.AutoReview, Directory.GetParent(retainedTask)!.Name);
        Assert.False(File.Exists(ReviewSubjectStore.PathFor(retainedTask)));
    }

    [Fact]
    public async Task Failed_artifact_write_can_retry_the_same_idempotency_key()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote artifact retry", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var invalid = new RArtifactIngest(TaskKey,
        [
            new RArtifact("retry.txt", "not-base64"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "artifact-retry-1");
        var invalidResponse = await http.PostAsJsonAsync("/api/runner/artifacts", invalid, ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var corrected = invalid with
        {
            Artifacts = [new RArtifact(
                "retry.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes("durable evidence")))],
        };
        var retryResponse = await http.PostAsJsonAsync("/api/runner/artifacts", corrected, ct);
        retryResponse.EnsureSuccessStatusCode();
        var retry = await retryResponse.Content.ReadFromJsonAsync<ArtifactIngestResponse>(ApiJson, ct);

        Assert.Equal(1, retry!.Uploaded);
        var artifactPath = Assert.Single(Directory.GetFiles(
            _watchPath, "retry.txt", SearchOption.AllDirectories));
        Assert.Equal("durable evidence", File.ReadAllText(artifactPath));
    }

    [Fact]
    public async Task Log_delivery_retry_after_authority_persist_failure_does_not_append_twice()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote log crash window", "Prompt.");
        var writer = new ControllableAtomicJsonFileWriter();

        using var factory = BuildFactory(writer);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var failNextAuthorityPersist = true;
        writer.ShouldFail = (path, _) =>
        {
            if (!path.EndsWith(AttemptAuthorityService.RelativePath, StringComparison.Ordinal)
                || !failNextAuthorityPersist)
            {
                return false;
            }
            failNextAuthorityPersist = false;
            return true;
        };
        var delivery = new RLogIngest(TaskKey,
        [
            new RCliLine(DateTime.UtcNow, "stdout", "crash-window-line"),
        ],
            RunnerId: lease.Lease!.RunnerId,
            LeaseId: lease.Lease.LeaseId,
            FencingToken: lease.Lease.FencingToken,
            AttemptId: lease.Lease.AttemptId,
            Fence: lease.Lease.FencingToken,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "log-crash-window-1");

        var failed = await http.PostAsJsonAsync("/api/runner/logs", delivery, ct);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failed.StatusCode);

        var retry = await http.PostAsJsonAsync("/api/runner/logs", delivery, ct);
        retry.EnsureSuccessStatusCode();

        var logPath = Assert.Single(Directory.GetFiles(
            _watchPath, "cli-output.log", SearchOption.AllDirectories));
        var occurrences = File.ReadLines(logPath).Count(line => line.Contains("crash-window-line", StringComparison.Ordinal));
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task Fenced_worktree_delivery_failure_routes_to_escalated_error_with_recovery_coordinates()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote cleanup failure", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        const string gate =
            "worktree-blocked: host=runner-host; worktree=/runner/worktrees/AGT-100; " +
            "branch=runner/runner-host/AGT-100; failure=registered repository ref missing. " +
            "Recovery recipe: publish the retained HEAD to the registered repository, then requeue.";
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Blocked",
            Reason: "salvage push failed",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "cleanup-blocked-1",
            GateItems: [gate]), ct);

        Assert.Equal(TaskStates.Escalated, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.Escalated, TaskKey);
        var followUp = File.ReadAllText(Path.Combine(moved, "orchestrator-follow-up.md"));
        Assert.Contains(gate, followUp);
        Assert.Contains("host=runner-host", followUp);
        Assert.Contains("worktree=/runner/worktrees/AGT-100", followUp);
        Assert.Contains("branch=runner/runner-host/AGT-100", followUp);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey)));
        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, ct);
        Assert.Equal(AttemptLifecycleState.Failed, projection!.CurrentRunAttempt!.State);
        Assert.Null(projection.CurrentReviewAttempt);
    }

    [Fact]
    public async Task Runner_completion_records_divergent_salvage_refs_and_shas_as_operator_evidence()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote salvage collision", "Continue safely.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        const string canonical = "runner/agent-runner-e2e/AGT-RUNNER-E2E";
        const string canonicalSha = "09faf1b709faf1b709faf1b709faf1b709faf1b7";
        const string localSha = "b6f23a3fb6f23a3fb6f23a3fb6f23a3fb6f23a3f";
        const string recovery = canonical + "-collision-" + localSha + "-" + canonicalSha;
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            SalvageBranch: canonical,
            SalvageCommitSha: canonicalSha,
            ResultSha: localSha,
            AttemptChainId: lease.Lease.LeaseId,
            SalvageResolution: "divergent",
            SalvageLocalCommitSha: localSha,
            SalvageRecoveryBranch: recovery,
            SalvageRecoveryCommitSha: localSha,
            SalvageAuthoritativeBaseBranch: canonical,
            SalvageAuthoritativeBaseSha: canonicalSha,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "salvage-collision-completion",
            BaseSha: "4136f00d4136f00d4136f00d4136f00d4136f00d",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!,
                lease.Lease.FencingToken,
                localSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        var timeline = File.ReadAllText(Path.Combine(moved, "logs", "timeline.jsonl"));
        Assert.Contains($"\"salvageBranch\":\"{canonical}\"", timeline);
        Assert.Contains($"\"salvageCommitSha\":\"{canonicalSha}\"", timeline);
        Assert.Contains("\"salvageResolution\":\"divergent\"", timeline);
        Assert.Contains($"\"salvageLocalCommitSha\":\"{localSha}\"", timeline);
        Assert.Contains($"\"salvageRecoveryBranch\":\"{recovery}\"", timeline);
        Assert.Contains($"\"salvageAuthoritativeBaseBranch\":\"{canonical}\"", timeline);
        Assert.Contains($"\"salvageAuthoritativeBaseSha\":\"{canonicalSha}\"", timeline);
        var deliverables = File.ReadAllText(Path.Combine(moved, "results", "deliverables.md"));
        Assert.Contains($"`{canonical}` at `{canonicalSha}`", deliverables);
        Assert.Contains($"`{recovery}` at `{localSha}`", deliverables);
        Assert.Contains($"`{canonical}` at `{canonicalSha}` was the authoritative pickup base", deliverables);
    }

    /// <summary>
    /// AGT-2494 - the canonical branch of a divergent salvage keeps the remote
    /// tip it collided with, so naming it as the review ref pins the subject to
    /// a SHA that ref never held. That is what happened to AGT-2220 on 28.07.
    /// (<c>resultSha=f538f896</c> on a ref holding <c>744deb89</c>) and it ended
    /// as <c>immutable-result-mismatch</c> with an empty <c>commits[]</c>. The
    /// review subject must name a ref that carries the fenced result.
    /// </summary>
    [Fact]
    public async Task Divergent_completion_reviews_a_ref_that_carries_the_result_sha()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote salvage collision", "Continue safely.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        const string canonical = "runner/agent-runner-e2e/AGT-RUNNER-E2E";
        const string canonicalSha = "09faf1b709faf1b709faf1b709faf1b709faf1b7";
        const string localSha = "b6f23a3fb6f23a3fb6f23a3fb6f23a3fb6f23a3f";
        const string recovery = canonical + "-collision-" + localSha + "-" + canonicalSha;
        var immutableRef = Contract.FencedGitRefs.ImmutableResult(
            lease.Lease.AttemptId!, lease.Lease.FencingToken, localSha);
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            SalvageBranch: canonical,
            SalvageCommitSha: canonicalSha,
            ResultSha: localSha,
            AttemptChainId: lease.Lease.LeaseId,
            SalvageResolution: "divergent",
            SalvageLocalCommitSha: localSha,
            SalvageRecoveryBranch: recovery,
            SalvageRecoveryCommitSha: localSha,
            SalvageAuthoritativeBaseBranch: canonical,
            SalvageAuthoritativeBaseSha: canonicalSha,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "salvage-collision-review-ref",
            BaseSha: "4136f00d4136f00d4136f00d4136f00d4136f00d",
            ImmutableResultRef: immutableRef,
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ct);

        Assert.NotNull(completion);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        var subject = ReviewSubjectStore.Read(moved);
        Assert.NotNull(subject);
        Assert.Equal(localSha, subject!.ResultSha);
        // The reviewed ref carries the result: the fenced immutable ref is named
        // for this exact SHA. The canonical branch, which holds canonicalSha, is
        // never claimed as the subject.
        Assert.Equal(immutableRef, subject.ResultRef);
        Assert.Contains(localSha, subject.ResultRef!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(canonical, subject.ResultRef);
    }

    /// <summary>
    /// AGT-2494, against a real origin - the completion still reports its
    /// immutable result ref, but that ref is gone from the repository (retention
    /// GC), and the canonical branch holds the foreign tip the salvage collided
    /// with. The collision branch is the only ref that carries the fenced result,
    /// so the completion must review it there: no ShaMismatch, an attributed
    /// <c>commits[]</c>, and no escalation of a delivery that demonstrably exists.
    /// </summary>
    [Fact]
    public async Task Divergent_completion_reviews_the_collision_branch_when_the_immutable_ref_is_gone()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Remote salvage collision", "Continue safely.");

        const string canonical = "runner/agent-runner-e2e/AGT-RUNNER-E2E";
        var origin = Path.Combine(_workspace, "collision-origin.git");
        var seed = Path.Combine(_workspace, "collision-seed");
        await GitAsync(_workspace, "init", "--bare", "--initial-branch=main", origin);
        await GitAsync(_workspace, "init", seed);
        await GitAsync(seed, "config", "user.name", "Test");
        await GitAsync(seed, "config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(seed, "base.txt"), "base");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "commit", "-m", "chore: base");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        var baseSha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();

        // The canonical branch as origin holds it: a foreign tip the salvage
        // refused to overwrite.
        await GitAsync(seed, "checkout", "-b", canonical, baseSha);
        await File.WriteAllTextAsync(Path.Combine(seed, "foreign.txt"), "foreign");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "commit", "-m", "feat: foreign tip that won the canonical ref");
        var canonicalSha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
        await GitAsync(seed, "push", "origin", canonical);

        // This run's own result, parked on the collision branch.
        await GitAsync(seed, "checkout", "-b", "salvage-work", baseSha);
        await File.WriteAllTextAsync(Path.Combine(seed, "result.txt"), "result");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "commit", "-m", "feat: the delivered result");
        var localSha = (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
        var recovery = $"{canonical}-collision-{localSha}-{canonicalSha}";
        await GitAsync(seed, "push", "origin", $"HEAD:refs/heads/{recovery}");

        // The project checkout the server inspects the delivery from.
        await GitAsync(_watchPath, "init");
        await GitAsync(_watchPath, "remote", "add", "origin", origin);

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await client.RegisterAsync(ProjectName, "service", ct);
        var lease = await client.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "hetzner-test", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        Assert.NotNull(lease.Lease);

        // Reported, but no longer on origin - exactly what retention GC leaves
        // behind, and what made the canonical branch the only remaining claim.
        var goneImmutableRef = Contract.FencedGitRefs.ImmutableResult(
            lease.Lease.AttemptId!, lease.Lease.FencingToken, localSha);
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            Source: ProjectName,
            SalvageBranch: canonical,
            SalvageCommitSha: canonicalSha,
            ResultSha: localSha,
            AttemptChainId: lease.Lease.LeaseId,
            SalvageResolution: "divergent",
            SalvageLocalCommitSha: localSha,
            SalvageRecoveryBranch: recovery,
            SalvageRecoveryCommitSha: localSha,
            SalvageAuthoritativeBaseBranch: canonical,
            SalvageAuthoritativeBaseSha: canonicalSha,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "salvage-collision-gc-completion",
            BaseSha: baseSha,
            ImmutableResultRef: goneImmutableRef,
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            IntegrationBranch: "refs/heads/main"), ct);

        Assert.NotNull(completion);
        // Not escalated: the delivery exists, it just lives on the collision branch.
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);

        var moved = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey);
        var subject = ReviewSubjectStore.Read(moved);
        Assert.NotNull(subject);
        Assert.Equal(localSha, subject!.ResultSha);
        Assert.Equal(recovery, subject.ResultRef);
        Assert.NotEqual(canonical, subject.ResultRef);

        // The reviewed ref really carries the result, and the range is attributed
        // instead of leaving commits[] empty as it did for AGT-2220.
        var cardJson = File.ReadAllText(Path.Combine(moved, "task.json"));
        var card = JsonDocument.Parse(cardJson);
        Assert.True(card.RootElement.TryGetProperty("commits", out var commitsElement), cardJson);
        var commits = commitsElement.EnumerateArray().ToArray();
        Assert.NotEmpty(commits);
        Assert.Equal(localSha, CommitField(commits[^1], "sha", cardJson));
        Assert.Equal(recovery, CommitField(commits[^1], "branch", cardJson));
    }

    [Fact]
    public async Task Second_runner_is_refused_while_the_lease_is_held()
    {
        SeedTask(TaskStates.Progress, TaskKey, "Contended", "Prompt.");

        using var factory = BuildFactory();
        using var httpA = factory.CreateClient();
        using var httpB = factory.CreateClient();
        using var runnerA = new RClient(httpA, "runner-a");
        using var runnerB = new RClient(httpB, "runner-b");
        var ct = CancellationToken.None;

        await runnerA.RegisterAsync("runner-a remote", "service", ct);
        await runnerB.RegisterAsync("runner-b remote", "service", ct);

        var a = await runnerA.AcquireLeaseAsync(
            new RAcquire(TaskKey, "runner-a", ProjectName, "host-a", 1, "claude"), ct);
        Assert.True(a.Granted);

        // §8.2C: the contender that loses the race is told the task is Held and
        // does not proceed — the split-brain guard the runner branches on.
        var b = await runnerB.AcquireLeaseAsync(
            new RAcquire(TaskKey, "runner-b", ProjectName, "host-b", 2, "claude"), ct);
        Assert.False(b.Granted);
        Assert.Equal("Held", b.Outcome);
    }

    [Fact]
    public async Task Daemon_claim_only_returns_server_assigned_remote_capable_project()
    {
        SeedTask(
            TaskStates.Ready,
            TaskKey,
            "Daemon pickup",
            "Prompt.",
            cliType: "codex",
            model: "gpt-5.6-sol",
            thinkingLevel: "xhigh");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(
            http,
            RunnerId,
            options: RunnerOptions("claude", hostMaxParallelism: 20));
        await RegisterCodingRunnerAsync(client, http);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        // Since the blank-integration-branch semantics changed to "derive from
        // the checkout's origin/HEAD" (there is no checkout here), the claim
        // only carries a deterministic branch when the project declares one.
        (await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/integration-branch",
            new { branch = "develop" })).EnsureSuccessStatusCode();

        var wrongRunner = await client.ClaimAsync(new RClaim(
            "runner-other", "runner-other", "other-host", 1, "remote-runner"), CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, wrongRunner.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        var request = new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner", Telemetry: new RTelemetry(
                DateTime.UtcNow, 54, 6.4, 6, 5, 34_000_000_000, 64_000_000_000,
                0, 0, 6.2, 2.1, 12, 6),
            AvailableSlots: 20,
            ActiveSlots: 0,
            IdempotencyKey: "daemon-claim-1");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, request);

        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.False(string.IsNullOrWhiteSpace(claim.TaskKey));
        Assert.Equal(TaskKey, claim.JobId);
        Assert.Equal(ProjectName, claim.ProjectName);
        Assert.NotNull(claim.Lease);
        Assert.Equal("PROJ-001", claim.ProjectId);
        Assert.Equal("https://github.com/agent-orc/agent-studio.git", claim.RepositoryUrl);
        Assert.Equal("develop", claim.DefaultBranch);
        Assert.Equal(TaskKinds.Task, claim.TaskKind);
        var attemptProjection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{claim.TaskKey}", ApiJson, CancellationToken.None);
        Assert.Equal(
            Contract.RepositoryIdentityContract.FromUrl(claim.RepositoryUrl),
            attemptProjection!.CurrentRunAttempt!.RepositoryId);
        Assert.Equal("Prompt.", await client.ReadTaskFileAsync(claim.TaskKey!, "prompt.md", CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
        var laneTimeline = File.ReadAllText(Path.Combine(
            _watchPath, TaskStates.Progress, TaskKey, "logs", "timeline.jsonl"));
        Assert.Contains($"\"runId\":\"{claim.Lease.AttemptId}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"attemptId\":\"{claim.Lease.AttemptId}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"fence\":\"{claim.Lease.FencingToken}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"authorityEpoch\":\"{claim.Lease.AuthorityEpoch}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":\"lane-claim:daemon-claim-1\"", laneTimeline, StringComparison.Ordinal);
        var sessionEvent = Assert.Single(
            File.ReadLines(Path.Combine(
                    _watchPath,
                    TaskStates.Progress,
                    TaskKey,
                    "logs",
                    "session-events.jsonl"))
                .Select(line => JsonSerializer.Deserialize<SessionEvent>(line, ApiJson))
                .OfType<SessionEvent>());
        Assert.Equal("gpt-5.6-sol", sessionEvent.Model);
        Assert.Equal("xhigh", sessionEvent.ThinkingLevel);

        await AdvertiseCodingCapabilitiesAsync(
            http,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "unavailable",
            });
        var replay = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, replay.Status);
        Assert.Equal(claim.TaskKey, replay.TaskKey);
        Assert.Equal(claim.Lease.LeaseId, replay.Lease!.LeaseId);
        Assert.Equal(claim.Lease.AttemptId, replay.Lease.AttemptId);
        Assert.Equal(claim.Lease.FencingToken, replay.Lease.FencingToken);

        var telemetry = await http.GetFromJsonAsync<HostTelemetryResponse>(
            $"/api/clients/{Uri.EscapeDataString(client.ClientId)}/telemetry?window=1h");
        Assert.NotNull(telemetry);
        Assert.Single(telemetry!.Points);
        Assert.Equal(6.4, telemetry.Points[0].Load1);
        Assert.Equal(6, telemetry.Points[0].ActiveSlots);
        var clients = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        var runner = Assert.Single(clients!, item => item.Id == client.ClientId);
        Assert.Equal(1, runner.RunnerActiveSlots);
        Assert.Equal(19, runner.RunnerAvailableSlots);

        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            claim.TaskKey!,
            claim.Lease.LeaseId,
            claim.Lease.FencingToken,
            RunnerId,
            "Done",
            ResultSha: resultSha,
            AttemptChainId: claim.Lease.LeaseId,
            Repository: claim.RepositoryUrl,
            AttemptId: claim.Lease.AttemptId,
            AuthorityEpoch: claim.Lease.AuthorityEpoch,
            IdempotencyKey: "daemon-claim-completion",
            BaseSha: "4136f00d4136f00d4136f00d4136f00d4136f00d",
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                claim.Lease.AttemptId!,
                claim.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), CancellationToken.None);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        var completedProjection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{claim.TaskKey}", ApiJson, CancellationToken.None);
        Assert.Equal(
            attemptProjection.CurrentRunAttempt.RepositoryId,
            completedProjection!.CurrentReviewSubject!.RepositoryId);
    }

    [Fact]
    public async Task Daemon_claim_replay_repairs_ready_task_to_progress_before_returning_claimed()
    {
        const string claimKey = "daemon-claim-ready-replay";
        const string repositoryUrl = "https://github.com/agent-orc/agent-studio.git";
        SeedTask(TaskStates.Ready, TaskKey, "Interrupted claim", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(
            http,
            RunnerId,
            options: RunnerOptions("claude", hostMaxParallelism: 20));
        await RegisterCodingRunnerAsync(client, http);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
        await AddRepositoryUrlAsync(http, repositoryUrl);

        // Reproduce the crash boundary: acquire authority is durable, but the
        // endpoint has not yet moved the card out of Ready or returned a body.
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var acquired = authority.AcquireRun(
            TaskKey,
            Contract.RepositoryIdentityContract.FromUrl(repositoryUrl)!,
            null,
            RunnerId,
            "hetzner-test",
            120,
            claimKey,
            ProjectName,
            "remote-runner",
            4242,
            client.ClientId);
        Assert.Equal(AttemptWriteStatus.Accepted, acquired.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        var request = new RClaim(
            RunnerId,
            ProjectName,
            "hetzner-test",
            4242,
            "remote-runner",
            AvailableSlots: 20,
            ActiveSlots: 0,
            IdempotencyKey: claimKey);
        var replay = await client.ClaimAsync(request, CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, replay.Status);
        Assert.Equal(acquired.RunAttempt!.AttemptId, replay.Lease!.AttemptId);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, TaskKey);
        Assert.True(Directory.Exists(progressFolder));
        using (var taskJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(progressFolder, "task.json"))))
            Assert.Equal(TaskStates.Progress, taskJson.RootElement.GetProperty("state").GetString());
        var laneTimeline = File.ReadAllText(Path.Combine(progressFolder, "logs", "timeline.jsonl"));
        Assert.Contains($"\"attemptId\":\"{acquired.RunAttempt.AttemptId}\"", laneTimeline, StringComparison.Ordinal);
        Assert.Contains($"\"idempotencyKey\":\"lane-claim:{claimKey}\"", laneTimeline, StringComparison.Ordinal);

        var contender = await client.ClaimAsync(
            request with { IdempotencyKey = "daemon-claim-ready-contender" },
            CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, contender.Status);
        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{TaskKey}", ApiJson, CancellationToken.None);
        Assert.Single(projection!.RunAttempts);
    }

    [Fact]
    public async Task Codex_only_runner_leaves_claude_card_ready_until_claude_is_advertised()
    {
        SeedTask(
            TaskStates.Ready,
            "AGT-CLI-CLAUDE",
            "Claude-only card",
            "Prompt.",
            cliType: "claude");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(
            client,
            http,
            cliStatuses: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["codex"] = "ready",
            });
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        var request = new RClaim(
            RunnerId,
            ProjectName,
            "hetzner-test",
            4242,
            "remote-runner",
            IdempotencyKey: "cli-capability-claude");
        var incompatible = await client.ClaimAsync(request, CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, incompatible.Status);
        Assert.Contains("cli-execution:claude", incompatible.Message);
        Assert.True(Directory.Exists(Path.Combine(
            _watchPath,
            TaskStates.Ready,
            "AGT-CLI-CLAUDE")));
        Assert.Null(factory.Services.GetRequiredService<RunLeaseService>().Peek("AGT-CLI-CLAUDE").Lease);

        await AdvertiseCodingCapabilitiesAsync(
            http,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "ready",
            });
        var claimed = await ClaimWithSuccessfulPreflightAsync(client, request);

        Assert.Equal(RClaimStatus.Claimed, claimed.Status);
        Assert.Equal("AGT-CLI-CLAUDE", claimed.JobId);
    }

    [Fact]
    public async Task Mixed_runner_claims_cards_for_each_advertised_cli()
    {
        SeedTask(
            TaskStates.Ready,
            "AGT-CLI-MIXED-CLAUDE",
            "Claude card",
            "Prompt.",
            cliType: "claude",
            order: 1);
        SeedTask(
            TaskStates.Ready,
            "AGT-CLI-MIXED-CODEX",
            "Codex card",
            "Prompt.",
            cliType: "codex",
            order: 2);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var claudeBinary = await StubCli.WriteAsync(
            _workspace,
            "claude",
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"5b1c9f70-2f4a-4c31-9f0e-2f0c9c4a1e77\"}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Claude fixture completed.\"}]}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"[[TASK_DONE]]\",\"session_id\":\"5b1c9f70-2f4a-4c31-9f0e-2f0c9c4a1e77\"}");
        var runnerOptions = RunnerOptions(
            "codex",
            hostMaxParallelism: 2,
            claudeCliBin: claudeBinary);
        using var client = new RClient(
            http,
            RunnerId,
            options: runnerOptions);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        var claude = await ClaimWithSuccessfulPreflightAsync(
            client,
            new RClaim(
                RunnerId,
                ProjectName,
                "hetzner-test",
                4242,
                "remote-runner",
                IdempotencyKey: "cli-mixed-claude"));
        var codex = await client.ClaimAsync(
            new RClaim(
                RunnerId,
                ProjectName,
                "hetzner-test",
                4242,
                "remote-runner",
                IdempotencyKey: "cli-mixed-codex"),
            CancellationToken.None);

        Assert.Equal("claude", claude.RunSpec!.CliType);
        Assert.Equal("codex", codex.RunSpec!.CliType);
        var claimedClaudeSpec = claude.RunSpec with { ContextMode = "shared" };

        var invocation = Runner::AgentRunner.AgentCliProcess.Resolve(
            runnerOptions,
            claimedClaudeSpec);
        Assert.Equal(claudeBinary, invocation.FileName);
        Assert.Equal("claude", invocation.CliType);

        var workerDirectory = Path.Combine(_workspace, "mixed-cli-claude-worker");
        var worktree = Path.Combine(_workspace, "mixed-cli-claude-worktree");
        var results = Path.Combine(_workspace, "mixed-cli-claude-results");
        Directory.CreateDirectory(workerDirectory);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var persistedSpec = RDurableAgentProcess.BuildSpec(
            runnerOptions,
            worktree,
            "Run the claimed Claude fixture.",
            results,
            runSpec: claimedClaudeSpec,
            runId: claude.RunId);
        Assert.Equal(ROptions.ExecEngineCar, persistedSpec.Engine);
        Assert.Equal("claude", persistedSpec.CliType);
        Assert.Equal(claudeBinary, persistedSpec.FileName);

        var carRun = await Runner::AgentRunner.CarWorkerExecution.RunAsync(
            persistedSpec,
            workerDirectory,
            (_, _) => { });
        Assert.True(
            carRun.Result.ExitCode == 0,
            $"CAR Claude fixture failed with exit {carRun.Result.ExitCode}: " +
            $"stdout={carRun.Result.StdOut}; stderr={carRun.Result.StdErr}");
        Assert.False(carRun.TimedOut);
        Assert.False(carRun.LaunchFailed);
        Assert.Contains("[[TASK_DONE]]", carRun.Result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_stale_cli_capability_leaves_card_unclaimed(bool stale)
    {
        SeedTask(
            TaskStates.Ready,
            "AGT-CLI-BLOCKED",
            "Capability-blocked card",
            "Prompt.",
            cliType: "claude");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(
            client,
            http,
            cliStatuses: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = stale ? "ready" : "unavailable",
            },
            advertisedAt: stale ? DateTime.UtcNow.AddMinutes(-10) : null);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        var claim = await client.ClaimAsync(
            new RClaim(
                RunnerId,
                ProjectName,
                "hetzner-test",
                4242,
                "remote-runner",
                IdempotencyKey: $"cli-blocked-{stale}"),
            CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, claim.Status);
        Assert.Contains(stale ? "stale" : "unavailable", claim.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(
            _watchPath,
            TaskStates.Ready,
            "AGT-CLI-BLOCKED")));
        Assert.Null(factory.Services.GetRequiredService<RunLeaseService>().Peek("AGT-CLI-BLOCKED").Lease);
    }

    /// <summary>
    /// T0b (CAR migration plan §3 T0b / §7 AP3): the claim carries the card's
    /// execution specification, and the runner turns it into the CLI invocation.
    /// Before this, a remote run took its CLI, model and reasoning level from the
    /// host's <c>RUNNER_CLI_*</c> environment — the card's choice was honoured
    /// locally and silently dropped remotely.
    ///
    /// <para>
    /// Two cards in one claim sequence prove both halves: a claude card whose
    /// pinned rung is supported travels through verbatim, and a codex card whose
    /// rung the model does not offer is resolved server-side to a supported one
    /// instead of reaching the CLI as an invalid flag value.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Daemon_claim_carries_the_cards_execution_spec_and_the_runner_builds_its_cli_args()
    {
        SeedTask(TaskStates.Ready, "AGT-SPEC-CLAUDE", "Spec on the wire", "Prompt.",
            cliType: "claude", model: "claude-opus-4-8", thinkingLevel: "max", order: 1);
        SeedTask(TaskStates.Ready, "AGT-SPEC-CODEX", "Spec on the wire, other CLI", "Prompt.",
            cliType: "codex", model: "gpt-5.6-codex", thinkingLevel: "max", order: 2);

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(
            http,
            RunnerId,
            options: RunnerOptions("claude", hostMaxParallelism: 2));
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner",
            IdempotencyKey: "spec-claim-claude"));

        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.Equal("AGT-SPEC-CLAUDE", claim.JobId);
        Assert.NotNull(claim.RunSpec);
        Assert.Equal("claude", claim.RunSpec!.CliType);
        Assert.Equal("claude-opus-4-8", claim.RunSpec.Model);
        Assert.Equal("max", claim.RunSpec.ThinkingLevel);
        Assert.Contains("## Prompt enrichment", claim.RunSpec.ModeFraming);
        Assert.Contains("repo-instructions-source", claim.RunSpec.ModeFraming);
        // Both modes resolve from live project settings, so they are always
        // stated; the runner transports them but does not yet build flags.
        Assert.False(string.IsNullOrWhiteSpace(claim.RunSpec.PermissionMode));
        Assert.False(string.IsNullOrWhiteSpace(claim.RunSpec.ContextMode));

        var claudeInvocation = Runner::AgentRunner.AgentCliProcess.Resolve(
            RunnerOptions("claude"), claim.RunSpec);
        Assert.Equal("claude", claudeInvocation.FileName);
        Assert.Equal(["--model", "claude-opus-4-8", "--effort", "max"], claudeInvocation.Arguments);

        // The host-capacity contract admits one slot for this fixture. Release
        // the first lease before probing the second card's independent RunSpec.
        await client.ReleaseLeaseAsync(new RRelease(
            claim.TaskKey!,
            claim.Lease!.LeaseId,
            claim.Lease.FencingToken,
            RunnerId,
            claim.Lease.AttemptId,
            claim.Lease.AuthorityEpoch,
            $"release:{claim.Lease.AttemptId}"), CancellationToken.None);

        var codexClaim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner",
            IdempotencyKey: "spec-claim-codex"), CancellationToken.None);

        Assert.True(
            codexClaim.Status == RClaimStatus.Claimed,
            $"Expected the second card to be claimed, got {codexClaim.Status}: {codexClaim.Message}; admission={codexClaim.AdmissionReason}");
        Assert.Equal("AGT-SPEC-CODEX", codexClaim.JobId);
        Assert.Equal("codex", codexClaim.RunSpec!.CliType);
        Assert.Equal("gpt-5.6-codex", codexClaim.RunSpec.Model);
        Assert.Contains("## Prompt enrichment", codexClaim.RunSpec.ModeFraming);
        // Codex has no "max" rung; the server resolves the card's request against
        // the model's ladder rather than shipping an invalid selector.
        Assert.Equal("medium", codexClaim.RunSpec.ThinkingLevel);

        // The card routes to the other CLI, so RUNNER_CLI_BIN / RUNNER_CLI_ARGS
        // stop being the truth: the codex binary and its minimal headless form win.
        var codexInvocation = Runner::AgentRunner.AgentCliProcess.Resolve(
            RunnerOptions("claude"), codexClaim.RunSpec);
        Assert.Equal("codex", codexInvocation.FileName);
        Assert.Equal(
            ["exec", "--experimental-json", "-m", "gpt-5.6-codex", "-c", "model_reasoning_effort=\"medium\"", "-"],
            codexInvocation.Arguments);
    }

    [Fact]
    public async Task Remote_assigned_ready_epic_completes_planning_with_children_and_no_runner_branch()
    {
        const string epicKey = "AGT-EPIC-REMOTE";
        SeedTask(TaskStates.Ready, epicKey, "Remote Epic", "Split this goal into coding cards.",
            kind: TaskKinds.Epic, cliType: "codex", model: "gpt-5.6-codex");
        var origin = await SeedOriginAsync();
        var runnerWork = Path.Combine(_workspace, "remote-runner-work");
        // The planner stub only has to print a plan and exit; StubCli emits it in
        // the form this host can execute (a shell script with the executable bit,
        // or a .cmd on Windows, where neither shebangs nor Unix modes exist).
        var cli = await StubCli.WriteAsync(
            _workspace,
            "fake-planner",
            "```json",
            "{\"subTasks\":[{\"title\":\"Implement API\",\"prompt\":\"Build and test the API.\"},{\"title\":\"Add UI\",\"prompt\":\"Build and test the UI.\"}]}",
            "```",
            "[[TASK_DONE]]");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/remote-epic-contract.git");

        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"));
        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.Equal(TaskKinds.Epic, claim.TaskKind);

        var options = new ROptions
        {
            ServerUrl = "http://in-process",
            RunnerId = RunnerId,
            RunnerName = ProjectName,
            Hostname = "hetzner-test",
            BackendName = "remote-runner",
            WorkDir = runnerWork,
            StateDir = Path.Combine(runnerWork, ".runner-state"),
            BaseBranch = "main",
            CliBin = cli,
            CodexCliBin = cli,
            CliArgs = "",
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            RunTimeoutSeconds = 30,
            HostMaxParallelism = 1,
            PollSeconds = 1,
            ExecEngine = ROptions.ExecEngineLegacy,
        };
        var taskRunner = new RTaskRunner(options, client, _ => { });
        var exit = await taskRunner.RunClaimedAsync(
            claim.TaskKey!, claim.Lease!, CancellationToken.None,
            claim.ProjectId, origin, "main", claim.TaskKind,
            claim.RunId, claim.LeaseInstanceId, claim.RunSpec);

        Assert.Equal(0, exit);
        // A planning run owns no Result-SHA, so it must never land in the
        // code-review lane; its delivery is the validated child set.
        var epicFolder = Path.Combine(_watchPath, TaskStates.HumanReview, epicKey);
        Assert.True(Directory.Exists(epicFolder));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.AutoReview)));
        var children = Directory.EnumerateFiles(_workspace, "task.json", SearchOption.AllDirectories)
            .Where(path =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.TryGetProperty("epicId", out var epicId)
                       && epicId.GetString() == epicKey;
            })
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .ToList();
        Assert.Equal(2, children.Count);
        foreach (var childFolder in children)
        {
            using var childJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(childFolder, "task.json")));
            Assert.Equal(epicKey, childJson.RootElement.GetProperty("epicId").GetString());
            Assert.Equal("codex", childJson.RootElement.GetProperty("cliType").GetString());
            Assert.Equal("gpt-5.6-codex", childJson.RootElement.GetProperty("model").GetString());
        }
        Assert.Equal(2, File.ReadAllLines(Path.Combine(epicFolder, ".metadata", "spawned-tasks.jsonl")).Length);
        var runnerBranch = await GitAsync(origin,
            ["show-ref", "--verify", "--quiet", $"refs/heads/runner/{RunnerId}/{epicKey}"],
            allowFailure: true);
        Assert.NotEqual(0, runnerBranch.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(runnerWork, "PROJ-001", "worktrees", epicKey)));
    }

    [Fact]
    public async Task Remote_epic_planning_completion_without_result_sha_never_enters_auto_review()
    {
        // TE-8's dead end, reproduced as a regression: a planning run settles
        // Completed with terminalOutcome=done, resultSha=null, on a mode=coding
        // card. CreateReviewAttempt demands a non-empty ExpectedResultSha, and
        // the report-only exception in the decision engine does not apply to a
        // coding card, so 4-auto-review would wait forever on a ReviewAttempt
        // that can never be minted.
        const string epicKey = "AGT-EPIC-NO-SHA";
        SeedTask(TaskStates.Ready, epicKey, "Planning Epic", "Split this goal into coding cards.",
            kind: TaskKinds.Epic);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner"));

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            claim.TaskKey!, claim.Lease!.LeaseId, claim.Lease.FencingToken, RunnerId,
            "Done", Source: ProjectName,
            ExitCode: 0,
            // A read-only planning teardown reports no Result-SHA, by design.
            ResultSha: null,
            OutputLines: ["{\"subTasks\":[{\"title\":\"Implement API\",\"prompt\":\"Build and test the API.\"}]}"],
            AttemptId: claim.Lease.AttemptId,
            AuthorityEpoch: claim.Lease.AuthorityEpoch,
            IdempotencyKey: $"epic-no-sha:{epicKey}"), ct);

        Assert.Equal(TaskStates.HumanReview, completion!.TargetState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, epicKey)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.AutoReview)));

        // The card really is the constellation from the report: coding mode,
        // done, no Result-SHA - and therefore no ReviewAttempt to wait for.
        using var task = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(_watchPath, TaskStates.HumanReview, epicKey, "task.json")));
        Assert.Equal(
            TaskModes.Coding,
            TaskModes.Normalize(task.RootElement.TryGetProperty("mode", out var mode) ? mode.GetString() : null));
        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{claim.TaskKey}", ApiJson, ct);
        Assert.Equal(AttemptLifecycleState.Completed, projection!.CurrentRunAttempt!.State);
        Assert.Equal("done", projection.CurrentRunAttempt.TerminalOutcome);
        Assert.Null(projection.CurrentRunAttempt.ResultSha);
        Assert.Null(projection.CurrentReviewAttempt);

        var sessionEvent = JsonSerializer.Deserialize<SessionEvent>(
            Assert.Single(File.ReadLines(Path.Combine(
                _watchPath,
                TaskStates.HumanReview,
                epicKey,
                "logs",
                TaskPaths.SessionEventsLogFileName))),
            TaskJsonFile.ReadOpts)!;
        Assert.Equal(claim.Lease.AttemptId, sessionEvent.RunAttemptId);
        Assert.Equal("done", sessionEvent.Result);
        Assert.Equal(RunStatuses.Completed, sessionEvent.Status);
        Assert.Equal(0, sessionEvent.ExitCode);
        Assert.NotNull(sessionEvent.FinishedAt);
        Assert.NotNull(sessionEvent.DurationSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a decomposition plan")]
    public async Task Remote_epic_empty_or_invalid_plan_returns_to_backlog(string output)
    {
        const string epicKey = "AGT-EPIC-INVALID";
        SeedTask(TaskStates.Ready, epicKey, "Invalid Epic", "Plan it.", kind: TaskKinds.Epic);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner"));

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            claim.TaskKey!, claim.Lease!.LeaseId, claim.Lease.FencingToken, RunnerId,
            "Done", Source: ProjectName,
            OutputLines: string.IsNullOrEmpty(output) ? [] : [output],
            AttemptId: claim.Lease.AttemptId,
            AuthorityEpoch: claim.Lease.AuthorityEpoch,
            IdempotencyKey: $"epic-invalid:{epicKey}:{output}"), CancellationToken.None);

        Assert.Equal(TaskStates.Backlog, completion!.TargetState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Backlog, epicKey)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.AutoReview)));
    }

    [Fact]
    public async Task Remote_epic_source_mutation_invalidates_an_otherwise_valid_plan()
    {
        const string epicKey = "AGT-EPIC-MUTATED";
        SeedTask(TaskStates.Ready, epicKey, "Mutating Epic", "Plan without editing source.",
            kind: TaskKinds.Epic);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner"));

        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            claim.TaskKey!, claim.Lease!.LeaseId, claim.Lease.FencingToken, RunnerId,
            "Done", Source: ProjectName,
            OutputLines: ["{\"subTasks\":[{\"title\":\"Must not exist\",\"prompt\":\"No.\"}]}"],
            SourceMutated: true,
            AttemptId: claim.Lease.AttemptId,
            AuthorityEpoch: claim.Lease.AuthorityEpoch,
            IdempotencyKey: $"epic-mutated:{epicKey}"), CancellationToken.None);

        Assert.Equal(TaskStates.Backlog, completion!.TargetState);
        Assert.Contains("read-only checkout", completion.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Backlog, epicKey)));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_workspace, "task.json", SearchOption.AllDirectories),
            path => JsonDocument.Parse(File.ReadAllText(path)).RootElement
                .TryGetProperty("epicId", out var epicId) && epicId.GetString() == epicKey);
    }

    [Theory]
    [InlineData(TaskKinds.Task)]
    [InlineData(TaskKinds.Epic)]
    // This exercises the production endpoint against persisted lease timestamps
    // and real host time. The class-level MachineBound trait keeps both theory
    // cases out of the card gate; the pure boundary decisions remain covered by
    // RemoteRunRequeuePolicyTests in the deterministic gate suite.
    public async Task Remote_claim_requeues_only_after_grace_and_runner_confirms_inactive(string kind)
    {
        SeedTask(TaskStates.Ready, TaskKey, "Restart recovery", "Prompt.", kind: kind);
        // Start with a grace so wide that no host can walk out of it. The endpoint
        // re-reads the value from IConfiguration on every claim, so the test can
        // narrow it later instead of racing the wall clock: with a 1s grace the
        // "inside grace" assertions below simply lost on a slow-spawn host, which
        // is what made both theory cases permanently red on Windows.
        using var factory = BuildFactory(remoteRequeueGraceSeconds: 900);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");
        var first = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "host", 1, "remote-runner",
            ActiveTaskKeys: []));
        await client.ReleaseLeaseAsync(new RRelease(
            first.TaskKey!, first.Lease!.LeaseId, first.Lease.FencingToken, RunnerId,
            first.Lease.AttemptId, first.Lease.AuthorityEpoch,
            $"release:{first.Lease.AttemptId}"), CancellationToken.None);

        var insideGrace = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 2, "remote-runner",
            ActiveTaskKeys: []), CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, insideGrace.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));

        // Narrow the grace to its minimum (the endpoint clamps to 1..900) and let
        // it elapse. From here the remaining claims turn on the runner's own
        // active-key report, not on timing.
        factory.Services.GetRequiredService<IConfiguration>()["Runner:RemoteRequeue:GraceSeconds"] = "1";
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var runnerStillActive = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 2, "remote-runner",
            ActiveTaskKeys: [first.TaskKey!]), CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, runnerStillActive.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));

        var recovered = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "host", 2, "remote-runner",
            ActiveTaskKeys: []), CancellationToken.None);

        Assert.Equal(RClaimStatus.Claimed, recovered.Status);
        Assert.Equal(TaskKey, recovered.JobId);
        Assert.Equal(kind, recovered.TaskKind);
        Assert.True(recovered.Lease!.FencingToken > first.Lease.FencingToken);
        var projection = await http.GetFromJsonAsync<AttemptAuthorityProjection>(
            $"/api/attempts/tasks/{first.TaskKey}", ApiJson, CancellationToken.None);
        Assert.Equal(first.Lease.AttemptId, projection!.CurrentRunAttempt!.SourceAttemptId);
    }

    [Fact]
    public async Task Fallback_remote_failure_does_not_override_a_successful_project_preflight()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Push-gated pickup", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        var clientId = await RegisterCodingRunnerAsync(client, http);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
        await AddRepositoryUrlAsync(http, "https://github.com/agent-orc/agent-studio.git");

        await client.ReportGitCapabilityAsync(clientId, new RGitCapability(
            "read-only", "push-dry-run failed (128): permission denied", DateTime.UtcNow), CancellationToken.None);

        var admitted = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"));

        Assert.True(
            admitted.Status == RClaimStatus.Claimed,
            $"Expected project-scoped admission, got {admitted.Status}: {admitted.Message}");
        Assert.Equal(TaskKey, admitted.JobId);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
    }

    [Fact]
    public async Task Project_without_write_permission_is_refused_and_card_stays_ready()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Project delivery denied", "Prompt.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/read-only-project.git");

        var request = new RClaim(RunnerId, ProjectName, "host", 1, "remote-runner");
        var offered = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, offered.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        var refused = await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                offered.ProjectId!, offered.RegistrationFingerprint!, false,
                "write probe failed (128): permission denied",
                DateTime.UtcNow, offered.RepositoryUrl!, offered.RepositoryUrl!),
        }, CancellationToken.None);

        Assert.Equal(RClaimStatus.PreflightFailed, refused.Status);
        Assert.Contains("permission denied", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, TaskKey)));
        using (var taskJson = JsonDocument.Parse(File.ReadAllText(
                   Path.Combine(_watchPath, TaskStates.Ready, TaskKey, "task.json"))))
        {
            Assert.False(taskJson.RootElement.TryGetProperty("remoteClaimFailure", out _));
        }

        var identities = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        var host = Assert.Single(identities!, identity => identity.Id == client.ClientId);
        var failure = Assert.Single(host.RunnerProjectPreflights);
        Assert.Equal("failed", failure.Status);
        Assert.Contains("permission denied", failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_project_preflight_does_not_block_another_assigned_project()
    {
        const string deliverableProjectName = "deliverable-project";
        var deliverableWatchPath = Path.Combine(_workspace, "projects", deliverableProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(deliverableWatchPath, state));
        SeedTask(TaskStates.Ready, "AGT-BLOCKED-REPO", "Blocked repository", "Prompt.");
        SeedTask(
            TaskStates.Ready,
            "DVP-001",
            "Deliverable repository",
            "Prompt.",
            watchPath: deliverableWatchPath,
            order: 2);
        using var factory = BuildFactory(
            additionalProjectName: deliverableProjectName,
            additionalWatchPath: deliverableWatchPath);
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/blocked-project.git");
        await AssignRemoteAsync(http, deliverableProjectName);
        var projects = await http.GetFromJsonAsync<List<ProjectSummary>>("/api/projects");
        var deliverableProject = Assert.Single(
            projects!,
            project => string.Equals(project.DisplayName, deliverableProjectName, StringComparison.Ordinal));
        await AddRepositoryUrlAsync(
            http,
            "https://github.com/example/deliverable-project.git",
            deliverableProject.Id);

        var request = new RClaim(RunnerId, ProjectName, "host", 1, "remote-runner");
        var blockedOffer = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, blockedOffer.Status);
        var blocked = await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                blockedOffer.ProjectId!, blockedOffer.RegistrationFingerprint!, false,
                "write probe failed (128): repository-specific permission denied",
                DateTime.UtcNow, blockedOffer.RepositoryUrl!, blockedOffer.RepositoryUrl!),
        }, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightFailed, blocked.Status);

        var deliverableOffer = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, deliverableOffer.Status);
        Assert.Equal(deliverableProject.Id, deliverableOffer.ProjectId);

        var admitted = await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                deliverableOffer.ProjectId!, deliverableOffer.RegistrationFingerprint!, true,
                "clone/fetch URLs match registration; target branch exists; write probe succeeded",
                DateTime.UtcNow, deliverableOffer.RepositoryUrl!, deliverableOffer.RepositoryUrl!),
        }, CancellationToken.None);

        Assert.True(
            admitted.Status == RClaimStatus.Claimed,
            $"Expected second project claim, got {admitted.Status}: {admitted.Message}");
        Assert.Equal(deliverableProject.Id, admitted.ProjectId);
        Assert.True(Directory.Exists(Path.Combine(
            _watchPath, TaskStates.Ready, "AGT-BLOCKED-REPO")));
    }

    /// <summary>
    /// AGT-2302 / AGT-2376: capacity is a host fact. The daemon's bootstrap
    /// value seeds the central ceiling on first contact, the ceiling then holds
    /// further claims, the slot ledger is derived from it (never "active + 1"),
    /// and an operator raise takes effect on the next poll.
    /// </summary>
    [Fact]
    public async Task Host_ceiling_admits_up_to_its_capacity_and_holds_further_claims()
    {
        SeedTask(TaskStates.Ready, "AGT-CAP-A", "First", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-CAP-B", "Second", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-CAP-C", "Third", "Prompt.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/writable-project.git");

        // The daemon reports RUNNER_MAX_PARALLELISM=2; the server adopts it as
        // the host's central ceiling and echoes the policy back on every poll.
        var request = new RClaim(RunnerId, ProjectName, "host", 1, "remote-runner")
        {
            BootstrapMaxParallelism = 2,
        };
        var first = await ClaimWithSuccessfulPreflightAsync(client, request);
        Assert.Equal(RClaimStatus.Claimed, first.Status);
        Assert.Equal(2, first.DesiredMaxParallelism);
        Assert.Equal(RunnerRampStrategies.Balanced, first.RampStrategy);

        var second = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, second.Status);
        Assert.NotEqual(first.TaskKey, second.TaskKey);

        // Ceiling reached: the third poll is held, and says why.
        var held = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, held.Status);
        Assert.Equal(HostAdmissionReasons.CeilingReached, held.AdmissionReason);
        Assert.Contains("2/2", held.Message);

        // The ledger describes a capacity, not the daemon's breathing headroom.
        var clients = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        var host = Assert.Single(clients!, item => item.Id == client.ClientId);
        Assert.Equal(2, host.RunnerDesiredMaxParallelism);
        Assert.Equal(2, host.RunnerActiveSlots);
        Assert.Equal(0, host.RunnerAvailableSlots);

        // Raising the central ceiling frees a slot on the very next poll.
        var raised = await http.PutAsJsonAsync(
            $"/api/clients/{Uri.EscapeDataString(client.ClientId)}/runner-capacity",
            new { maxParallelism = 3, targetLoadPercent = 85, rampStrategy = "aggressive" });
        raised.EnsureSuccessStatusCode();

        var third = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, third.Status);
        Assert.Equal(3, third.DesiredMaxParallelism);
        Assert.Equal(RunnerRampStrategies.Aggressive, third.RampStrategy);
    }

    /// <summary>
    /// Review fix (AGT-2302 / AGT-2376): the deprecated per-project
    /// <c>maxParallelism</c> may narrow the seeded host ceiling, never raise it.
    /// Seeding a project cap of 6 onto a daemon that runs 2 would let the server
    /// hand out three times the slots the host actually has. The editing route is
    /// back because local execution still limits itself by the same value.
    /// </summary>
    [Fact]
    public async Task Project_max_parallelism_narrows_the_host_seed_but_never_raises_it()
    {
        SeedTask(TaskStates.Ready, "AGT-SEED-A", "First", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-SEED-B", "Second", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-SEED-C", "Third", "Prompt.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/seed-clamp.git");

        var projectCap = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/max-parallelism", new { maxParallelism = 6 });
        projectCap.EnsureSuccessStatusCode();

        var request = new RClaim(RunnerId, ProjectName, "host", 1, "remote-runner")
        {
            BootstrapMaxParallelism = 2,
        };
        var first = await ClaimWithSuccessfulPreflightAsync(client, request);
        Assert.Equal(RClaimStatus.Claimed, first.Status);
        Assert.Equal(2, first.DesiredMaxParallelism);

        var second = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, second.Status);

        // The project asked for 6, the daemon can run 2: the third poll is held.
        var held = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Empty, held.Status);
        Assert.Equal(HostAdmissionReasons.CeilingReached, held.AdmissionReason);

        var clients = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        Assert.Equal(2, Assert.Single(clients!, item => item.Id == client.ClientId)
            .RunnerDesiredMaxParallelism);
    }

    /// <summary>
    /// Review fix (AGT-2302 / AGT-2376): a daemon that declares no capacity of
    /// its own is not capped from a project value alone. Without a declaration
    /// the server enforces nothing - the fleet keeps behaving exactly as before.
    /// </summary>
    [Fact]
    public async Task Host_that_declares_no_capacity_is_not_capped_by_a_project_value()
    {
        SeedTask(TaskStates.Ready, "AGT-NOCAP-A", "First", "Prompt.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/no-declared-capacity.git");

        var projectCap = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/max-parallelism", new { maxParallelism = 6 });
        projectCap.EnsureSuccessStatusCode();

        // Posted raw: an old daemon sends neither its bootstrap value nor an
        // adopted one, and the runner client would otherwise fill both in.
        var response = await http.PostAsJsonAsync("/api/runner/claim", new
        {
            runnerId = RunnerId,
            runnerName = ProjectName,
            hostname = "host",
            pid = 1,
            backendName = "remote-runner",
            availableSlots = 1,
        });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            !body.RootElement.TryGetProperty("desiredMaxParallelism", out var ceiling)
            || ceiling.ValueKind == JsonValueKind.Null,
            "A project value alone must not become a server-enforced host ceiling.");

        var clients = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        Assert.Null(Assert.Single(clients!, item => item.Id == client.ClientId)
            .RunnerDesiredMaxParallelism);
    }

    [Fact]
    public async Task Green_project_preflight_is_cached_for_the_following_card()
    {
        SeedTask(TaskStates.Ready, "AGT-PREFLIGHT-A", "First", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-PREFLIGHT-B", "Second", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-PREFLIGHT-C", "Third", "Prompt.");
        SeedTask(TaskStates.Ready, "AGT-PREFLIGHT-D", "Fourth", "Prompt.");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(
            http,
            RunnerId,
            options: RunnerOptions("claude", hostMaxParallelism: 4));
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/writable-project.git");

        var request = new RClaim(RunnerId, ProjectName, "host", 1, "remote-runner");
        var first = await ClaimWithSuccessfulPreflightAsync(client, request);
        Assert.Equal(RClaimStatus.Claimed, first.Status);

        // One direct poll claims the following card. A PreflightRequired reply
        // here would force the daemon into an additional request roundtrip.
        var following = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, following.Status);
        Assert.NotEqual(first.TaskKey, following.TaskKey);

        var branchChange = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/integration-branch",
            new { branch = "release" });
        branchChange.EnsureSuccessStatusCode();
        var branchInvalidated = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, branchInvalidated.Status);
        Assert.Equal("release", branchInvalidated.DefaultBranch);

        var afterBranchChange = await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                branchInvalidated.ProjectId!, branchInvalidated.RegistrationFingerprint!, true,
                "clone/fetch URLs match registration; write probe succeeded",
                DateTime.UtcNow, branchInvalidated.RepositoryUrl!, branchInvalidated.RepositoryUrl!),
        }, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, afterBranchChange.Status);

        var registrationChange = await http.PutAsJsonAsync(
            "/api/projects/PROJ-001/urls/url-1",
            new { label = "repo", url = "https://github.com/example/re-registered-project.git" });
        registrationChange.EnsureSuccessStatusCode();
        var invalidated = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, invalidated.Status);
        Assert.Equal("https://github.com/example/re-registered-project.git", invalidated.RepositoryUrl);
    }

    [Fact]
    public async Task Daemon_claim_skips_assigned_project_without_repository_url()
    {
        SeedTask(TaskStates.Ready, TaskKey, "No remote repository", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
        // Declared so the preflight row can report the delivery target; blank
        // would mean "derive from the checkout's origin/HEAD", and this fixture
        // deliberately has no checkout.
        (await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/integration-branch",
            new { branch = "develop" })).EnsureSuccessStatusCode();

        // The registry invariant is visible in Remote Hosts before the Runner
        // ever polls. Operators do not need a failed claim to discover the
        // missing repository registration.
        var beforeClaim = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        var registeredHost = Assert.Single(beforeClaim!, identity => identity.Id == client.ClientId);
        var registryWarning = Assert.Single(registeredHost.RunnerProjectPreflights);
        Assert.Equal("failed", registryWarning.Status);
        Assert.Contains("repositoryUrl is missing", registryWarning.Detail, StringComparison.Ordinal);

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, claim.Status);
        Assert.Null(claim.RepositoryUrl);
        Assert.Contains("not remote-capable", claim.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repository URL is not configured", claim.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));

        var identities = await http.GetFromJsonAsync<List<ClientSummary>>("/api/clients");
        var host = Assert.Single(identities!, identity => identity.Id == client.ClientId);
        var projectFailure = Assert.Single(host.RunnerProjectPreflights);
        Assert.Equal("failed", projectFailure.Status);
        Assert.Equal("develop", projectFailure.TargetBranch);
        Assert.Contains("repository URL is not configured", projectFailure.Detail, StringComparison.OrdinalIgnoreCase);

        using var grouped = await http.GetAsync("/api/tasks/grouped");
        grouped.EnsureSuccessStatusCode();
        using var groupedJson = JsonDocument.Parse(await grouped.Content.ReadAsStringAsync());
        var card = Assert.Single(groupedJson.RootElement.GetProperty("ready").EnumerateArray());
        var rejection = card.GetProperty("executionLocation").GetProperty("lastRejection");
        Assert.Equal("repository-url-missing", rejection.GetProperty("code").GetString());
        Assert.Equal(RunnerId, rejection.GetProperty("runnerId").GetString());
        Assert.Equal("project has no repositoryUrl", rejection.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Daemon_claim_records_build_profile_gate_on_every_ready_card_before_filtering()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Profile validation blocks pickup", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);

        (await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/build-profile",
            new { stack = "dotnet", buildCmds = new[] { "dotnet build" } })).EnsureSuccessStatusCode();

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, claim.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
        using var grouped = await http.GetAsync("/api/tasks/grouped");
        grouped.EnsureSuccessStatusCode();
        using var groupedJson = JsonDocument.Parse(await grouped.Content.ReadAsStringAsync());
        var card = Assert.Single(groupedJson.RootElement.GetProperty("ready").EnumerateArray());
        var rejection = card.GetProperty("executionLocation").GetProperty("lastRejection");
        Assert.Equal("build-profile-gate", rejection.GetProperty("code").GetString());
        Assert.Equal(RunnerId, rejection.GetProperty("runnerId").GetString());
        Assert.Contains(
            "not yet validated",
            rejection.GetProperty("reason").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Daemon_claim_skips_project_that_opts_out_of_remote_execution()
    {
        SeedTask(TaskStates.Ready, TaskKey, "Machine-bound", "Prompt.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);

        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = false });
        assignment.EnsureSuccessStatusCode();

        var claim = await client.ClaimAsync(new RClaim(
            RunnerId, ProjectName, "hetzner-test", 4242, "remote-runner"), CancellationToken.None);

        Assert.Equal(RClaimStatus.Empty, claim.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, TaskKey)));
    }

    private WebApplicationFactory<Program> BuildFactory(
        IAtomicJsonFileWriter? writer = null,
        int? remoteRequeueGraceSeconds = null,
        string? additionalProjectName = null,
        string? additionalWatchPath = null,
        ICliOneShot? summaryOneShot = null,
        string? repositoryPath = null,
        string? primaryProjectName = null,
        string? primaryWatchPath = null,
        Func<DateTime>? authorityNow = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = primaryProjectName ?? ProjectName,
                        ["WatchPaths:0:Path"] = primaryWatchPath ?? _watchPath,
                        ["WatchPaths:0:RootPath"] = repositoryPath ?? _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = repositoryPath ?? _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                        ["Runner:RunLiveness:Enabled"] = authorityNow is null ? null : "false",
                        ["Runner:RemoteRequeue:GraceSeconds"] =
                            remoteRequeueGraceSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    };
                    if (!string.IsNullOrWhiteSpace(additionalProjectName)
                        && !string.IsNullOrWhiteSpace(additionalWatchPath))
                    {
                        values["WatchPaths:1:Name"] = additionalProjectName;
                        values["WatchPaths:1:Path"] = additionalWatchPath;
                        values["WatchPaths:1:RootPath"] = additionalWatchPath;
                        values["WatchPaths:1:RepositoryPath"] = additionalWatchPath;
                    }
                    cfg.AddInMemoryCollection(values);
                });
                if (writer is not null || summaryOneShot is not null || authorityNow is not null)
                {
                    b.ConfigureTestServices(services =>
                    {
                        if (writer is not null)
                            services.AddSingleton<IAtomicJsonFileWriter>(writer);
                        if (summaryOneShot is not null)
                        {
                            services.AddSingleton(
                                new CliOneShotRegistry([summaryOneShot]));
                        }
                        if (authorityNow is not null)
                        {
                            services.RemoveAll<AttemptAuthorityService>();
                            services.AddSingleton(sp => new AttemptAuthorityService(
                                sp.GetRequiredService<IConfiguration>(),
                                sp.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                                authorityNow,
                                sp.GetRequiredService<IAtomicJsonFileWriter>()));
                        }
                    });
                }
            });

    private sealed class StubSummaryOneShot : ICliOneShot
    {
        private readonly object _gate = new();
        private readonly Queue<bool> _outcomes;
        private bool _lastOutcome;

        public StubSummaryOneShot(params bool[] outcomes)
        {
            _outcomes = new Queue<bool>(outcomes.Length == 0 ? [true] : outcomes);
            _lastOutcome = _outcomes.Last();
        }

        public string CliType => CliTypes.Claude;

        public int Calls { get; private set; }

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            var isSummary = string.Equals(
                request.Source,
                AdHocUsageSources.SummaryGeneration,
                StringComparison.Ordinal);
            bool succeed;
            lock (_gate)
            {
                if (isSummary)
                {
                    Calls++;
                    if (_outcomes.Count > 0) _lastOutcome = _outcomes.Dequeue();
                    succeed = _lastOutcome;
                }
                else
                {
                    succeed = _lastOutcome;
                }
            }
            const string markdown = """
                # Status

                - Result: Success
                - Case: bugfix

                ## Overview

                - Problem: The remote task needed a durable result protocol.
                - Solution: Done and verified by the remote result fixture.

                ## What Was Done

                - Uploaded all remote evidence before teardown.

                ## Open Items

                - None.
                """;
            var requestedAt = DateTime.UtcNow;
            var completedAt = requestedAt.AddMilliseconds(1);
            return Task.FromResult(new CliOneShotResult(
                Ok: succeed,
                ExitCode: succeed ? 0 : 1,
                Stdout: succeed ? markdown : string.Empty,
                Stderr: succeed ? string.Empty : isSummary ? "summary fixture failed" : "not a summary fixture",
                Duration: completedAt - requestedAt,
                ParsedText: succeed ? markdown : string.Empty,
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(
                    RequestedAt: requestedAt,
                    CompletedAt: completedAt,
                    TotalMs: 1),
                Error: succeed ? null : isSummary ? "summary fixture failed" : "not a summary fixture"));
        }
    }

    private ReviewAttemptDto SeedReviewAttempt(
        IServiceProvider services,
        bool includeResultEnvelope)
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string baseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
        const string repositoryUrl = "https://example.invalid/agent-studio.git";
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(repositoryUrl)!;
        var authority = services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(
            TaskKey,
            repositoryId,
            null,
            RunnerId,
            "coding-host",
            120,
            "seed-review-run").RunAttempt!;
        var envelope = includeResultEnvelope
            ? new Contract.ImmutableResultEnvelope(
                repositoryId,
                run.AttemptId,
                baseSha,
                resultSha,
                "refs/heads/agent-studio/results/review-budget",
                null,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RepositoryUrl: repositoryUrl)
            : null;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "seed-review-completion"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = envelope,
        });
        Assert.True(settled.Accepted);
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey,
            repositoryId,
            resultSha,
            run.AttemptId,
            "requirements",
            "policy",
            [],
            "seed-review-attempt",
            RepositoryUrl: repositoryUrl));
        Assert.True(created.Accepted);
        return created.ReviewAttempt!;
    }

    private static async Task RegisterReviewExecutorAsync(
        HttpClient http,
        string runnerId,
        string instanceId)
    {
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{runnerId}",
            new Contract.RegisterRunnerRequest(
                runnerId,
                "review-host",
                instanceId,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.BaselineComparison,
                    Contract.ReviewCapabilities.DependencyPreparation,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ]));
        registration.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Monolith_v1_review_executor_accepts_capability_advertisement()
    {
        const string reviewRunnerId = "review-runner-capabilities";
        const string reviewInstance = "review-capability-host:4243";

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        var advertisedAt = DateTime.UtcNow;

        var response = await http.PutAsJsonAsync(
            $"/api/v1/runners/{reviewRunnerId}/capabilities",
            new Contract.CapabilityAdvertisementRequest(
                reviewRunnerId,
                reviewInstance,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                advertisedAt,
                180,
                1,
                [
                    new(
                        Contract.CapabilityProtocol.ReviewExecutor,
                        "executor",
                        Identity: "review"),
                    new(
                        Contract.ReviewCapabilities.SemanticReview,
                        "review",
                        Identity: "remote-review"),
                    new(
                        Contract.ReviewCapabilities.BaselineComparison,
                        "review",
                        Identity: "merge-base"),
                ]));

        response.EnsureSuccessStatusCode();
        var snapshot =
            await response.Content.ReadFromJsonAsync<Contract.RunnerCapabilitySnapshotDto>();
        Assert.NotNull(snapshot);
        Assert.Equal(reviewRunnerId, snapshot.RunnerId);
        Assert.Equal(reviewInstance, snapshot.InstanceId);
        Assert.Equal("open", snapshot.HostAdmission.AdmissionState);
        Assert.Contains(
            snapshot.Capabilities,
            capability => capability.Key == Contract.CapabilityProtocol.ReviewExecutor
                          && capability.HealthState == Contract.CapabilityHealthStates.Healthy
                          && capability.IsFresh);
    }

    [Fact]
    public async Task Unknown_authority_review_report_returns_LeaseExpired_quickly()
    {
        const string executorId = "review-runner-unknown-authority";
        const string instanceId = "review-host:unknown-authority";
        const string expectedSha = "589c462f589c462f589c462f589c462f589c462f";
        var request = new Contract.ReviewReportRequest(
            executorId,
            instanceId,
            "lease-unknown-authority",
            1,
            "unknown-authority-report",
            "Pass",
            null,
            "The report must fail at the authority boundary.",
            new Contract.ReviewWorkspaceProofDto(
                "repository-unknown-authority",
                expectedSha,
                expectedSha,
                expectedSha,
                false,
                false,
                new string('c', 64),
                "review-unknown-authority-f1"),
            new Contract.ReviewEnvironmentDto(
                "review-host",
                executorId,
                instanceId,
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [],
            [],
            [new Contract.ReviewVerdictDto(
                "build-tests",
                "pass",
                "Verified",
                "Build and tests passed.")],
            1);

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        // Warm the host first: the invariant under test is that the REPORT
        // returns quickly at the authority boundary, not that a cold Kestrel
        // pipeline JITs within the same budget - on a loaded Windows host the
        // first request alone can eat the two seconds.
        (await http.GetAsync("/healthz")).EnsureSuccessStatusCode();
        using var response = await http.PostAsJsonAsync(
                "/api/v1/reviews/attempts/review_unknown_authority/report",
                request)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<Contract.ApiError>();
        Assert.NotNull(error);
        Assert.Equal(AttemptWriteStatus.LeaseExpired.ToString(), error.Code);
    }

    private static Contract.ReviewReportRequest InfrastructureReport(
        Contract.ReviewClaimResponse claim,
        string runnerId,
        string instanceId,
        string idempotencyKey,
        string failureClassification = "SnapshotUnavailable",
        string summary = "The immutable snapshot was unavailable.")
    {
        return new Contract.ReviewReportRequest(
            runnerId,
            instanceId,
            claim.Lease!.LeaseId,
            claim.Lease.Fence,
            idempotencyKey,
            "ReviewInfra",
            failureClassification,
            summary,
            new Contract.ReviewWorkspaceProofDto(
                claim.Subject!.RepositoryId,
                claim.Subject.ExpectedResultSha,
                claim.Subject.ExpectedResultSha,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                false,
                false,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                claim.Lease.ResourceNamespace),
            new Contract.ReviewEnvironmentDto(
                "review-host",
                runnerId,
                instanceId,
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [],
            [],
            [],
            claim.Lease.AuthorityEpoch);
    }

    private static async Task<Runner::AgentRunner.RunnerClaimResponse> ClaimWithSuccessfulPreflightAsync(
        RClient client,
        RClaim request)
    {
        var offered = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, offered.Status);
        Assert.False(string.IsNullOrWhiteSpace(offered.ProjectId));
        Assert.False(string.IsNullOrWhiteSpace(offered.RepositoryUrl));
        Assert.False(string.IsNullOrWhiteSpace(offered.RegistrationFingerprint));

        return await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                offered.ProjectId!, offered.RegistrationFingerprint!, true,
                "clone/fetch URLs match registration; write probe succeeded",
                DateTime.UtcNow, offered.RepositoryUrl!, offered.RepositoryUrl!),
        }, CancellationToken.None);
    }

    private static async Task<string> RegisterCodingRunnerAsync(
        RClient client,
        HttpClient http,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? cliStatuses = null,
        DateTime? advertisedAt = null)
    {
        var clientId = await client.RegisterAsync(ProjectName, "service", ct);
        var instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                ProjectName,
                "test-host",
                instanceId,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor]),
            ct);
        registration.EnsureSuccessStatusCode();
        await AdvertiseCodingCapabilitiesAsync(
            http,
            cliStatuses
            ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "ready",
                ["codex"] = "ready",
            },
            advertisedAt,
            ct);
        return clientId;
    }

    private static async Task AdvertiseCodingCapabilitiesAsync(
        HttpClient http,
        IReadOnlyDictionary<string, string> cliStatuses,
        DateTime? advertisedAt = null,
        CancellationToken ct = default)
    {
        var capabilities = new List<Contract.AdvertisedCapabilityDto>
        {
            new(Contract.CapabilityProtocol.CodingExecutor, "executor"),
            new(Contract.CapabilityProtocol.GitFetch, "source"),
            new(Contract.CapabilityProtocol.GitPush, "source"),
            new(Contract.CapabilityProtocol.RepositoryAccess, "source"),
            new(Contract.CapabilityProtocol.Disk, "foundation"),
            new(Contract.CapabilityProtocol.TaskServerConnectivity, "foundation"),
        };
        foreach (var (cliType, status) in cliStatuses)
        {
            capabilities.Add(new Contract.AdvertisedCapabilityDto(
                Contract.CapabilityProtocol.CliExecution(cliType),
                "cli-execution",
                status));
            capabilities.Add(new Contract.AdvertisedCapabilityDto(
                Contract.CapabilityProtocol.ProviderAuthentication(cliType),
                "provider-auth",
                status));
        }
        var response = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/capabilities",
            new Contract.CapabilityAdvertisementRequest(
                RunnerId,
                $"{Environment.MachineName}:{Environment.ProcessId}",
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                advertisedAt ?? DateTime.UtcNow,
                180,
                DateTime.UtcNow.Ticks,
                capabilities),
            ct);
        response.EnsureSuccessStatusCode();
    }

    private static JsonSerializerOptions CreateApiJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static async Task AddRepositoryUrlAsync(
        HttpClient http,
        string repositoryUrl,
        string projectId = "PROJ-001")
    {
        var response = await http.PostAsJsonAsync(
            $"/api/projects/{projectId}/urls",
            new { label = "repo", url = repositoryUrl });
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssignRemoteAsync(
        HttpClient http,
        string projectName = ProjectName)
    {
        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{projectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
    }

    private ROptions RunnerOptions(
        string cliBin,
        int hostMaxParallelism = 1,
        string? claudeCliBin = null) => new()
    {
        ServerUrl = "http://in-process",
        RunnerId = RunnerId,
        RunnerName = ProjectName,
        Hostname = Environment.MachineName,
        BackendName = "remote-runner",
        WorkDir = Path.Combine(_workspace, "remote-runner-work"),
        StateDir = Path.Combine(_workspace, "remote-runner-work", ".runner-state"),
        BaseBranch = "main",
        CliBin = cliBin,
        ClaudeCliBin = claudeCliBin ?? "claude",
        CliArgs = "",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 30,
        HostMaxParallelism = hostMaxParallelism,
        PollSeconds = 1,
    };

    private void SeedTask(
        string state, string key, string title, string promptBody,
        string kind = TaskKinds.Task, string? cliType = null, string? model = null,
        string? thinkingLevel = null, string? watchPath = null, int order = 1)
    {
        var dir = Path.Combine(watchPath ?? _watchPath, state, key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, title, state, order, agent = cliType ?? "claude", kind, cliType, model, thinkingLevel,
        }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");
    }

    private async Task<string> SeedOriginAsync()
    {
        var origin = Path.Combine(_workspace, "origin.git");
        var seed = Path.Combine(_workspace, "origin-seed");
        // Pin the bare HEAD: without it the origin's default branch is whatever
        // the host's init.defaultBranch says, and a clone then may or may not
        // auto-check-out "main" (Git for Windows configures "main" system-wide,
        // stock git still defaults to "master").
        await GitAsync(_workspace, "init", "--bare", "--initial-branch=main", origin);
        await GitAsync(_workspace, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        return origin;
    }

    /// <summary>Idle host: the review-slot admission gate opens.</summary>
    private static RTelemetry IdleHostTelemetry(int activeSlots) => new(
        DateTime.UtcNow, 1, 0.0, 0.0, 0.0, 1_000_000_000, 64_000_000_000,
        0, 0, 0, 0, Environment.ProcessorCount, activeSlots);

    /// <summary>One full load per core: the admission gate closes.</summary>
    private static RTelemetry BusyHostTelemetry(int activeSlots) => new(
        DateTime.UtcNow, 100, Environment.ProcessorCount, Environment.ProcessorCount,
        Environment.ProcessorCount, 32_000_000_000, 64_000_000_000,
        0, 0, 0, 0, Environment.ProcessorCount, activeSlots);

    /// <summary>
    /// A review-plan command that appends one line to a file. The plan carries
    /// a concrete executable, and the production plans name the Linux review
    /// host's <c>/bin/sh</c> - which does not exist on the Windows dev host
    /// this MachineBound class also runs on, where the always-present
    /// <c>powershell.exe</c> stands in.
    /// </summary>
    private static Contract.ReviewCommandDto AppendLineCommand(
        string stepId, string path, string line)
        => OperatingSystem.IsWindows()
            ? new Contract.ReviewCommandDto(
                stepId,
                "build-tests",
                "powershell",
                ["-NoProfile", "-Command", $"Add-Content -LiteralPath '{path}' -Value '{line}'"])
            : new Contract.ReviewCommandDto(
                stepId,
                "build-tests",
                "/bin/sh",
                ["-c", $"printf '{line}\\n' >> '{path}'"]);

    /// <summary>
    /// Like <see cref="AppendLineCommand"/> but parks until
    /// <paramref name="awaitedFile"/> exists first - the in-flight command a
    /// daemon restart must adopt rather than repeat.
    /// </summary>
    private static Contract.ReviewCommandDto AwaitFileThenAppendLineCommand(
        string stepId, string awaitedFile, string path, string line)
        => OperatingSystem.IsWindows()
            ? new Contract.ReviewCommandDto(
                stepId,
                "build-tests",
                "powershell",
                [
                    "-NoProfile",
                    "-Command",
                    $"while (-not (Test-Path -LiteralPath '{awaitedFile}')) {{ Start-Sleep -Milliseconds 50 }}; "
                    + $"Add-Content -LiteralPath '{path}' -Value '{line}'",
                ])
            : new Contract.ReviewCommandDto(
                stepId,
                "build-tests",
                "/bin/sh",
                [
                    "-c",
                    $"while [ ! -f '{awaitedFile}' ]; do sleep 0.05; done; "
                    + $"printf '{line}\\n' >> '{path}'",
                ]);

    [Fact]
    public async Task Review_host_runs_tool_and_agent_aspect_end_to_end_with_honest_step_location()
    {
        if (OperatingSystem.IsWindows()) return;
        const string reviewRunnerId = "review-runner-pipeline-parity";
        var origin = await SeedOriginAsync();
        var resultSha = (await GitAsync(origin, "rev-parse", "refs/heads/main")).StdOut.Trim();
        var fakeCodex = Path.Combine(_workspace, "fake-review-codex.sh");
        await File.WriteAllTextAsync(fakeCodex, """
            #!/bin/sh
            printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"Exact subject reviewed.\n[[ASPECT_VERDICT: status=pass; summary=Remote code quality aspect passed.]]"}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":34,"output_tokens":9,"cached_input_tokens":7}}'
            """);
        File.SetUnixFileMode(
            fakeCodex,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SeedTask(
            TaskStates.AutoReview,
            TaskKey,
            "Run the remote pipeline",
            "Execute one tool gate and one semantic aspect on the review host.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(origin)!;
        var run = authority.AcquireRun(
            TaskKey,
            repositoryId,
            null,
            RunnerId,
            "coding-host",
            120,
            "remote-pipeline-run").RunAttempt!;
        var completed = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "remote-pipeline-run-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            Reason = null,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                repositoryId,
                run.AttemptId,
                resultSha,
                resultSha,
                "refs/heads/main",
                null,
                new string('c', 64),
                RepositoryUrl: origin),
        });
        Assert.True(completed.Accepted);
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(TaskKey, _watchPath)!;
        var remotePlan = factory.Services.GetRequiredService<RemoteReviewPlanBuilder>()
            .Build(task, repositoryPath: null, projectSettings: null, "refs/heads/main");
        Assert.Contains(remotePlan.Commands, command =>
            command.ExecutionKind == Contract.ReviewCommandKinds.Tool);
        Assert.Contains(remotePlan.Commands, command =>
            command.ExecutionKind == Contract.ReviewCommandKinds.AgentAspect
            && command.StepId == "aspect-code-quality");
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey,
            repositoryId,
            resultSha,
            run.AttemptId,
            "requirements",
            "remote-pipeline-policy",
            [],
            "remote-pipeline-review-create",
            RepositoryUrl: origin,
            ResultRef: "refs/heads/main",
            Plan: remotePlan));
        Assert.True(created.Accepted);

        var options = ReviewRunnerOptions(
            reviewRunnerId,
            claimMaxLoadPerCore: double.MaxValue,
            codexCliBin: fakeCodex);
        using var client = new RClient(
            http,
            reviewRunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "review-host:pipeline-parity");
        await client.EnsureCompatibleAsync(CancellationToken.None);
        await client.RegisterAsync(
            "pipeline parity review host",
            "review-executor",
            CancellationToken.None);
        var claim = await client.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(
                reviewRunnerId,
                "review-host:pipeline-parity",
                120,
                AvailableSlots: 1),
            CancellationToken.None);
        Assert.Equal("claimed", claim.Status);

        var workspace = new RReviewWorkspace(
            options,
            claim.Subject!,
            claim.Lease!,
            _ => { });
        await workspace.PrepareAsync(client, CancellationToken.None);
        var evidence = await workspace.ExecutePlanAsync(CancellationToken.None);
        var report = await client.ReportReviewAsync(
            claim.Attempt!.AttemptId,
            new Contract.ReviewReportRequest(
                claim.Lease!.ExecutorId,
                claim.Lease.InstanceId,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "remote-pipeline-review-report",
                evidence.Outcome,
                null,
                "Remote tool and aspect steps passed.",
                evidence.Workspace,
                workspace.EnvironmentEvidence(),
                evidence.Commands,
                evidence.Artifacts,
                evidence.Verdicts,
                claim.Lease.AuthorityEpoch),
            CancellationToken.None);
        Assert.Equal("Pass", report.Outcome);

        var humanFolder = Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey);
        Assert.True(Directory.Exists(humanFolder));

        // Evidence projection (AGT-2762) now runs on a background queue after
        // the settlement response, so the aspect files and pipeline/timeline
        // entries it writes are not guaranteed to exist the instant the report
        // call returns. Wait on the timeline entries specifically: they are
        // the last thing ProjectAsync writes, after the pipeline steps and
        // aspect files, so their presence orders-after everything else below.
        // A transient IOException (e.g. racing this same request's own lane
        // move) sends a projection through one 5s-backoff retry, so the
        // budget here is longer than WaitUntilAsync's default 10s.
        var timelineLog = factory.Services.GetRequiredService<TimelineLog>();
        await WaitUntilAsync(
            () => timelineLog.ReadAll(humanFolder).Any(item =>
                item.Kind == TimelineEventKinds.PostStepFinished
                && item.RunId == created.ReviewAttempt!.AttemptId
                && item.Details?["pipelineStepId"] == PipelineCatalogue.BuildTestGateStepId),
            () => "Evidence projection did not record the tool-gate timeline entry before the wait budget expired.",
            attempts: 400);

        var pipeline = factory.Services.GetRequiredService<PipelineExecutionLog>().Read(humanFolder)!;
        var tool = Assert.Single(pipeline.Steps, step =>
            step.StepId == PipelineCatalogue.BuildTestGateStepId);
        var aspect = Assert.Single(pipeline.Steps, step =>
            step.StepId == "aspect-code-quality");
        Assert.Equal("remote", tool.ExecutionLocation);
        Assert.Equal("remote", aspect.ExecutionLocation);
        Assert.Equal("review-host", aspect.ExecutionHostId);
        Assert.Equal(reviewRunnerId, aspect.ExecutionExecutorId);
        Assert.Equal(created.ReviewAttempt!.AttemptId, aspect.ExecutionAttemptId);
        Assert.Equal(34, aspect.InputTokens);
        Assert.True(File.Exists(Path.Combine(humanFolder, "aspect-code-quality.md")));
        Assert.True(File.Exists(Path.Combine(humanFolder, "aspect-code-quality.json")));

        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(humanFolder);
        var remoteSteps = timeline.Where(item =>
                item.Kind == TimelineEventKinds.PostStepFinished
                && item.RunId == created.ReviewAttempt.AttemptId)
            .ToArray();
        Assert.Contains(remoteSteps, item =>
            item.Details?["pipelineStepId"] == PipelineCatalogue.BuildTestGateStepId);
        Assert.Contains(remoteSteps, item =>
            item.Details?["stepId"] == "aspect-code-quality");
        Assert.All(remoteSteps, item => Assert.Equal("remote", item.Details?["executionLocation"]));
        Assert.All(remoteSteps, item => Assert.Equal("review-host", item.Details?["hostId"]));

        Assert.True(await workspace.CleanupAsync());
        var cleanup = await client.CleanupReviewAsync(
            claim.Attempt.AttemptId,
            new Contract.ReviewCleanupRequest(
                claim.Lease.ExecutorId,
                claim.Lease.InstanceId,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "remote-pipeline-review-cleanup",
                WorkspaceRemoved: true,
                AuthorityEpoch: claim.Lease.AuthorityEpoch),
            CancellationToken.None);
        Assert.Equal("cleaned", cleanup.Status);
    }

    [Fact]
    public async Task Review_daemon_restart_adopts_six_in_flight_workers_without_losing_or_repeating_commands()
    {
        const string reviewRunnerId = "review-runner-restart";
        const int reviewCount = 6;
        var origin = await SeedOriginAsync();
        var resultSha = (await GitAsync(origin, "rev-parse", "refs/heads/main")).StdOut.Trim();
        var release = Path.Combine(_workspace, "release-review-command");

        using var factory = BuildFactory();
        using var httpOne = factory.CreateClient();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(origin)!;
        var reviews = new List<(string TaskKey, string Marker, string AttemptId)>();
        for (var index = 1; index <= reviewCount; index++)
        {
            var taskKey = $"{TaskKey}-{index:D2}";
            var marker = Path.Combine(_workspace, $"review-command-marker-{index:D2}.txt");
            SeedTask(
                TaskStates.AutoReview,
                taskKey,
                $"Adopt review {index} across daemon restart",
                "Run both review commands once.",
                order: index);
            var run = authority.AcquireRun(
                taskKey,
                repositoryId,
                null,
                RunnerId,
                "coding-host",
                120,
                $"restart-review-run-{index:D2}").RunAttempt!;
            var completed = authority.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(
                    run.AttemptId,
                    run.LastFence,
                    run.AuthorityEpoch,
                    $"restart-review-run-complete-{index:D2}"),
                Outcome = "done",
                ResultSha = resultSha,
                Reason = null,
                ResultEnvelope = new Contract.ImmutableResultEnvelope(
                    repositoryId,
                    run.AttemptId,
                    resultSha,
                    resultSha,
                    "refs/heads/main",
                    null,
                    new string('a', 64),
                    RepositoryUrl: origin),
            });
            Assert.True(completed.Accepted);
            var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                taskKey,
                repositoryId,
                resultSha,
                run.AttemptId,
                "requirements",
                "restart-adoption-policy",
                [],
                $"restart-review-create-{index:D2}",
                RepositoryUrl: origin,
                ResultRef: "refs/heads/main",
                Plan: new Contract.ReviewPlanDto(
                    [
                        AppendLineCommand("completed-before-restart", marker, "first"),
                        AwaitFileThenAppendLineCommand(
                            "in-flight-during-restart", release, marker, "second"),
                    ],
                    ["build-tests"])));
            Assert.True(created.Accepted);
            reviews.Add((taskKey, marker, created.ReviewAttempt!.AttemptId));
        }

        var options = ReviewRunnerOptions(
            reviewRunnerId,
            hostMaxParallelism: reviewCount,
            claimMaxLoadPerCore: double.MaxValue);
        var firstLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var firstClient = new RClient(
            httpOne,
            reviewRunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "review-host:generation-1");
        using var firstStop = new CancellationTokenSource();
        // Deterministic idle-host telemetry: the real sampler reads
        // /proc/loadavg, which does not exist on Windows (admission would stay
        // closed forever) and reports whatever the host happens to be doing on
        // Linux. This daemon must be admitted, so its host is idle by fiat.
        var firstDaemon = new RReviewDaemon(
            options,
            firstClient,
            firstLogs.Enqueue,
            telemetryProbe: (activeSlots, _) => IdleHostTelemetry(activeSlots));
        var firstRun = firstDaemon.RunAsync(firstStop.Token);

        // Each detached worker is a separate dotnet process. Seeing the first
        // checkpoint from every slot proves that all six reviews are running
        // concurrently and parked inside their second command at shutdown.
        await WaitUntilAsync(
            () => reviews.All(review =>
                TryReadAllLines(review.Marker) is { } lines
                && lines.SequenceEqual(["first"])),
            () => "not all first review commands completed; daemon log:\n"
                  + string.Join("\n", firstLogs),
            attempts: 1200);
        var firstSlots = new RReviewStateStore(options.StateDir).LoadAll();
        Assert.Equal(reviewCount, firstSlots.Count);
        Assert.Equal(reviewCount, firstSlots.Select(slot => slot.ProcessId).Distinct().Count());
        Assert.All(firstSlots, slot =>
        {
            Assert.NotNull(slot.ProcessId);
            Assert.Equal("review-host:generation-1", slot.Claim.Lease!.InstanceId);
            using var process = Process.GetProcessById(slot.ProcessId.Value);
            Assert.False(process.HasExited);
        });
        var originalFences = firstSlots.ToDictionary(
            slot => slot.AttemptId,
            slot => slot.Claim.Lease!.Fence,
            StringComparer.Ordinal);

        firstStop.Cancel();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(reviews, review => Assert.Contains(firstLogs, line => line.Contains(
            $"attempt={review.AttemptId}",
            StringComparison.Ordinal)
            && line.Contains(
                "detached worker left running for replacement adoption",
                StringComparison.Ordinal)));

        using var httpTwo = factory.CreateClient();
        var replacementOptions = ReviewRunnerOptions(
            reviewRunnerId,
            // Leave one nominal slot so the busy-host admission signal, not
            // the capacity ceiling, proves that no fresh claim can slip in.
            hostMaxParallelism: reviewCount + 1,
            claimMaxLoadPerCore: double.Epsilon);
        var secondLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var secondClient = new RClient(
            httpTwo,
            reviewRunnerId,
            usesDurableTaskServer: true,
            options: replacementOptions,
            runnerInstanceId: "review-host:generation-2");
        using var secondStop = new CancellationTokenSource();
        // Deterministic busy-host telemetry: this daemon must adopt the
        // persisted review but keep NEW claims closed by the load gate. One
        // full load per core is at or above the epsilon threshold on any host;
        // the real sampler could report exactly 0.00 on an idle box and never
        // close the gate.
        var secondDaemon = new RReviewDaemon(
            replacementOptions,
            secondClient,
            secondLogs.Enqueue,
            telemetryProbe: (activeSlots, _) => BusyHostTelemetry(activeSlots));
        var secondRun = secondDaemon.RunAsync(secondStop.Token);

        await WaitUntilAsync(
            () => reviews.All(review => secondLogs.Any(line =>
                line.Contains($"attempt={review.AttemptId}", StringComparison.Ordinal)
                && line.Contains("adopting persisted review", StringComparison.Ordinal))),
            () => "replacement daemon did not adopt all persisted reviews; daemon log:\n"
                  + string.Join("\n", secondLogs),
            attempts: 1200);
        await WaitUntilAsync(
            () => reviews.All(review => secondLogs.Any(line =>
                line.Contains($"attempt={review.AttemptId}", StringComparison.Ordinal)
                && line.Contains("review adoption lease verified", StringComparison.Ordinal))),
            () => "replacement daemon did not verify every adopted lease; daemon log:\n"
                  + string.Join("\n", secondLogs),
            attempts: 1200);
        await WaitUntilAsync(
            () => secondLogs.Any(line => line.Contains(
                "review slot admission closed: load/core",
                StringComparison.Ordinal)),
            "load admission did not close while the persisted review continued",
            attempts: 600);
        await File.WriteAllTextAsync(release, "continue");
        // From here every step rides on the detached worker process plus a
        // daemon poll plus server-side moves; each observation gets the same
        // generous budget instead of a knife-edge 10 s.
        await WaitUntilAsync(
            () => reviews.All(review => Directory.Exists(
                Path.Combine(_watchPath, TaskStates.HumanReview, review.TaskKey))),
            () => "not every adopted review reached Human Review; daemon log:\n"
                  + string.Join("\n", secondLogs),
            attempts: 1200);
        // Slot-state hygiene rides on the adopting daemon noticing the worker's
        // exit and removing the workspace (read-only git objects, Windows file
        // handles) before it deletes the state file. Poll gently - a tight
        // LoadAll loop holds the very files the daemon is deleting - and give
        // the slow path a real budget; on timeout the daemon names its reason.
        await WaitUntilAsync(
            () => !new RReviewStateStore(options.StateDir).LoadAll().Any(),
            () => "adopted review state was not cleaned up; replacement daemon log:\n"
                  + string.Join("\n", secondLogs),
            attempts: 240,
            delayMs: 500);

        secondStop.Cancel();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(reviews, review =>
        {
            Assert.Equal(["first", "second"], File.ReadAllLines(review.Marker));
            Assert.Contains(secondLogs, line =>
                line.Contains($"attempt={review.AttemptId}", StringComparison.Ordinal)
                && line.Contains("review report accepted", StringComparison.Ordinal));
            var terminal = authority.GetReview(review.AttemptId)!;
            Assert.Equal(AttemptLifecycleState.Completed, terminal.State);
            Assert.Equal(ReviewTerminalOutcome.Pass, terminal.Outcome);
            Assert.Equal(originalFences[review.AttemptId], terminal.LastFence);
            Assert.Single(terminal.Reports);
        });
    }

    [Fact]
    public async Task Backend_restart_during_active_review_is_re_adopted_and_report_lands()
    {
        const string reviewRunnerId = "review-runner-server-restart";
        const string reviewInstance = "review-host:server-restart";
        var now = DateTime.UtcNow;
        SeedTask(
            TaskStates.AutoReview,
            TaskKey,
            "Review survives backend restart",
            "Report the review after authority is re-adopted.");
        var generatedResultPath = Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey, "status.md");
        var generatedResult = Encoding.UTF8.GetBytes(
            "# Status\n\n- Result: Success\n- Case: feature\n\n## What Was Done\n\n- Result predates backend restart.\n");
        File.WriteAllBytes(generatedResultPath, generatedResult);

        Contract.ReviewClaimResponse claim;
        using (var firstFactory = BuildFactory(authorityNow: () => now))
        using (var firstHttp = firstFactory.CreateClient())
        {
            var authority = firstFactory.Services.GetRequiredService<AttemptAuthorityService>();
            var run = authority.AcquireRun(
                TaskKey,
                "PROJ-RESTART",
                null,
                RunnerId,
                "coding-host",
                30,
                "review-restart-source").RunAttempt!;
            Assert.True(authority.SettleRun(new SettleRunAttemptRequest
            {
                Write = new AttemptWriteReference(
                    run.AttemptId,
                    run.LastFence,
                    run.AuthorityEpoch,
                    "review-restart-source-complete"),
                Outcome = "done",
                ResultSha = "589c462f589c462f589c462f589c462f589c462f",
                ResultEnvelope = new Contract.ImmutableResultEnvelope(
                    "PROJ-RESTART",
                    run.AttemptId,
                    "4136f00d4136f00d4136f00d4136f00d4136f00d",
                    "589c462f589c462f589c462f589c462f589c462f",
                    "refs/heads/restart-review-result",
                    null,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            }).Accepted);
            Assert.True(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
                TaskKey,
                "PROJ-RESTART",
                "589c462f589c462f589c462f589c462f589c462f",
                run.AttemptId,
                "requirements",
                "policy",
                [],
                "review-restart-create")).Accepted);

            await RegisterReviewExecutorAsync(firstHttp, reviewRunnerId, reviewInstance);
            var claimed = await firstHttp.PostAsJsonAsync(
                $"/api/v1/runners/{reviewRunnerId}/review-claims",
                new Contract.ReviewClaimRequest(
                    reviewRunnerId,
                    reviewInstance,
                    30,
                    AvailableSlots: 1));
            claimed.EnsureSuccessStatusCode();
            claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
            Assert.Equal("claimed", claim.Status);
        }

        now = now.AddMinutes(2);
        using var restartedFactory = BuildFactory(authorityNow: () => now);
        using var restartedHttp = restartedFactory.CreateClient();
        restartedHttp.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var registration = await restartedHttp.PutAsJsonAsync(
            $"/api/v1/runners/{reviewRunnerId}",
            new Contract.RegisterRunnerRequest(
                reviewRunnerId,
                "review-host",
                reviewInstance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.BaselineComparison,
                    Contract.ReviewCapabilities.DependencyPreparation,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ],
                ActiveAttempts:
                [
                    new Contract.RunnerActiveAttempt(
                        Contract.RunnerAttemptKinds.Review,
                        claim.Attempt!.AttemptId,
                        claim.Attempt.TaskId,
                        claim.Lease!.LeaseId,
                        claim.Lease.Fence,
                        claim.Lease.AuthorityEpoch,
                        claim.Lease.InstanceId),
                ],
                AttemptLeaseTtlSeconds: 900));
        registration.EnsureSuccessStatusCode();
        var registered = await registration.Content.ReadFromJsonAsync<Contract.RunnerDto>();
        Assert.Equal("adopted", Assert.Single(registered!.AttemptAdoptions!).Status);
        Assert.Equal(generatedResult, File.ReadAllBytes(generatedResultPath));
        var reviewHosts = await restartedHttp.GetFromJsonAsync<
            IReadOnlyList<Contract.RunnerCapabilitySnapshotDto>>(
            "/api/v1/management/remote-hosts");
        Assert.Equal(1, Assert.Single(reviewHosts!, host =>
            host.RunnerId == reviewRunnerId).Telemetry!.ActiveSlots);

        var boardStatus = await restartedHttp.GetFromJsonAsync<AutoReviewStatusView>(
            "/api/auto-review/status");
        Assert.Contains(boardStatus!.ActiveJobs, activity =>
            activity.Project == ProjectName && activity.JobId == TaskKey);

        var report = await restartedHttp.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}/report",
            InfrastructureReport(
                claim,
                reviewRunnerId,
                reviewInstance,
                "review-report-after-server-restart"));
        report.EnsureSuccessStatusCode();
        var terminal = restartedFactory.Services
            .GetRequiredService<AttemptAuthorityService>()
            .GetReview(claim.Attempt.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Failed, terminal.State);
        Assert.Single(terminal.Reports);
    }

    [Fact]
    public async Task Backend_restart_during_coding_run_is_re_adopted_and_completion_lands()
    {
        const string codingInstance = "coding-host:server-restart";
        var now = DateTime.UtcNow;
        SeedTask(
            TaskStates.Progress,
            TaskKey,
            "Coding survives backend restart",
            "Complete after authority is re-adopted.");

        RunAttemptDto run;
        using (var firstFactory = BuildFactory(authorityNow: () => now))
        {
            _ = firstFactory.CreateClient();
            run = firstFactory.Services.GetRequiredService<AttemptAuthorityService>().AcquireRun(
                TaskKey,
                "PROJ-RESTART",
                null,
                RunnerId,
                "coding-host",
                30,
                "coding-restart-claim",
                clientId: RunnerId,
                leaseInstanceId: codingInstance).RunAttempt!;
        }

        now = now.AddMinutes(2);
        using var restartedFactory = BuildFactory(authorityNow: () => now);
        using var restartedHttp = restartedFactory.CreateClient();
        restartedHttp.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var registration = await restartedHttp.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                ProjectName,
                "coding-host",
                codingInstance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor],
                ActiveAttempts:
                [
                    new Contract.RunnerActiveAttempt(
                        Contract.RunnerAttemptKinds.Coding,
                        run.AttemptId,
                        run.TaskKey,
                        run.Lease!.LeaseId,
                        run.LastFence,
                        run.AuthorityEpoch,
                        codingInstance),
                ],
                AttemptLeaseTtlSeconds: 120));
        registration.EnsureSuccessStatusCode();
        var registered = await registration.Content.ReadFromJsonAsync<Contract.RunnerDto>();
        Assert.Equal("adopted", Assert.Single(registered!.AttemptAdoptions!).Status);
        var codingHosts = await restartedHttp.GetFromJsonAsync<
            IReadOnlyList<Contract.RunnerCapabilitySnapshotDto>>(
            "/api/v1/management/remote-hosts");
        Assert.Equal(1, Assert.Single(codingHosts!, host =>
            host.RunnerId == RunnerId).Telemetry!.ActiveSlots);
        var runnerBadge = restartedFactory.Services
            .GetRequiredService<TaskRunnerService>()
            .ResolveRunnerBadge(TaskKey);
        Assert.Equal(run.AttemptId, runnerBadge!.AttemptId);
        Assert.Equal(RunnerId, runnerBadge.RunnerId);

        var completion = await restartedHttp.PostAsJsonAsync(
            "/api/runner/completion",
            new RemoteRunCompletionRequest(
                TaskKey,
                run.Lease.LeaseId,
                run.LastFence,
                RunnerId,
                "Blocked",
                Reason: "controlled server-restart integration test",
                GateItems:
                [
                    "worktree-blocked: controlled restart test retains the fenced worker authority.",
                ],
                AttemptId: run.AttemptId,
                AuthorityEpoch: run.AuthorityEpoch,
                IdempotencyKey: "coding-completion-after-server-restart"));
        completion.EnsureSuccessStatusCode();
        var terminal = restartedFactory.Services
            .GetRequiredService<AttemptAuthorityService>()
            .GetRun(run.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Failed, terminal.State);
        Assert.Equal("blocked", terminal.TerminalOutcome);
    }

    [Fact]
    public async Task Review_daemon_restart_reports_non_adoptable_process_with_loss_extent_and_retry_reason()
    {
        const string reviewRunnerId = "review-runner-lost-process";
        var origin = await SeedOriginAsync();
        var resultSha = (await GitAsync(origin, "rev-parse", "refs/heads/main")).StdOut.Trim();
        SeedTask(
            TaskStates.AutoReview,
            TaskKey,
            "Report non-adoptable review",
            "Keep restart loss visible.");

        using var factory = BuildFactory();
        using var firstHttp = factory.CreateClient();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(origin)!;
        var run = authority.AcquireRun(
            TaskKey,
            repositoryId,
            null,
            RunnerId,
            "coding-host",
            120,
            "lost-review-run").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "lost-review-run-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            Reason = null,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                repositoryId,
                run.AttemptId,
                resultSha,
                resultSha,
                "refs/heads/main",
                null,
                new string('b', 64),
                RepositoryUrl: origin),
        });
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey,
            repositoryId,
            resultSha,
            run.AttemptId,
            "requirements",
            "lost-process-policy",
            [],
            "lost-review-create",
            RepositoryUrl: origin,
            ResultRef: "refs/heads/main",
            Plan: new Contract.ReviewPlanDto([], [])));
        Assert.True(created.Accepted);

        var options = ReviewRunnerOptions(reviewRunnerId);
        using (var firstClient = new RClient(
                   firstHttp,
                   reviewRunnerId,
                   usesDurableTaskServer: true,
                   options: options,
                   runnerInstanceId: "review-host:lost-generation"))
        {
            await firstClient.RegisterAsync(reviewRunnerId, "review-executor", CancellationToken.None);
            var claim = await firstClient.ClaimReviewAsync(
                new Contract.ReviewClaimRequest(
                    reviewRunnerId,
                    "review-host:lost-generation",
                    120),
                CancellationToken.None);
            Assert.Equal("claimed", claim.Status);
            var repositoryPath = Path.Combine(
                options.ReviewWorkDir,
                claim.Lease!.ResourceNamespace,
                "repository");
            new RReviewStateStore(options.StateDir).Create(claim, repositoryPath);
        }

        using var replacementHttp = factory.CreateClient();
        var replacementLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var replacementClient = new RClient(
            replacementHttp,
            reviewRunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "review-host:replacement-generation");
        using var replacementStop = new CancellationTokenSource();
        var replacementRun = new RReviewDaemon(
                options,
                replacementClient,
                replacementLogs.Enqueue)
            .RunAsync(replacementStop.Token);

        await WaitUntilAsync(
            () => authority.GetReview(created.ReviewAttempt!.AttemptId)?.Outcome
                  == ReviewTerminalOutcome.InfrastructureFailure,
            "non-adoptable review did not produce an infrastructure terminal");
        replacementStop.Cancel();
        await replacementRun.WaitAsync(TimeSpan.FromSeconds(10));

        var terminal = authority.GetReview(created.ReviewAttempt!.AttemptId)!;
        Assert.Equal("ExecutorRestarted", terminal.FailureClassification);
        Assert.Contains("Lost work extent: 0 of 0 review commands completed", terminal.TerminalReason);
        Assert.Contains("no persisted review process identity", terminal.TerminalReason);
        Assert.Contains(replacementLogs, line => line.Contains(
            "settling visible restart loss",
            StringComparison.Ordinal));
        var reportPath = Path.Combine(
            _watchPath,
            TaskStates.AutoReview,
            TaskKey,
            $"remote-review-grade-{created.ReviewAttempt.AttemptId}.md");
        // Evidence projection (AGT-2762) runs on a background queue after the
        // settlement checked above, so the grade file is not guaranteed to
        // exist the instant the daemon's run task completes. A transient
        // IOException sends it through one 5s-backoff retry, so the budget
        // here is longer than WaitUntilAsync's default 10s.
        await WaitUntilAsync(
            () => File.Exists(reportPath),
            "evidence projection did not write the grade file before the wait budget expired",
            attempts: 400);
        Assert.Contains("ExecutorRestarted", File.ReadAllText(reportPath), StringComparison.Ordinal);
        Assert.Contains("Lost work extent", File.ReadAllText(reportPath), StringComparison.Ordinal);
    }

    private ROptions ReviewRunnerOptions(
        string runnerId,
        int hostMaxParallelism = 1,
        double claimMaxLoadPerCore = 1.5,
        string? codexCliBin = null) => new()
    {
        ServerUrl = "http://in-process",
        RunnerId = runnerId,
        RunnerName = runnerId,
        Hostname = "review-host",
        BackendName = "remote-review",
        Role = "review",
        WorkDir = Path.Combine(_workspace, "coding-work-not-used"),
        ReviewWorkDir = Path.Combine(_workspace, "review-work"),
        StateDir = Path.Combine(_workspace, "review-state"),
        BaseBranch = "main",
        CliBin = "unused",
        CliArgs = string.Empty,
        CodexCliBin = codexCliBin ?? "codex",
        TtlSeconds = 120,
        HeartbeatSeconds = 1,
        RunTimeoutSeconds = 30,
        HostMaxParallelism = hostMaxParallelism,
        PollSeconds = 1,
        ClaimMaxLoadPerCore = claimMaxLoadPerCore,
    };

    /// <summary>
    /// Reads all lines, or null while another process still holds the file
    /// (the marker writer's own handle, for the instant of the append).
    /// </summary>
    private static string[]? TryReadAllLines(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static Task WaitUntilAsync(
        Func<bool> condition,
        string failure,
        int attempts = 200)
        => WaitUntilAsync(condition, () => failure, attempts);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<string> failure,
        int attempts = 200,
        int delayMs = 50)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (condition()) return;
            await Task.Delay(delayMs);
        }
        Assert.Fail(failure());
    }

    [Theory]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public async Task Monolith_v1_review_report_after_operator_acceptance_keeps_terminal_lane_and_records_evidence(
        string terminalState)
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string repositoryUrl = "https://example.invalid/agent-studio.git";
        const string reviewRunnerId = "review-runner-post-acceptance";
        const string reviewInstance = "review-host:5252";
        SeedTask(TaskStates.AutoReview, TaskKey, "Accepted before review report", "Keep acceptance terminal.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(repositoryUrl)!;
        var run = authority.AcquireRun(
            TaskKey,
            repositoryId,
            null,
            "coding-runner",
            "coding-host",
            120,
            "post-acceptance-run").RunAttempt!;
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                run.AttemptId,
                run.LastFence,
                run.AuthorityEpoch,
                "post-acceptance-run-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                repositoryId,
                run.AttemptId,
                resultSha,
                resultSha,
                "refs/heads/agent-studio/results/post-acceptance",
                null,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RepositoryUrl: repositoryUrl),
        });
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey,
            repositoryId,
            resultSha,
            run.AttemptId,
            "requirements",
            "policy",
            [],
            "post-acceptance-review",
            RepositoryUrl: repositoryUrl,
            ResultRef: resultSha,
            Plan: new Contract.ReviewPlanDto([], [])));
        Assert.Equal(AttemptWriteStatus.Accepted, created.Status);

        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{reviewRunnerId}",
            new Contract.RegisterRunnerRequest(
                reviewRunnerId,
                "review-host",
                reviewInstance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.BaselineComparison,
                    Contract.ReviewCapabilities.DependencyPreparation,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ]));
        registration.EnsureSuccessStatusCode();

        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);
        var claim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            CancellationToken.None);
        Assert.Equal("claimed", claim.Status);

        var moved = factory.Services.GetRequiredService<TaskStateMachine>().MoveJob(
            TaskKey,
            terminalState,
            _watchPath,
            TimelineActors.Human("operator"));
        Assert.Equal(MoveJobStatus.Success, moved.Status);

        var reportRequest = PassingV1ReviewReport(claim, "post-acceptance-report");
        var report = await reviewClient.ReportReviewAsync(
            claim.Attempt!.AttemptId,
            reportRequest,
            CancellationToken.None);
        var replay = await reviewClient.ReportReviewAsync(
            claim.Attempt.AttemptId,
            reportRequest,
            CancellationToken.None);

        var completedFolder = Path.Combine(_watchPath, terminalState, TaskKey);
        var evidenceFile = Path.Combine(
            completedFolder,
            $"remote-review-grade-{claim.Attempt.AttemptId}.md");
        Assert.Equal(terminalState, report.TaskState);
        // The replay answers the fast idempotent-duplicate path (AGT-2762): it
        // matches the original settlement in every field except
        // EvidenceProjection, which the original leaves queued and the replay
        // reports as an unrepeated duplicate.
        Assert.Equal(Contract.ReviewEvidenceProjectionStatus.Queued, report.EvidenceProjection);
        Assert.Equal(
            report with { EvidenceProjection = Contract.ReviewEvidenceProjectionStatus.Duplicate },
            replay);
        Assert.False(report.RetryScheduled);
        Assert.True(Directory.Exists(completedFolder));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey)));
        // Evidence projection (AGT-2762) now runs on a background queue after
        // the settlement response, so the grade file is not guaranteed to
        // exist the instant the report call returns. A transient IOException
        // sends it through one 5s-backoff retry, so the budget here is longer
        // than WaitUntilAsync's default 10s.
        await WaitUntilAsync(
            () => File.Exists(evidenceFile),
            "evidence projection did not write the grade file before the wait budget expired",
            attempts: 400);
        Assert.Contains("Remote Review Grade", File.ReadAllText(evidenceFile), StringComparison.Ordinal);

        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(completedFolder);
        var recorded = Assert.Single(
            timeline,
            item => item.Kind == TimelineEventKinds.PostAcceptanceReviewReportRecorded);
        Assert.Equal("post-acceptance review report recorded", recorded.Summary);
        Assert.Equal(Path.GetFileName(evidenceFile), recorded.PayloadRef);
        Assert.DoesNotContain(
            timeline,
            item => item.Kind == TimelineEventKinds.LaneChanged
                    && item.Details?.GetValueOrDefault("to") == TaskStates.HumanReview);
    }

    private static Contract.ReviewReportRequest PassingV1ReviewReport(
        Contract.ReviewClaimResponse claim,
        string idempotencyKey)
    {
        var lease = claim.Lease!;
        var subject = claim.Subject!;
        return new Contract.ReviewReportRequest(
            lease.ExecutorId,
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            idempotencyKey,
            "Pass",
            null,
            "Remote review passed after operator acceptance.",
            new Contract.ReviewWorkspaceProofDto(
                subject.RepositoryId,
                subject.ExpectedResultSha,
                subject.ExpectedResultSha,
                "0123456789abcdef0123456789abcdef01234567",
                false,
                false,
                new string('c', 64),
                lease.ResourceNamespace),
            new Contract.ReviewEnvironmentDto(
                lease.HostId,
                lease.ExecutorId,
                lease.InstanceId,
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [],
            [],
            [new Contract.ReviewVerdictDto("build-tests", "pass", "Verified", "Build and tests passed.")],
            lease.AuthorityEpoch);
    }

    /// <summary>
    /// task.json is written without a naming policy, so persisted commit entries
    /// keep their CLR casing while the HTTP projection is camelCase. Reading the
    /// card directly has to tolerate both.
    /// </summary>
    private static string? CommitField(JsonElement commit, string name, string cardJson)
    {
        foreach (var property in commit.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.GetString();
        }
        Assert.Fail($"Persisted commit has no '{name}' field: {cardJson}");
        return null;
    }

    private static async Task<LocalGitResult> GitAsync(
        string cwd, params string[] args)
        => await GitAsync(cwd, args, allowFailure: false);

    private static async Task<LocalGitResult> GitAsync(
        string cwd, string[] args, bool allowFailure)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return new LocalGitResult(process.ExitCode, stdout, stderr);
    }

    [Fact]
    public async Task Monolith_v1_review_plane_claims_and_accepts_fenced_grade_end_to_end()
    {
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string baseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
        const string artifactDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewRunnerId = "review-runner-e2e";
        const string reviewInstance = "review-host:4243";
        SeedTask(TaskStates.Progress, TaskKey, "Remote review plane", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var coding = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await coding.RegisterAsync(ProjectName, "service", ct);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(TaskKey, RunnerId, ProjectName, "coding-host", 4242, "codex"), ct);
        Assert.True(lease.Granted);

        var mismatchedRef = await Assert.ThrowsAsync<Runner::AgentRunner.TaskServerException>(() =>
            coding.CompleteRunAsync(new RRemoteComplete(
                TaskKey,
                lease.Lease!.LeaseId,
                lease.Lease.FencingToken,
                RunnerId,
                "Done",
                ResultSha: resultSha,
                AttemptChainId: lease.Lease.LeaseId,
                Repository: "https://example.invalid/agent-studio.git",
                AttemptId: lease.Lease.AttemptId,
                AuthorityEpoch: lease.Lease.AuthorityEpoch,
                IdempotencyKey: "v1-review-plane-wrong-fence-ref",
                BaseSha: baseSha,
                ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                    lease.Lease.AttemptId!,
                    lease.Lease.FencingToken + 1,
                    resultSha),
                ArtifactManifestDigest: artifactDigest), ct));
        Assert.Equal(400, mismatchedRef.StatusCode);
        Assert.Contains("current fenced attempt", mismatchedRef.Message);

        var completion = await coding.CompleteRunAsync(new RRemoteComplete(
            TaskKey,
            lease.Lease!.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "v1-review-plane-completion",
            BaseSha: baseSha,
            ImmutableResultRef: Contract.FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!,
                lease.Lease.FencingToken,
                resultSha),
            ArtifactManifestDigest: artifactDigest), ct);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);

        var compatibility = await http.PostAsJsonAsync(
            "/api/v1/protocol/compatibility",
            new Contract.ProtocolCompatibilityRequest(
                "review-runner",
                "1.0.0",
                Contract.TaskServerProtocol.Current),
            ct);
        compatibility.EnsureSuccessStatusCode();
        var compatible = await compatibility.Content.ReadFromJsonAsync<Contract.ProtocolCompatibilityResponse>(ct);
        Assert.True(compatible!.Supported);
        Assert.Contains("review-plane", compatible.Server.Capabilities!);

        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{reviewRunnerId}",
            new Contract.RegisterRunnerRequest(
                reviewRunnerId,
                "review-host",
                reviewInstance,
                "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.BaselineComparison,
                    Contract.ReviewCapabilities.DependencyPreparation,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ]),
            ct);
        registration.EnsureSuccessStatusCode();

        using var reviewClient = new RClient(
            http,
            reviewRunnerId,
            usesDurableTaskServer: true);
        var claim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            ct);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(completion.ReviewAttemptId, claim.Attempt!.AttemptId);
        Assert.True(claim.Lease!.Fence > 0);
        Assert.True(claim.Lease.AuthorityEpoch > 0);
        Assert.Equal(resultSha, claim.Subject!.ExpectedResultSha);

        var renewed = await reviewClient.RenewReviewLeaseAsync(
            claim.Attempt.AttemptId,
            new Contract.ReviewLeaseRenewRequest(
                reviewRunnerId,
                reviewInstance,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "v1-review-renew",
                120,
                claim.Lease.AuthorityEpoch),
            ct);
        Assert.Equal(claim.Lease.AuthorityEpoch, renewed.AuthorityEpoch);

        var reportRequest = new Contract.ReviewReportRequest(
            reviewRunnerId,
            reviewInstance,
            claim.Lease.LeaseId,
            claim.Lease.Fence,
            "v1-review-grade",
            "Pass",
            null,
            "Focused .NET gate passed.",
            new Contract.ReviewWorkspaceProofDto(
                claim.Subject.RepositoryId,
                resultSha,
                resultSha,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                false,
                false,
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                claim.Lease.ResourceNamespace),
            new Contract.ReviewEnvironmentDto(
                "review-host",
                reviewRunnerId,
                reviewInstance,
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [],
            [],
            [new Contract.ReviewVerdictDto("build-tests", "pass", "GatePassed", "Focused gate passed.")],
            claim.Lease.AuthorityEpoch);
        var report = await reviewClient.ReportReviewAsync(
            claim.Attempt.AttemptId,
            reportRequest,
            ct);
        Assert.Equal("Pass", report.Outcome);
        Assert.Equal(TaskStates.HumanReview, report.TaskState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, TaskKey)));

        var duplicate = await reviewClient.ReportReviewAsync(
            claim.Attempt.AttemptId,
            reportRequest,
            ct);
        Assert.Equal(report.ReportId, duplicate.ReportId);

        var reportedAttempt = await http.GetFromJsonAsync<Contract.ReviewAttemptDto>(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}",
            ct);
        Assert.Equal("Pass", reportedAttempt!.Outcome);

        var cleanup = await reviewClient.CleanupReviewAsync(
            claim.Attempt.AttemptId,
            new Contract.ReviewCleanupRequest(
                reviewRunnerId,
                reviewInstance,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "v1-review-cleanup",
                true,
                AuthorityEpoch: claim.Lease.AuthorityEpoch),
            ct);
        Assert.Equal("cleaned", cleanup.Status);

        var handoff = await http.GetFromJsonAsync<Contract.ResultHandoffDto>(
            $"/api/v1/runs/{lease.Lease.AttemptId}/result-handoff",
            ct);
        Assert.Equal(resultSha, handoff!.Envelope.ResultSha);
        Assert.Equal(lease.Lease.AttemptId, handoff.Envelope.SourceRunAttemptId);
    }

    [Theory]
    [InlineData("QS")]
    [InlineData("CAC")]
    [InlineData("TE")]
    [Trait("Category", "MachineBound")]
    public async Task Monolith_v1_green_remote_delivery_integrates_before_human_review(
        string deliveryProject)
    {
        const string reviewRunnerId = "review-runner-immediate-integration";
        const string reviewInstance = "review-host:immediate-integration";
        var origin = await SeedOriginAsync();
        await GitAsync(origin, "branch", "develop", "main");
        var repository = Path.Combine(_workspace, "integration-repository");
        await GitAsync(_workspace, "clone", origin, repository);
        await GitAsync(repository, "config", "user.name", "Remote Integration Test");
        await GitAsync(repository, "config", "user.email", "remote-integration@example.invalid");
        await GitAsync(repository, "checkout", "-b", "delivery-work", "origin/develop");
        var baseSha = (await GitAsync(repository, "rev-parse", "origin/develop")).StdOut.Trim();
        await File.WriteAllTextAsync(
            Path.Combine(repository, "remote-immediate.txt"),
            "remote delivery\n");
        await GitAsync(repository, "add", "remote-immediate.txt");
        await GitAsync(repository, "commit", "-m", "feat: remote immediate delivery");
        var resultSha = (await GitAsync(repository, "rev-parse", "HEAD")).StdOut.Trim();
        SeedTask(
            TaskStates.Progress,
            TaskKey,
            "Immediate Remote integration",
            "Deliver and integrate before Human Review.");

        using var factory = BuildFactory(
            repositoryPath: repository,
            primaryProjectName: deliveryProject);
        using var http = factory.CreateClient();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var seededTask = scanner.FindJob(TaskKey, _watchPath)!;
        var canonicalTaskKey = seededTask.Key ?? seededTask.TaskKey;
        factory.Services.GetRequiredService<ProjectSettingsService>()
            .SetIntegrationBranch(deliveryProject, "develop");
        using var coding = new RClient(http, RunnerId);
        var ct = CancellationToken.None;
        await coding.RegisterAsync(deliveryProject, "service", ct);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(canonicalTaskKey, RunnerId, deliveryProject, "coding-host", 4242, "codex"), ct);
        Assert.True(lease.Granted);
        var immutableRef = Contract.FencedGitRefs.ImmutableResult(
            lease.Lease!.AttemptId!,
            lease.Lease.FencingToken,
            resultSha);
        await GitAsync(repository, "push", "origin", $"HEAD:{immutableRef}");
        await GitAsync(repository, "checkout", "main");

        var completion = await coding.CompleteRunAsync(new RRemoteComplete(
            canonicalTaskKey,
            lease.Lease.LeaseId,
            lease.Lease.FencingToken,
            RunnerId,
            "Done",
            ResultSha: resultSha,
            AttemptChainId: lease.Lease.LeaseId,
            Repository: origin,
            AttemptId: lease.Lease.AttemptId,
            AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "immediate-integration-completion",
            BaseSha: baseSha,
            ImmutableResultRef: immutableRef,
            ArtifactManifestDigest: new string('a', 64),
            IntegrationBranch: "refs/heads/develop"), ct);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);

        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(
            http,
            reviewRunnerId,
            usesDurableTaskServer: true);
        var claim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            ct);
        Assert.Equal("claimed", claim.Status);
        var reportRequest = PassingV1ReviewReport(claim, "immediate-integration-review") with
        {
            Summary = "All applicable Remote gates passed; build/test is not applicable.",
            Verdicts =
            [
                new Contract.ReviewVerdictDto(
                    "completion",
                    "pass",
                    "Verified",
                    "The immutable delivery is complete."),
            ],
        };

        var report = await reviewClient.ReportReviewAsync(
            claim.Attempt!.AttemptId,
            reportRequest,
            ct);
        Assert.Equal(TaskStates.HumanReview, report.TaskState);
        var reviewed = scanner.FindJob(canonicalTaskKey, _watchPath)!;
        var ancestry = await GitAsync(
            repository,
            ["merge-base", "--is-ancestor", resultSha, "develop"],
            allowFailure: true);
        var branches = await GitAsync(repository, "branch", "--all", "--verbose");
        Assert.True(
            ancestry.ExitCode == 0,
            $"Delivery did not reach develop: {ancestry.StdErr}\n{branches.StdOut}\n" +
            (File.Exists(Path.Combine(reviewed.FolderPath, PipelineExecutionLog.FileName))
                ? File.ReadAllText(Path.Combine(reviewed.FolderPath, PipelineExecutionLog.FileName))
                : "no pipeline record"));

        Assert.Equal(TaskStates.HumanReview, reviewed.State);
        var integration = Assert.Single(
            factory.Services.GetRequiredService<TaskIntegrationStatusService>()
                .BuildLookup([reviewed]).Values);
        Assert.Equal(IntegrationStatuses.Integrated, integration.Status);

        var timeline = factory.Services.GetRequiredService<TimelineLog>()
            .ReadAll(reviewed.FolderPath)
            .ToList();
        var integratedAt = timeline.FindIndex(item =>
            item.Kind == TimelineEventKinds.IntegrationSucceeded
            && item.Details?.GetValueOrDefault("stage") == "pre-human-review");
        var humanReviewAt = timeline.FindIndex(item =>
            item.Kind == TimelineEventKinds.LaneChanged
            && item.Details?.GetValueOrDefault("to") == TaskStates.HumanReview);
        Assert.True(integratedAt >= 0, "Immediate integration evidence was not recorded.");
        Assert.True(humanReviewAt > integratedAt, "Human Review was entered before integration settled.");

        var developBeforeAcceptance = (await GitAsync(repository, "rev-parse", "develop")).StdOut.Trim();
        var pipelineBeforeAcceptance = factory.Services.GetRequiredService<PipelineExecutionLog>()
            .Read(reviewed.FolderPath)!.Steps.Single(step =>
                step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        var accepted = await factory.Services.GetRequiredService<TaskTransitionService>()
            .MoveAsync(canonicalTaskKey, TaskStates.Completed, _watchPath, ct);
        Assert.Equal(MoveJobStatus.Success, accepted.Status);
        var completedTask = scanner.FindJob(canonicalTaskKey, _watchPath)!;
        Assert.Equal(TaskStates.Completed, completedTask.State);
        Assert.Equal(
            developBeforeAcceptance,
            (await GitAsync(repository, "rev-parse", "develop")).StdOut.Trim());
        var pipelineAfterAcceptance = factory.Services.GetRequiredService<PipelineExecutionLog>()
            .Read(completedTask.FolderPath)!.Steps.Single(step =>
                step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(pipelineBeforeAcceptance.Status, pipelineAfterAcceptance.Status);
        Assert.Equal(pipelineBeforeAcceptance.Verdict, pipelineAfterAcceptance.Verdict);
        Assert.Equal(pipelineBeforeAcceptance.CompletedAt, pipelineAfterAcceptance.CompletedAt);
    }

    [Fact]
    public async Task Monolith_v1_review_claim_terminalizes_legacy_subject_without_result_envelope()
    {
        const string reviewRunnerId = "review-runner-legacy";
        const string reviewInstance = "review-legacy-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Legacy review subject", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var legacy = SeedReviewAttempt(factory.Services, includeResultEnvelope: false);
        // The claim-time terminalization honours a grace window (rollout of
        // old runner binaries); age the seeded review past it so this test
        // exercises the terminalize path, not the grace.
        factory.Services.GetRequiredService<AttemptAuthorityService>()
            .AgeReviewForTests(legacy.AttemptId, TimeSpan.FromMinutes(16));
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);

        var firstClaim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            CancellationToken.None);
        Assert.Equal("empty", firstClaim.Status);

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var terminal = authority.GetReview(legacy.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Failed, terminal.State);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, terminal.Outcome);
        Assert.Equal("SnapshotUnavailable", terminal.FailureClassification);
        Assert.Equal(
            AttemptAuthorityService.UnmaterializableReviewSubjectReason,
            terminal.TerminalReason);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, TaskKey)));

        var terminalAt = terminal.TerminalAt;
        var secondClaim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            CancellationToken.None);
        Assert.Equal("empty", secondClaim.Status);
        Assert.Equal(terminalAt, authority.GetReview(legacy.AttemptId)!.TerminalAt);
        Assert.Single(authority.GetTaskProjection(TaskKey).ReviewAttempts);
    }

    [Fact]
    public async Task Monolith_v1_review_claim_leaves_an_envelope_less_subject_inside_the_grace_alone()
    {
        const string reviewRunnerId = "review-runner-grace";
        const string reviewInstance = "review-grace-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Fresh review subject", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        // No envelope yet and still inside the grace: the completion ingest may
        // simply be in flight. The subject must neither be killed nor handed to
        // an executor that provably cannot materialize it - it waits.
        var fresh = SeedReviewAttempt(factory.Services, includeResultEnvelope: false);
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);

        var claim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            CancellationToken.None);

        Assert.Equal("empty", claim.Status);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var waiting = authority.GetReview(fresh.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Pending, waiting.State);
        Assert.Null(waiting.Lease);
        Assert.Null(waiting.TerminalAt);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, TaskKey)));
    }

    [Fact]
    public void Monolith_v1_review_terminalization_never_kills_an_actively_leased_subject()
    {
        const string reviewRunnerId = "review-runner-leased";
        const string reviewInstance = "review-leased-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Leased review subject", "Build and verify.");

        using var factory = BuildFactory();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var leased = SeedReviewAttempt(factory.Services, includeResultEnvelope: false);
        var claimed = authority.ClaimReview(
            leased.AttemptId,
            reviewRunnerId,
            "review-host",
            300,
            "leased-envelope-less-claim",
            reviewInstance);
        Assert.True(claimed.Accepted);
        // Age past the grace: the terminalizer would now normally kill it, but a
        // live lease means an executor is working - killing it would clear the
        // lease under the running report.
        authority.AgeReviewForTests(leased.AttemptId, TimeSpan.FromMinutes(16));

        var terminalized = authority.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope();

        Assert.DoesNotContain(terminalized, review => review.AttemptId == leased.AttemptId);
        var survivor = authority.GetReview(leased.AttemptId)!;
        Assert.Equal(AttemptLifecycleState.Leased, survivor.State);
        Assert.NotNull(survivor.Lease);
        Assert.Null(survivor.TerminalAt);
    }

    [Fact]
    public async Task Monolith_v1_review_claim_replaces_a_stale_card_integration_branch()
    {
        const string reviewRunnerId = "review-runner-stale-branch";
        const string reviewInstance = "review-stale-branch-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Stale integration branch", "Build and verify.");
        // AGT-2220 shape: the card still records the pre-30.07. integration line
        // while develop is the project's working branch. A merge-base against
        // main is an ancient commit the baseline commands no longer run on.
        SetCardIntegrationBranch(TaskStates.AutoReview, "refs/heads/main");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        // The project truth the stale card must yield to: the configured
        // integration branch (blank would mean the checkout's origin/HEAD,
        // and this fixture has no checkout).
        factory.Services.GetRequiredService<ProjectSettingsService>()
            .SetIntegrationBranch(ProjectName, "develop");
        SeedReviewAttempt(factory.Services, includeResultEnvelope: true);
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);

        var claim = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
            CancellationToken.None);

        Assert.Equal("claimed", claim.Status);
        Assert.Equal("refs/heads/develop", claim.Subject!.Plan.IntegrationRef);
        Assert.Equal("refs/heads/develop", ReadCardIntegrationBranch(TaskStates.AutoReview));
        var corrected = Assert.Single(
            ReadTimeline(TaskStates.AutoReview),
            entry => entry.GetProperty("kind").GetString() == "integration_branch_corrected");
        var details = corrected.GetProperty("details");
        Assert.Equal("main", details.GetProperty("previousBranch").GetString());
        Assert.Equal("refs/heads/develop", details.GetProperty("integrationRef").GetString());
    }

    [Fact]
    public async Task Monolith_v1_review_plane_names_a_repeated_baseline_failure_on_the_card()
    {
        const string reviewRunnerId = "review-runner-repeat-diagnosis";
        const string reviewInstance = "review-repeat-host:4243";
        const string baselineSha = "b649ff8dab649ff8dab649ff8dab649ff8dab649f";
        SeedTask(TaskStates.AutoReview, TaskKey, "Repeated baseline failure", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        SeedReviewAttempt(factory.Services, includeResultEnvelope: true);
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);

        // Two attempts, one identical infrastructure cause - the AGT-2220 loop
        // that previously left only a classification behind.
        for (var attemptNumber = 1; attemptNumber <= 2; attemptNumber++)
        {
            var claim = await reviewClient.ClaimReviewAsync(
                new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
                CancellationToken.None);
            Assert.Equal("claimed", claim.Status);
            var report = await reviewClient.ReportReviewAsync(
                claim.Attempt!.AttemptId,
                InfrastructureReport(
                    claim,
                    reviewRunnerId,
                    reviewInstance,
                    $"review-baseline-{attemptNumber}",
                    "BaselineUnavailable",
                    Contract.ReviewInfrastructureDiagnosis.Append(
                        "Baseline command 'verify-2' did not complete normally.",
                        [
                            new(Contract.ReviewInfrastructureDiagnosis.BaseKey, baselineSha),
                            new(Contract.ReviewInfrastructureDiagnosis.RefKey, "refs/heads/develop"),
                            new(Contract.ReviewInfrastructureDiagnosis.StepKey, "verify-2"),
                            new(Contract.ReviewInfrastructureDiagnosis.CommandKey, "sh -lc dotnet test"),
                        ])),
                CancellationToken.None);
            Assert.True(report.RetryScheduled);
        }

        var diagnosed = Assert.Single(
            ReadTimeline(TaskStates.AutoReview),
            entry => entry.GetProperty("kind").GetString()
                     == "review_infrastructure_repeat_diagnosed");
        var details = diagnosed.GetProperty("details");
        Assert.Equal("BaselineUnavailable", details.GetProperty("classification").GetString());
        Assert.Equal("2", details.GetProperty("repeatCount").GetString());
        Assert.Equal(baselineSha, details.GetProperty("baselineSha").GetString());
        Assert.Equal("refs/heads/develop", details.GetProperty("integrationRef").GetString());
        Assert.Equal("verify-2", details.GetProperty("step").GetString());
        Assert.Equal("sh -lc dotnet test", details.GetProperty("command").GetString());
        Assert.Contains(
            baselineSha,
            diagnosed.GetProperty("summary").GetString(),
            StringComparison.Ordinal);
    }

    private void SetCardIntegrationBranch(string state, string integrationBranch)
    {
        var path = Path.Combine(_watchPath, state, TaskKey, "task.json");
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(path))!;
        fields["integrationBranch"] = JsonSerializer.SerializeToElement(integrationBranch);
        File.WriteAllText(path, JsonSerializer.Serialize(fields));
    }

    private string? ReadCardIntegrationBranch(string state)
    {
        var path = Path.Combine(_watchPath, state, TaskKey, "task.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("integrationBranch", out var value)
            ? value.GetString()
            : null;
    }

    private List<JsonElement> ReadTimeline(string state)
    {
        var path = Path.Combine(_watchPath, state, TaskKey, "logs", "timeline.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    [Fact]
    public async Task Monolith_v1_review_plane_exhausts_three_infrastructure_retries_to_escalated()
    {
        const string reviewRunnerId = "review-runner-budget";
        const string reviewInstance = "review-budget-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Remote review retry budget", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        SeedReviewAttempt(factory.Services, includeResultEnvelope: true);
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);

        Contract.ReviewReportDto? terminal = null;
        for (var attemptNumber = 1;
             attemptNumber <= AttemptAuthorityService.ReviewInfrastructureRetryBudget + 1;
             attemptNumber++)
        {
            var claim = await reviewClient.ClaimReviewAsync(
                new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
                CancellationToken.None);
            Assert.Equal("claimed", claim.Status);

            var report = await reviewClient.ReportReviewAsync(
                claim.Attempt!.AttemptId,
                InfrastructureReport(
                    claim,
                    reviewRunnerId,
                    reviewInstance,
                    $"review-infra-{attemptNumber}"),
                CancellationToken.None);
            if (attemptNumber <= AttemptAuthorityService.ReviewInfrastructureRetryBudget)
            {
                Assert.True(report.RetryScheduled);
                Assert.Equal(TaskStates.AutoReview, report.TaskState);
            }
            else
            {
                terminal = report;
            }
        }

        Assert.NotNull(terminal);
        Assert.False(terminal.RetryScheduled);
        Assert.Equal(TaskStates.Escalated, terminal.TaskState);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, TaskKey)));

        var projection = factory.Services
            .GetRequiredService<AttemptAuthorityService>()
            .GetTaskProjection(TaskKey);
        Assert.Equal(
            AttemptAuthorityService.ReviewInfrastructureRetryBudget + 1,
            projection.ReviewAttempts.Count);
        Assert.All(
            projection.ReviewAttempts,
            attempt => Assert.Equal(
                projection.CurrentReviewSubject!.SubjectId,
                attempt.Subject.SubjectId));
        Assert.DoesNotContain(
            projection.ReviewAttempts,
            attempt => attempt.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased);
    }

    /// <summary>
    /// AGT-2220 acceptance: when the last, youngest attempt of an exhausted
    /// chain carries a HARDER classification than the ones before it, the park
    /// summary must be built from that youngest cause. The frequency reading
    /// ("all attempts were SnapshotUnavailable, so it is a baseline infra
    /// problem") would send the operator after a remedy that cannot work.
    /// </summary>
    [Fact]
    public async Task Monolith_v1_review_escalation_summary_names_the_youngest_failure_class()
    {
        const string reviewRunnerId = "review-runner-divergent";
        const string reviewInstance = "review-divergent-host:4243";
        SeedTask(TaskStates.AutoReview, TaskKey, "Remote review divergent chain", "Build and verify.");

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        SeedReviewAttempt(factory.Services, includeResultEnvelope: true);
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);

        var last = AttemptAuthorityService.ReviewInfrastructureRetryBudget + 1;
        for (var attemptNumber = 1; attemptNumber <= last; attemptNumber++)
        {
            var claim = await reviewClient.ClaimReviewAsync(
                new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120),
                CancellationToken.None);
            Assert.Equal("claimed", claim.Status);

            // The youngest attempt fails differently and harder than its
            // predecessors: an immutable Result-SHA that was never materializable.
            var report = attemptNumber == last
                ? InfrastructureReport(
                    claim, reviewRunnerId, reviewInstance, $"review-infra-{attemptNumber}",
                    "ShaMismatch",
                    "Materialized HEAD '744deb892' does not match expected Result-SHA 'f538f896'.")
                : InfrastructureReport(claim, reviewRunnerId, reviewInstance, $"review-infra-{attemptNumber}");
            await reviewClient.ReportReviewAsync(claim.Attempt!.AttemptId, report, CancellationToken.None);
        }

        var status = await File.ReadAllTextAsync(
            Path.Combine(_watchPath, TaskStates.Escalated, TaskKey, "status.md"));

        // 1. The youngest attempt owns the situation report.
        Assert.Contains("ReviewInfra/ShaMismatch", status);
        Assert.Contains("Materialized HEAD", status);
        Assert.Contains("- Newest attempt: ", status);
        // 2. Every distinct class is enumerated, dated, and none is dropped.
        Assert.Contains("- Failure classifications (2 distinct, complete, newest first):", status);
        Assert.Contains("ReviewInfra/ShaMismatch: 1 attempt, ", status);
        Assert.Contains(
            $"ReviewInfra/SnapshotUnavailable: {AttemptAuthorityService.ReviewInfrastructureRetryBudget} attempts, ",
            status);
        Assert.Contains("- Divergent attempts: 3 of 4 attempts are classified differently", status);
        // 3. The options match the youngest cause, not the majority one.
        Assert.Contains("- Operator options for the newest cause ReviewInfra/ShaMismatch:", status);
        Assert.Contains("Re-run the source coding attempt", status);
        Assert.DoesNotContain("Restore the baseline ref", status);
        // The board's status-stub contract stays intact: exactly one Category
        // line and one Reason line, and the Reason names the youngest cause.
        Assert.Equal(1, CountLines(status, "- Category: "));
        Assert.Equal(1, CountLines(status, "- Reason: "));
        var reason = status
            .Split('\n')
            .First(line => line.StartsWith("- Reason: ", StringComparison.Ordinal));
        Assert.Contains("ReviewInfra/ShaMismatch", reason);
        Assert.Contains("Divergent chain", reason);
    }

    private static int CountLines(string text, string prefix) => text
        .Split('\n')
        .Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

        }

internal sealed record LocalGitResult(int ExitCode, string StdOut, string StdErr);
