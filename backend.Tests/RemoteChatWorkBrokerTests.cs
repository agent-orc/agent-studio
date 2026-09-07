using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class RemoteChatWorkBrokerTests
{
    private static readonly RemoteChatWorkRoute Route = new(
        "runner-01",
        "PROJ-002",
        "Agent Studio",
        "ssh://git.example.invalid/agent-studio.git",
        "develop");

    [Fact]
    public async Task Assigned_runner_claims_turn_and_completion_preserves_exact_host_checkout()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        var pending = broker.EnqueueTurnAsync(
            Route,
            "Inspect the repository.",
            "gpt-5.5",
            "high",
            CancellationToken.None);

        var wrongRunner = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-02", "runner-02", "other-host"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, wrongRunner.Status);

        var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, claim.Status);
        Assert.NotNull(claim.Work);
        Assert.Equal(Route.RepositoryUrl, claim.Work!.RepositoryUrl);
        Assert.Equal(Route.DefaultBranch, claim.Work.DefaultBranch);
        Assert.Equal("Inspect the repository.", claim.Work.Prompt);

        var context = new ChatExecutionContext(
            "remote",
            "agent-runner-01",
            "/srv/agent-runner/work/PROJ-002/project-chat",
            "develop",
            "0123456789abcdef0123456789abcdef01234567",
            "ready",
            DateTime.UtcNow);
        var accepted = broker.Complete(new RemoteChatWorkCompletionRequest(
            claim.Work.WorkId,
            claim.Work.ClaimToken,
            "runner-01",
            true,
            $"tool-output cwd={context.RepoPath}",
            "gpt-5.5",
            null,
            null,
            context));

        Assert.True(accepted);
        var result = await pending;
        Assert.True(result.Success);
        Assert.Contains(context.RepoPath!, result.ReplyText);
        Assert.Equal(context, broker.GetContext(Route));

        var reassigned = Route with { RunnerId = "runner-02" };
        Assert.Null(broker.GetContext(reassigned));
    }

    [Fact]
    public async Task Cancelled_unclaimed_turn_is_removed_before_a_host_can_pick_it_up()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        using var cancelled = new CancellationTokenSource();
        var pending = broker.EnqueueTurnAsync(
            Route,
            "This request will be cancelled.",
            "gpt-5.5",
            null,
            cancelled.Token);

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, claim.Status);
    }

    [Fact]
    public void Inspection_work_is_deduplicated_per_project_checkout()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);

        broker.RequestInspection(Route);
        broker.RequestInspection(Route);

        var first = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        var second = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));

        Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, first.Status);
        Assert.Equal(RemoteChatWorkKinds.Inspect, first.Work?.Kind);
        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, second.Status);
    }

    [Fact]
    public async Task Queued_turn_uses_claim_time_quota_and_returns_to_primary_after_recovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "remote-chat-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = root })
                .Build();
            var store = new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance);
            store.Write([QuotaSnapshot("codex", 50), QuotaSnapshot("claude", 34)]);
            var quota = new QuotaService(
                NullLogger<QuotaService>.Instance,
                [new EmptyProbe("codex"), new EmptyProbe("claude")],
                configuration,
                store);
            var caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, configuration);
            caps.SetCap("codex", "Weekly", 98);
            var fallback = new CliQuotaFallbackService(
                configuration,
                NullLogger<CliQuotaFallbackService>.Instance);
            fallback.Set(new CliModelRouteProfile
            {
                CliType = "codex",
                FallbackCliType = "claude",
                FallbackModel = "claude-opus-5",
                FallbackThinkingLevel = "high",
            });
            var broker = new RemoteChatWorkBroker(
                NullLogger<RemoteChatWorkBroker>.Instance,
                quota,
                caps,
                fallback);

            var pending = broker.EnqueueTurnAsync(
                Route,
                "Inspect the repository.",
                "gpt-5.6-sol",
                "high",
                CancellationToken.None);

            // The work was healthy when queued. Exhaustion before pickup must
            // be observed by claim-time planning, not frozen at enqueue.
            caps.SetCap("codex", "Weekly", 50);
            var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
                "runner-01", "agent-runner-01", "agent-runner-01"));

            Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, claim.Status);
            Assert.Equal("claude", claim.Work!.CliType);
            Assert.Equal("claude-opus-5", claim.Work.Model);
            Assert.Equal("high", claim.Work.ThinkingLevel);
            Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
                claim.Work.WorkId,
                claim.Work.ClaimToken,
                "runner-01",
                true,
                "done",
                claim.Work.Model,
                null,
                null,
                null)));
            var result = await pending;
            Assert.True(result.QuotaAdmission?.IsFallback);
            Assert.Equal("claude", result.CliType);

            var queuedWhileCapped = broker.EnqueueTurnAsync(
                Route,
                "Inspect after recovery.",
                "gpt-5.6-sol",
                "high",
                CancellationToken.None);
            caps.SetCap("codex", "Weekly", 98);
            var recoveredClaim = broker.TryClaim(new RemoteChatWorkClaimRequest(
                "runner-01", "agent-runner-01", "agent-runner-01"));

            Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, recoveredClaim.Status);
            Assert.Equal("codex", recoveredClaim.Work!.CliType);
            Assert.Equal("gpt-5.6-sol", recoveredClaim.Work.Model);
            Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
                recoveredClaim.Work.WorkId,
                recoveredClaim.Work.ClaimToken,
                "runner-01",
                true,
                "recovered",
                recoveredClaim.Work.Model,
                null,
                null,
                null)));
            var recoveredResult = await queuedWhileCapped;
            Assert.False(recoveredResult.QuotaAdmission?.IsFallback);
            Assert.Equal("codex", recoveredResult.CliType);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Fallback_claim_waits_for_runner_with_effective_provider_capability()
    {
        var root = Path.Combine(Path.GetTempPath(), "remote-chat-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = root })
                .Build();
            var store = new QuotaCacheStore(configuration, NullLogger<QuotaCacheStore>.Instance);
            store.Write([QuotaSnapshot("codex", 98), QuotaSnapshot("claude", 34)]);
            var quota = new QuotaService(
                NullLogger<QuotaService>.Instance,
                [new EmptyProbe("codex"), new EmptyProbe("claude")],
                configuration,
                store);
            var caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, configuration);
            caps.SetCap("codex", "Weekly", 98);
            var fallback = new CliQuotaFallbackService(
                configuration,
                NullLogger<CliQuotaFallbackService>.Instance);
            fallback.Set(new CliModelRouteProfile
            {
                CliType = "codex",
                FallbackCliType = "claude",
                FallbackModel = "claude-opus-5",
                FallbackThinkingLevel = "high",
            });
            var capabilities = new V1ReviewExecutorRegistry();
            const string instanceId = "agent-runner-01:123";
            capabilities.Register(
                "runner-01",
                new Contract.RegisterRunnerRequest(
                    "agent-runner-01",
                    "agent-runner-01",
                    instanceId,
                    "1.0.0",
                    Contract.TaskServerProtocol.Current,
                    [Contract.ReviewCapabilities.CodingExecutor]));
            AdvertiseProvider(capabilities, instanceId, "codex", generation: 1);
            var broker = new RemoteChatWorkBroker(
                NullLogger<RemoteChatWorkBroker>.Instance,
                quota,
                caps,
                fallback,
                capabilityRegistry: capabilities);

            var pending = broker.EnqueueTurnAsync(
                Route,
                "Inspect the repository.",
                "gpt-5.6-sol",
                "high",
                CancellationToken.None);
            var incompatible = broker.TryClaim(new RemoteChatWorkClaimRequest(
                "runner-01", "agent-runner-01", "agent-runner-01", instanceId));

            Assert.Equal(RemoteChatWorkClaimStatuses.Empty, incompatible.Status);

            AdvertiseProvider(capabilities, instanceId, "claude", generation: 2);
            var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
                "runner-01", "agent-runner-01", "agent-runner-01", instanceId));

            Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, claim.Status);
            Assert.Equal("claude", claim.Work!.CliType);
            Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
                claim.Work.WorkId,
                claim.Work.ClaimToken,
                "runner-01",
                true,
                "done",
                claim.Work.Model,
                null,
                null,
                null)));
            _ = await pending;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void AdvertiseProvider(
        V1ReviewExecutorRegistry registry,
        string instanceId,
        string cliType,
        long generation)
    {
        registry.AdvertiseCapabilities(
            "runner-01",
            new Contract.CapabilityAdvertisementRequest(
                "runner-01",
                instanceId,
                Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow,
                180,
                generation,
                [
                    new Contract.AdvertisedCapabilityDto(
                        Contract.ReviewCapabilities.CodingExecutor,
                        "executor"),
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.CliExecution(cliType),
                        "cli-execution"),
                    new Contract.AdvertisedCapabilityDto(
                        Contract.CapabilityProtocol.ProviderAuthentication(cliType),
                        "provider-auth"),
                ]));
    }

    private static QuotaSnapshot QuotaSnapshot(string cliType, double usedPct) => new()
    {
        CliType = cliType,
        FetchedAt = DateTime.UtcNow,
        Windows =
        [
            new QuotaWindow
            {
                Label = "Weekly",
                UsedPct = usedPct,
                ResetAt = DateTime.UtcNow.AddDays(2),
                ObservedStartAt = DateTime.UtcNow.AddDays(-5),
            },
        ],
    };

    private sealed class EmptyProbe(string cliType) : IQuotaProbe
    {
        public string CliType { get; } = cliType;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new QuotaSnapshot { CliType = CliType });
    }
}
