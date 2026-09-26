using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableHandoffRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "runner-recovery-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Prelaunch_infrastructure_release_is_not_replayed_after_restart()
    {
        var authority = new RunOutboxAuthority("run-prelaunch", "TASK-11", "runner-a", "old-host:43", "lease-c", 11);
        var outbox = DurableRunOutbox.Open(Path.Combine(_root, "outbox"), authority);
        outbox.Enqueue("status", "{}");
        outbox.RecordHandoffState("abandoned-prelaunch");
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: true);

        await new DurableHandoffRecovery(Options(), client, _ => { }).RecoverAllAsync(default);

        Assert.Equal(0, handler.RenewalCalls);
        Assert.Equal("abandoned-prelaunch", outbox.Snapshot.FinalHandoffState);
    }

    [Fact]
    public async Task Restart_replays_handoff_and_completion_without_starting_a_coding_process()
    {
        var authority = new RunOutboxAuthority(
            "run-recovery",
            "TASK-9",
            "runner-a",
            "old-host:41",
            "lease-a",
            9);
        var outbox = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        var manifest = RemoteTaskRunner.BuildArtifactManifest([]);
        var resultSha = new string('2', 40);
        var envelope = new ImmutableResultEnvelope(
            "repo-9",
            authority.RunId,
            new string('1', 40),
            resultSha,
            FencedGitRefs.ImmutableResult(authority.RunId, authority.Fence, resultSha),
            null,
            manifest.Digest);
        outbox.Enqueue("run-context", JsonSerializer.Serialize(
            new DurableRunContextPayload(
                "repo-9", null, "main", new string('1', 40)),
            WebJson));
        outbox.Enqueue("terminal", JsonSerializer.Serialize(
            new DurableTerminalPayload("Done", null),
            WebJson));
        outbox.Enqueue("artifact-manifest", manifest.Json);
        outbox.Enqueue("final-result", JsonSerializer.Serialize(envelope, WebJson));

        var handler = new RecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-a",
            usesDurableTaskServer: true);
        var options = Options();

        await new DurableHandoffRecovery(options, client, _ => { })
            .RecoverAllAsync(default);

        var reopened = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        Assert.Equal("completed", reopened.Snapshot.FinalHandoffState);
        Assert.Empty(reopened.Pending);
        Assert.Equal(1, handler.RenewalCalls);
        Assert.Equal(authority.Fence, handler.LastRenewal?.Fence);
        Assert.Equal(1, handler.HandoffCalls);
        Assert.Equal(1, handler.CompletionCalls);
        Assert.Equal(0, handler.CodingProcessCalls);
    }

    [Fact]
    public async Task Recovery_repairs_a_response_received_before_atomic_local_ack_persistence()
    {
        var authority = new RunOutboxAuthority(
            "run-split-ack",
            "TASK-10",
            "runner-a",
            "old-host:42",
            "lease-b",
            10);
        var outbox = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        var manifest = RemoteTaskRunner.BuildArtifactManifest([]);
        var resultSha = new string('4', 40);
        var envelope = new ImmutableResultEnvelope(
            "repo-10",
            authority.RunId,
            new string('3', 40),
            resultSha,
            FencedGitRefs.ImmutableResult(authority.RunId, authority.Fence, resultSha),
            null,
            manifest.Digest);
        outbox.Enqueue("run-context", JsonSerializer.Serialize(
            new DurableRunContextPayload(
                "repo-10", null, "main", new string('3', 40)),
            WebJson));
        outbox.Enqueue("terminal", JsonSerializer.Serialize(
            new DurableTerminalPayload("Done", null),
            WebJson));
        outbox.Enqueue("artifact-manifest", manifest.Json);
        var final = outbox.Enqueue(
            "final-result",
            JsonSerializer.Serialize(envelope, WebJson));
        outbox.Acknowledge(final.Sequence);

        var handler = new RecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-a",
            usesDurableTaskServer: true);

        await new DurableHandoffRecovery(Options(), client, _ => { })
            .RecoverAllAsync(default);

        var recovered = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        Assert.NotNull(recovered.HandoffAcknowledgement);
        Assert.Equal(final.Sequence, recovered.HandoffAcknowledgement!.AcknowledgedSequence);
        Assert.Equal("completed", recovered.Snapshot.FinalHandoffState);
        Assert.Equal(1, handler.HandoffCalls);
        Assert.Equal(1, handler.CompletionCalls);
        Assert.Equal(1, handler.RenewalCalls);
    }

    [Fact]
    public async Task Recovery_sends_nothing_when_exact_authority_cannot_be_reconciled()
    {
        var authority = new RunOutboxAuthority(
            "run-unreconciled",
            "TASK-10B",
            "runner-a",
            "old-host:42",
            "lease-unreconciled",
            22);
        var outbox = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        outbox.Enqueue("status", """{"phase":"terminal-queued"}""");
        outbox.Enqueue("run-context", JsonSerializer.Serialize(
            new DurableRunContextPayload(
                "repo-10b", null, "main", new string('5', 40)),
            WebJson));
        outbox.Enqueue("terminal", JsonSerializer.Serialize(
            new DurableTerminalPayload("Done", null),
            WebJson));
        outbox.Enqueue(
            "artifact-manifest",
            RemoteTaskRunner.BuildArtifactManifest([]).Json);

        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-a",
            usesDurableTaskServer: true);

        await new DurableHandoffRecovery(Options(), client, _ => { })
            .RecoverAllAsync(default);

        var unchanged = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        Assert.Equal("collecting", unchanged.Snapshot.FinalHandoffState);
        Assert.NotEmpty(unchanged.Pending);
        Assert.Equal(1, handler.RenewalCalls);
        Assert.Equal(0, handler.EventCalls);
        Assert.Equal(0, handler.HandoffCalls);
        Assert.Equal(0, handler.ArtifactCalls);
        Assert.Equal(0, handler.CompletionCalls);
        Assert.Equal(0, handler.OutboxStatusCalls);
    }

    [Fact]
    public async Task Periodic_recovery_skips_an_outbox_owned_by_a_live_slot()
    {
        var authority = new RunOutboxAuthority(
            "run-active",
            "TASK-11",
            "runner-a",
            "new-host:43",
            "lease-c",
            11);
        var outbox = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        outbox.Enqueue("status", """{"phase":"running"}""");
        using var active = outbox.MarkActive();
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-a",
            usesDurableTaskServer: true);

        await new DurableHandoffRecovery(Options(), client, _ => { })
            .RecoverAllAsync(default);

        var unchanged = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        Assert.Equal("collecting", unchanged.Snapshot.FinalHandoffState);
        Assert.Equal(0, handler.RenewalCalls);
        Assert.Equal(0, handler.HandoffCalls);
        Assert.Equal(0, handler.CompletionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crash_before_or_after_artifact_upload_converges_without_coding(
        bool artifactUploadWasAcknowledged)
    {
        var origin = await SeedOriginAsync();
        var options = Options(origin);
        var authority = new RunOutboxAuthority(
            artifactUploadWasAcknowledged ? "run-after-upload" : "run-before-upload",
            "TASK-12",
            "runner-a",
            "old-host:44",
            "lease-d",
            12);
        var workspace = new GitWorkspace(
            options,
            authority.TaskKey,
            _ => { },
            "repo-12",
            origin,
            "main",
            sourceRunAttemptId: authority.RunId,
            fencingToken: authority.Fence);
        await workspace.PrepareAsync(default);
        var baseSha = workspace.BaseSha!;
        await File.WriteAllTextAsync(
            Path.Combine(workspace.RepoPath, "result.txt"),
            "durable result");
        var results = Path.Combine(
            _root,
            "tasks",
            GitWorkspace.SafeSegment(authority.TaskKey),
            "results");
        Directory.CreateDirectory(results);
        var evidence = System.Text.Encoding.UTF8.GetBytes("tested evidence");
        await File.WriteAllBytesAsync(Path.Combine(results, "evidence.txt"), evidence);

        var outbox = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        outbox.Enqueue("run-context", JsonSerializer.Serialize(
            new DurableRunContextPayload("repo-12", origin, "main", baseSha),
            WebJson));
        outbox.Enqueue("terminal", JsonSerializer.Serialize(
            new DurableTerminalPayload("Done", null),
            WebJson));
        if (artifactUploadWasAcknowledged)
        {
            var sha = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(evidence))
                .ToLowerInvariant();
            outbox.Enqueue("artifact", JsonSerializer.Serialize(
                new DurableArtifactPayload(
                    "results/evidence.txt",
                    "text/plain",
                    Convert.ToBase64String(evidence),
                    sha),
                WebJson));
            var manifest = RemoteTaskRunner.BuildArtifactManifest(
            [
                new ArtifactManifestEntry(
                    "results/evidence.txt",
                    sha,
                    evidence.LongLength),
            ]);
            var manifestItem = outbox.Enqueue("artifact-manifest", manifest.Json);
            outbox.Acknowledge(manifestItem.Sequence);
        }

        var handler = new RecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-a",
            usesDurableTaskServer: true);
        var recovery = new DurableHandoffRecovery(options, client, _ => { });

        await recovery.RecoverAllAsync(default);
        await recovery.RecoverAllAsync(default);

        var envelope = Assert.IsType<ImmutableResultEnvelope>(handler.LastEnvelope);
        Assert.Equal(authority.RunId, envelope.SourceRunAttemptId);
        Assert.Equal(baseSha, envelope.BaseSha);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal(1, handler.HandoffCalls);
        Assert.Equal(1, handler.CompletionCalls);
        Assert.Equal(1, handler.RenewalCalls);
        Assert.Equal(artifactUploadWasAcknowledged ? 0 : 1, handler.ArtifactCalls);
        Assert.Equal(0, handler.CodingProcessCalls);

        var reader = Path.Combine(_root, "read-only-reconstruction");
        await GitAsync(_root, "clone", origin, reader);
        await GitAsync(
            reader,
            "fetch",
            "origin",
            envelope.ImmutableRemoteRef!);
        Assert.Equal(
            envelope.ResultSha,
            (await GitAsync(reader, "rev-parse", "FETCH_HEAD")).StdOut);
    }

    private RunnerOptions Options(string? gitRemote = null) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-a",
        RunnerName = "runner-a",
        Hostname = "new-host",
        BackendName = "test",
        GitRemote = gitRemote,
        WorkDir = _root,
        BaseBranch = "main",
        CliBin = "must-not-run",
        CliArgs = "",
    };

    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        // The root holds a git clone; its read-only objects make a plain recursive
        // delete throw on Windows and fail the test from teardown.
        ResilientDirectory.TryDelete(_root);
    }

    private async Task<string> SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", origin);
        await GitAsync(_root, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "--all");
        await GitAsync(
            seed,
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "-m",
            "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        return origin;
    }

    private static async Task<ProcessResult> GitAsync(
        string workingDirectory,
        params string[] args)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            args,
            workingDirectory: workingDirectory);
        Assert.True(
            result.Success,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
        return new ProcessResult(
            result.ExitCode,
            result.StdOut.Trim(),
            result.StdErr.Trim());
    }

    private sealed class RecordingHandler(
        HttpStatusCode renewalStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RenewalCalls { get; private set; }
        public int HandoffCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public int ArtifactCalls { get; private set; }
        public int EventCalls { get; private set; }
        public int OutboxStatusCalls { get; private set; }
        public int CodingProcessCalls { get; private set; }
        public LeaseRenewRequest? LastRenewal { get; private set; }
        public ImmutableResultEnvelope? LastEnvelope { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lease/renew", StringComparison.Ordinal))
            {
                RenewalCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastRenewal = JsonSerializer.Deserialize<LeaseRenewRequest>(
                    body,
                    WebJson)!;
                if (renewalStatus != HttpStatusCode.OK)
                    return Json(renewalStatus, new { error = "partitioned" });
                var runId = path.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)[3];
                return Json(HttpStatusCode.OK, new LeaseResponse(
                    "renewed",
                    new LeaseDto(
                        LastRenewal.LeaseId,
                        runId,
                        "task-9",
                        LastRenewal.RunnerId,
                        LastRenewal.InstanceId,
                        LastRenewal.Fence,
                        DateTime.UtcNow,
                        DateTime.UtcNow.AddMinutes(15),
                        "active")));
            }
            if (path.EndsWith("/result-handoff", StringComparison.Ordinal))
            {
                HandoffCalls++;
                var runId = path.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)[3];
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var handoff = JsonSerializer.Deserialize<ResultHandoffRequest>(
                    body,
                    WebJson)!;
                LastEnvelope = handoff.Envelope;
                return Json(HttpStatusCode.OK, new ResultHandoffAck(
                    runId,
                    handoff.Sequence,
                    handoff.EnvelopeDigest,
                    "acknowledged",
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddDays(30),
                    false));
            }
            if (path.EndsWith("/completion", StringComparison.Ordinal))
            {
                CompletionCalls++;
                var runId = path.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)[3];
                return Json(HttpStatusCode.OK, new RunDto(
                    runId,
                    "task-9",
                    "Done",
                    "runner-a",
                    9,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    DateTime.UtcNow));
            }
            if (path.EndsWith("/outbox-status", StringComparison.Ordinal))
            {
                OutboxStatusCalls++;
                return Json(HttpStatusCode.OK, new RunnerOutboxStatusDto(
                    "runner-a",
                    "new-host:1",
                    0,
                    0,
                    0,
                    null,
                    "completed",
                    "run-recovery",
                    null,
                    DateTime.UtcNow));
            }
            if (path.EndsWith("/artifact-limits", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, new ArtifactTransferLimitsResponse(
                    25L * 1024 * 1024,
                    18L * 1024 * 1024,
                    100L * 1024 * 1024));
            }
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                ArtifactCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var artifact = JsonSerializer.Deserialize<
                    AgentStudio.TaskServer.Contracts.ArtifactIngestRequest>(
                    body,
                    WebJson)!;
                return Json(HttpStatusCode.Created, new ArtifactDto(
                    artifact.ArtifactId,
                    path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3],
                    artifact.Name,
                    artifact.MediaType,
                    artifact.Sha256,
                    Convert.FromBase64String(artifact.ContentBase64).LongLength,
                    artifact.IdempotencyKey,
                    artifact.Fence,
                    DateTime.UtcNow,
                    artifact.Sequence));
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                EventCalls++;
                return Json(HttpStatusCode.Created, new EventDto(
                    1,
                    $"event-{Guid.NewGuid():N}",
                    "run-recovery",
                    "task-9",
                    "runner.event",
                    "{}",
                    $"key-{Guid.NewGuid():N}",
                    9,
                    DateTime.UtcNow));
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T body)
            => new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body, WebJson),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
