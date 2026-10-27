using System.Text;
using System.Security.Cryptography;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PullGateStoreTests : IDisposable
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Digest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pull-gate-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Claim_heartbeat_expiry_and_late_upload_are_fenced()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:HostCapacity:gate-1:Slots"] = "2",
            ["RemoteGate:HostCapacity:gate-1:ProcessorCount"] = "3",
        }).Build();
        var store = new PullGateStore(_root, _clock, config);
        var host = store.Register("gate-1", "KORI");
        Assert.Equal(2, host.Slots);
        Assert.Equal(3, host.ProcessorCount);
        var runId = Enqueue(store);
        var first = store.Claim(host.Id)!;
        Assert.Equal(runId, first.RunId);
        Assert.Equal("claimed", store.Progress(runId));
        store.ReportProgress(host.Id, runId, first.Lease, "preparing");
        Assert.Equal("preparing", store.Progress(runId));
        _clock.Advance(TimeSpan.FromSeconds(80));
        store.Heartbeat(host.Id, runId, first.Lease, "building");
        store.UploadArtifact(host.Id, runId, first.Lease, "partial.zip",
            new MemoryStream(Encoding.UTF8.GetBytes("partial")));
        Assert.Equal("building", store.Progress(runId));
        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(store.Claim(host.Id));
        _clock.Advance(TimeSpan.FromSeconds(80));
        store.Register(host.Id, host.Name);
        var second = store.Claim(host.Id)!;
        Assert.NotEqual(first.Lease, second.Lease);
        Assert.False(File.Exists(Path.Combine(_root, runId, "artifacts", "partial.zip")));
        Assert.Throws<InvalidOperationException>(() =>
            store.UploadArtifact(host.Id, runId, first.Lease, "release.zip",
                new MemoryStream(Encoding.UTF8.GetBytes("late"))));
        Assert.Throws<InvalidOperationException>(() =>
            store.Complete(host.Id, runId, first.Lease, Result(runId)));
        store.UploadArtifact(host.Id, runId, second.Lease, "release.zip",
            new MemoryStream(Encoding.UTF8.GetBytes("release")));
        store.Complete(host.Id, runId, second.Lease, Result(runId));
        Assert.Equal("release", File.ReadAllText(store.Artifact(runId, "release.zip")!));
        Assert.Equal(Sha, store.Response(runId)!.Result.ExpectedSha);
        Assert.Throws<InvalidOperationException>(() =>
            store.UploadArtifact(host.Id, runId, second.Lease, "late.zip", new MemoryStream()));
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(store.HasHealthyHost);
    }

    [Fact]
    public void Persisted_bundle_and_result_survive_server_restart()
    {
        var store = new PullGateStore(_root, _clock);
        store.Register("gate-1", "KORI");
        var runId = Enqueue(store);
        var restarted = new PullGateStore(_root, _clock);
        restarted.Register("gate-1", "KORI");
        var claim = restarted.Claim("gate-1")!;
        Assert.Equal("bundle", File.ReadAllText(
            restarted.FileForLease("gate-1", runId, claim.Lease, "source.bundle")));
        restarted.Complete("gate-1", runId, claim.Lease, Result(runId));
        Assert.Equal(Sha, new PullGateStore(_root, _clock).Response(runId)!.Result.ExpectedSha);
    }

    [Fact]
    public void Restart_fence_blocks_queued_claims_and_survives_a_studio_restart()
    {
        var store = new PullGateStore(_root, _clock);
        store.Register("gate-1", "KORI");
        var runId = Enqueue(store);
        Assert.Equal((0, 1), store.Workload("gate-1")!.Value);
        var fence = Assert.IsAssignableFrom<AgentStudio.Runner.IRestartClaimFence>(store.TryFenceHostClaims("gate-1"));
        fence.Retain(); // The Operations Server accepted the command but its status cannot be read.
        Assert.Null(store.Claim("gate-1"));

        var recovered = new PullGateStore(_root, _clock);
        recovered.Register("gate-1", "KORI");
        Assert.Null(recovered.Claim("gate-1"));
        Assert.Null(recovered.TryFenceHostClaims("gate-1"));
        fence.Dispose();
        Assert.Null(recovered.Claim("gate-1"));
        Assert.Equal((0, 1), recovered.Workload("gate-1")!.Value);
        recovered.ReleaseRestartFence("gate-1"); // only after the remote attempt is terminal
        Assert.Equal(runId, recovered.Claim("gate-1")!.RunId);
    }

    [Fact]
    public void Busy_gate_worker_cannot_be_fenced_for_recreation()
    {
        var store = new PullGateStore(_root, _clock);
        store.Register("gate-1", "KORI");
        Enqueue(store);
        Assert.NotNull(store.Claim("gate-1"));
        Assert.Null(store.TryFenceHostClaims("gate-1"));
    }

    [Fact]
    public void Ssh_and_pull_accept_the_same_gate_result_for_the_same_bundle()
    {
        var runId = Guid.NewGuid().ToString("N");
        var bundle = Encoding.UTF8.GetBytes("same source bundle");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bundle));
        var response = Result(runId) with { BundleSha256 = digest };
        var ssh = RemoteGateTransport.AcceptResponse(response, runId, digest,
            "KORI", "integration", 6, TimeSpan.FromSeconds(1));
        var pull = PullGateTransport.AcceptResponse(response, runId, digest);
        Assert.Equal(ssh.Verdict, pull.Verdict);
        Assert.Equal(ssh.TestedSha, pull.TestedSha);
        Assert.Equal(ssh.ExpectedSha, pull.ExpectedSha);
        Assert.Equal(ssh.GateId, pull.GateId);
        Assert.Equal(ssh.Output, pull.Output);
        Assert.Equal(runId, pull.ServerArtifactRunId);
        Assert.Throws<InvalidDataException>(() => RemoteGateTransport.AcceptResponse(
            response, runId, new string('c', 64), "KORI", "integration", 6, TimeSpan.Zero));
        Assert.Throws<InvalidDataException>(() => PullGateTransport.AcceptResponse(
            response, runId, new string('c', 64)));
    }

    [Fact]
    public async Task Registered_healthy_worker_selects_pull_and_stale_worker_selects_ssh()
    {
        var store = new PullGateStore(_root, _clock);
        var pull = new MarkerTransport("pull");
        var ssh = new MarkerTransport("ssh");
        var transport = new ConventionalGateTransport(store, pull, ssh);
        var request = new BuildTestGateRequest("source.git", Sha, "test");
        Assert.Equal("ssh", (await transport.RunAsync(request, null, null,
            PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None)).Output);
        store.Register("gate-1", "KORI");
        Assert.Equal("pull", (await transport.RunAsync(request, null, null,
            PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None)).Output);
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("ssh", (await transport.RunAsync(request, null, null,
            PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None)).Output);
    }

    private sealed class MarkerTransport(string marker) : IRemoteGateTransport
    {
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 0,
                marker, marker, true, true));
    }

    private string Enqueue(PullGateStore store)
    {
        var runId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bundle");
        File.WriteAllText(path, "bundle");
        var invocation = new RemoteGateInvocation(1, runId, Digest,
            new BuildTestGateRequest("source.git", Sha, "test")
            {
                InfrastructureTimeout = TimeSpan.FromMinutes(1),
            }, null, null, PostStepMode.Fail, 60, 120);
        return store.Enqueue(invocation, path);
    }

    private static RemoteGateResponse Result(string runId)
        => new(1, runId, Digest,
            new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 1, "", "passed", true, true)
            { ExpectedSha = Sha, TestedSha = Sha });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
