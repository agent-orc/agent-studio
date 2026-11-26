using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed partial class TaskServerStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_routing_selects_the_claim_route_or_waits_without_a_lease(bool bothExhausted)
    {
        using var temp = _fixture.CreateDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var seed = Store(temp.Path, clock);
        await seed.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(seed);
        await seed.SetTaskCliTypeAsync(project.ProjectId, task.TaskId, new("claude", 0), "test", default);
        await seed.SetTaskModelAsync(project.ProjectId, task.TaskId, new("claude-sonnet-5", 1), "test", default);
        await seed.SetTaskThinkingLevelAsync(project.ProjectId, task.TaskId, new("high", 2), "test", default);
        await seed.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        await ModelAcceptanceTestSupport.AcceptRoutedModelsAsync(seed, clock.GetUtcNow().UtcDateTime);
        var routing = new ClaimRoutingAuthority(bothExhausted);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock,
            new ApplicationResultFinalizationSummaryGenerator(), null, null, modelRouteAdmission: routing);
        await store.InitializeAsync();
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "runner-a", default);
        Assert.NotEmpty(routing.Requests);
        if (bothExhausted)
        {
            Assert.Equal("empty", claim.Status);
            Assert.Null(claim.Lease);
            Assert.Contains("all providers", claim.Message);
        }
        else
        {
            Assert.Equal("claimed", claim.Status);
            Assert.Equal("codex", claim.ModelSelection?.Route.CliType);
            Assert.Equal("gpt-6-sol", claim.ModelSelection?.Route.Model);
            Assert.Equal("medium", claim.ModelSelection?.Route.ThinkingLevel);
        }
    }

    private sealed class ClaimRoutingAuthority(bool bothExhausted) : IModelRouteAdmission
    {
        public List<ModelRouteAdmissionRequest> Requests { get; } = [];
        public Task<SelectionResult> SelectAsync(ModelRouteAdmissionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var primary = new SelectionRoute(request.Request.Pins.CliType!, request.Request.Pins.Model, request.Request.Pins.ThinkingLevel);
            return Task.FromResult(new SelectionResult { Outcome = bothExhausted ? SelectionOutcome.Wait : SelectionOutcome.Fallback,
                Primary = primary, Route = bothExhausted ? primary : new("codex", "gpt-6-sol", "medium"),
                Rule = SelectionRules.QuotaCap, Reason = bothExhausted ? "all providers exhausted" : "fixture quota exhausted" });
        }
    }
}
