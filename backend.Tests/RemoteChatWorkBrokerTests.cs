using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Shared;

using Xunit;

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
            ModelIds.ClaudeOpus5,
            "high",
            CancellationToken.None,
            cliType: CliTypes.Claude,
            configuredCliType: CliTypes.Codex,
            configuredModel: ModelIds.Gpt56Sol,
            quotaFallbackReason: "codex weekly cap reached");

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
        Assert.Equal(CliTypes.Claude, claim.Work.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, claim.Work.Model);
        var running = broker.GetStatus(Route.ProjectName, contextKey: null);
        Assert.Equal("running", running?.State);
        Assert.Equal("agent-runner-01", running?.HostName);
        Assert.NotNull(running?.StartedAt);
        Assert.Equal(CliTypes.Codex, claim.Work.ConfiguredCliType);
        Assert.Equal(ModelIds.Gpt56Sol, claim.Work.ConfiguredModel);

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
            ModelIds.ClaudeOpus5,
            null,
            null,
            context));

        Assert.True(accepted);
        var result = await pending;
        Assert.True(result.Success);
        Assert.NotNull(result.QueuedAt);
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.FinishedAt);
        Assert.True(result.QueuedAt <= result.StartedAt);
        Assert.True(result.StartedAt <= result.FinishedAt);
        Assert.Contains(context.RepoPath!, result.ReplyText);
        Assert.Equal(CliTypes.Claude, result.CliType);
        Assert.Equal(ModelIds.Gpt56Sol, result.ConfiguredModel);
        Assert.Equal("codex weekly cap reached", result.QuotaFallbackReason);
        Assert.Equal(context, broker.GetContext(Route));

        var reassigned = Route with { RunnerId = "runner-02" };
        Assert.Null(broker.GetContext(reassigned));
    }

    [Fact]
    public async Task Capability_preparation_can_defer_fallback_work_without_consuming_it()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        using var cancelled = new CancellationTokenSource();
        var pending = broker.EnqueueTurnAsync(
            Route,
            "Use the fallback family.",
            ModelIds.ClaudeOpus5,
            "high",
            cancelled.Token,
            cliType: CliTypes.Claude);

        var deferred = broker.TryClaim(
            new RemoteChatWorkClaimRequest("runner-01", "runner-01", "host"),
            candidate => new RemoteChatWorkClaimPreparation(
                candidate.CliType == CliTypes.Codex,
                "claude capability unavailable"));

        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, deferred.Status);
        Assert.Equal("claude capability unavailable", deferred.Message);
        var waiting = broker.GetStatus(Route.ProjectName, contextKey: null);
        Assert.Equal("queued", waiting?.State);
        Assert.Equal("claude capability unavailable", waiting?.Reason);
        var claimedLater = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "runner-01", "host"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, claimedLater.Status);
        Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
            claimedLater.Work!.WorkId,
            claimedLater.Work.ClaimToken,
            "runner-01",
            true,
            "done",
            ModelIds.ClaudeOpus5,
            null,
            null,
            null)));
        Assert.True((await pending).Success);
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
    public async Task Unreachable_host_releases_queued_turn_for_workstation_fallback()
    {
        var broker = new RemoteChatWorkBroker(
            NullLogger<RemoteChatWorkBroker>.Instance,
            TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAsync<RemoteChatHostUnreachableException>(() =>
            broker.EnqueueTurnAsync(Route, "question", "gpt-5.5", null, CancellationToken.None));
        Assert.Null(broker.GetStatus(Route.ProjectName, contextKey: null));
    }

    [Fact]
    public async Task Usage_separates_light_and_heavy_chat_from_coding_capacity()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        var pending = broker.EnqueueTurnAsync(
            Route, "question", "gpt-5.5", null, CancellationToken.None);
        var work = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "runner-01", "agent-runner-01")).Work!;
        var light = Assert.Single(broker.GetUsage());
        Assert.Equal(1, light.ActiveTurns);
        Assert.Equal(0, light.HeavyTurns);

        Assert.True(broker.Renew(new RemoteChatWorkRenewRequest(
            work.WorkId, work.ClaimToken, "runner-01", Heavy: true, CpuPercent: 54)));
        var heavy = Assert.Single(broker.GetUsage());
        Assert.Equal(1, heavy.HeavyTurns);
        Assert.Equal(54, heavy.CpuPercent);

        Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
            work.WorkId, work.ClaimToken, "runner-01", true, "done", "gpt-5.5",
            new OrchestratorTokenUsage { InputTokens = 7, OutputTokens = 3 },
            null, null)));
        await pending;
        var completed = Assert.Single(broker.GetUsage());
        Assert.Equal(0, completed.ActiveTurns);
        Assert.Equal(0, completed.HeavyTurns);
        Assert.Equal(10, completed.Tokens);
        Assert.NotNull(completed.CostUsd);
    }
}
