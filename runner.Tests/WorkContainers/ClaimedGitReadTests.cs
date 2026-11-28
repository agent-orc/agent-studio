using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRunner.WorkContainers;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentRunner.Tests.WorkContainers;

public sealed class ClaimedGitReadTests : IDisposable
{
    private const string Source = "https://github.com/example/private.git";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claim-git-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task V1_claim_supplies_canonical_exact_checkout_and_links_refresh_to_cleanup()
    {
        var clock = new FakeTimeProvider();
        using var broker = new Broker(clock);
        using var http = new HttpClient(broker) { BaseAddress = new Uri("https://tasks.example.test") };
        var options = new RunnerOptions
        {
            ServerUrl = "https://tasks.example.test", RunnerId = "runner", RunnerName = "runner",
            Hostname = "host", BackendName = "task-server", WorkDir = _root,
            StateDir = Path.Combine(_root, "state"), BaseBranch = "main", ClaudeCliBin = "test",
            GitRemote = "git@github.com:example/private.git",
        };
        using var client = new TaskServerClient(http, options.RunnerId, authToken: broker.RunnerBearer,
            usesDurableTaskServer: true, options: options, runnerInstanceId: "instance");
        var claim = await client.ClaimAsync(new RunnerClaimRequest("runner", "runner", "host", 1, "task-server"), default);
        var state = new RunnerStateStore(options.StateDir);
        var slot = state.Create(claim.TaskKey!, claim.Lease!, _root, claim.RunId, claim.LeaseInstanceId)
            with { ClaimSubjectId = claim.JobId };
        var runner = new RemoteTaskRunner(options, client, _ => { }, state);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sha = WorkContainerPolicyTests.Sha;
        var runtime = new WorkContainerExecutorTests.FakeRuntime
        {
            WaitUntil = finish.Task,
            OnTokenInjected = token =>
            {
                if (token == broker.RenewedToken) refreshed.SetResult();
            },
            Log = $"1970-01-01T00:00:01Z AGENT_SHA_VERIFIED {sha}",
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = WorkContainerPolicyTests.Settings() with { StateDirectory = options.StateDir };
        var grant = new WorkContainerGrant(4, client.AcknowledgedWorkAllocationVersion, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunClaimedGitReadAsync(
            slot with { Lease = slot.Lease with { LeaseId = "foreign-lease" } }, claim.RepositoryUrl, sha,
            settings, runtime, WorkContainerPolicyTests.Digest, grant, deadline.Token));
        Assert.Empty(runtime.Calls);

        var running = runner.RunClaimedGitReadAsync(slot, claim.RepositoryUrl, sha, settings, runtime,
            WorkContainerPolicyTests.Digest, grant, deadline.Token, () => new HostMemory(56L << 30, 62L << 30), clock);
        await runtime.WaitEntered.Task.WaitAsync(deadline.Token);
        Assert.Equal(broker.InitialToken, runtime.InjectedToken);
        await client.RenewLeaseAsync(new RunLeaseHeartbeatRequest(
            claim.TaskKey!, claim.Lease!.LeaseId, claim.Lease.FencingToken, "runner", 240), deadline.Token);
        clock.Advance(TimeSpan.FromSeconds(30));
        await refreshed.Task.WaitAsync(deadline.Token);
        finish.SetResult();

        var receipt = await running;
        Assert.Equal("succeeded", receipt.Outcome);
        Assert.Equal(sha, receipt.ExactSha);
        Assert.True(receipt.ShaVerified);
        Assert.True(receipt.CleanupVerified);
        Assert.Equal("issuance", receipt.CredentialIssuanceId);
        Assert.Equal(1, receipt.CredentialRefreshCount);
        Assert.Equal("revoked", receipt.CredentialRevocationStatus);
        Assert.Equal("container-tmpfs-removed", receipt.SecretDeletionStatus);
        Assert.Equal(broker.RenewedToken, runtime.InjectedToken);
        Assert.Equal(["capability", "exchange", "lease-renew", "renew", "revoke", "deletion"], broker.Operations);
        var arguments = Assert.IsAssignableFrom<IReadOnlyList<string>>(runtime.CreateArguments);
        Assert.Contains("AGENT_REPOSITORY_SOURCE=" + Source, arguments);
        Assert.Contains("AGENT_EXPECTED_SHA=" + sha, arguments);
        Assert.Equal("/bin/true", arguments[^1]);
        Assert.DoesNotContain(arguments, value => value.Contains(broker.RunnerBearer, StringComparison.Ordinal)
            || value.Contains(broker.InitialToken, StringComparison.Ordinal) || value.Contains(broker.RenewedToken, StringComparison.Ordinal));
    }

    private sealed class Broker(FakeTimeProvider clock) : HttpMessageHandler
    {
        public string RunnerBearer { get; } = Guid.NewGuid().ToString("N");
        public string InitialToken { get; } = Guid.NewGuid().ToString("N");
        public string RenewedToken { get; } = Guid.NewGuid().ToString("N");
        public List<string> Operations { get; } = [];
        private Contract.LeaseDto Lease() => new("lease", "run", "subject", "runner", "instance", 7,
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().AddMinutes(2).UtcDateTime, "active");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(RunnerBearer, request.Headers.Authorization?.Parameter);
            var now = clock.GetUtcNow().UtcDateTime;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/runners/runner/claims")
                return Ok(new Contract.ClaimResponse("claimed",
                    new Contract.RunDto("run", "subject", "running", "runner", 7, now, now, null),
                    new Contract.TaskDto("subject", "project", "PROJ-1", "Private task", "3-progress", 1, now, now),
                    Lease(), RuntimeCapacity: new("host", 1, 80, "balanced", 4, now, 4)));
            if (path == "/api/v1/runs/run/lease/renew")
            {
                Operations.Add("lease-renew");
                return Ok(new Contract.LeaseResponse("renewed", Lease() with { ExpiresAt = now.AddMinutes(4) }));
            }

            Assert.StartsWith("/api/v1/runs/run/credentials/", path);
            var operation = path.Split('/')[^1];
            Operations.Add(operation);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var identity = operation == "capability" ? body : body.GetProperty("identity");
            Assert.Equal("host", identity.GetProperty("hostId").GetString());
            Assert.Equal("coding", identity.GetProperty("role").GetString());
            Assert.Equal("subject", identity.GetProperty("subjectId").GetString());
            Assert.Equal("run", identity.GetProperty("attemptId").GetString());
            Assert.Equal("runner", identity.GetProperty("runnerId").GetString());
            Assert.Equal("instance", identity.GetProperty("instanceId").GetString());
            Assert.Equal("lease", identity.GetProperty("leaseId").GetString());
            Assert.Equal(7, identity.GetProperty("fence").GetInt64());
            Assert.Equal(7, identity.GetProperty("epoch").GetInt64());
            Assert.Equal(Source, identity.GetProperty("repositorySource").GetString());
            if (operation != "capability") Assert.Equal("issuance", body.GetProperty("issuanceId").GetString());
            if (operation == "exchange") Assert.Equal("one-use", body.GetProperty("capability").GetString());
            if (operation == "deletion") Assert.Equal("container-tmpfs-removed", body.GetProperty("deletionStatus").GetString());
            return operation switch
            {
                "capability" => Ok(new { issuanceId = "issuance", capability = "one-use", expiresAtUtc = now.AddMinutes(2) }),
                "exchange" => Ok(new { issuanceId = "issuance", token = InitialToken, expiresAtUtc = now.AddMinutes(4) }),
                "renew" => Ok(new { issuanceId = "issuance", token = RenewedToken, expiresAtUtc = now.AddHours(1) }),
                "revoke" or "deletion" => Ok(new { issuanceId = "issuance", revocationStatus = "revoked" }),
                _ => throw new InvalidOperationException(operation),
            };
        }

        private static HttpResponseMessage Ok<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
