using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestrationRolloutTests
{
    [Fact]
    public void Fifty_matches_mark_ready_without_switching_and_mismatch_resets_the_streak()
    {
        using var fixture = new Fixture();
        fixture.Rollout.Switch("ReviewReport", "shadow", "operator", "begin comparison");
        for (var index = 0; index < 50; index++)
        {
            var monolith = OrchestrationRolloutStore.Receipt($"run-{index}", "review-report", "{}",
                "FindingRound", "Fix the review findings.", "monolith:v1");
            var engine = monolith with { Decider = "engine:v1" };
            fixture.Rollout.Compare("ReviewReport", monolith, engine);
        }
        var ready = fixture.Rollout.GetProfiles().Single(item => item.Profile == "ReviewReport");
        Assert.Equal("ready", ready.State);
        Assert.Equal(50, ready.Streak);
        var duplicate = OrchestrationRolloutStore.Receipt("run-49", "review-report", "{}",
            "FindingRound", "Fix the review findings.", "monolith:v1");
        fixture.Rollout.Compare("ReviewReport", duplicate, duplicate with { Decider = "engine:v1" });
        Assert.Equal(50, fixture.Rollout.GetProfiles().Single(item => item.Profile == "ReviewReport").Streak);
        Assert.Equal(OrchestrationExecutionMode.Shadow,
            MonolithTransitionSwitch.For(fixture.Configuration, "ReviewReport"));

        var left = OrchestrationRolloutStore.Receipt("mismatch", "review-report", "{}",
            "FindingRound", "Fix a finding.", "monolith:v1");
        fixture.Rollout.Compare("ReviewReport", left,
            left with { Action = "Escalate", Reason = "Test block exhausted.", Decider = "engine:v1" });
        var reset = fixture.Rollout.GetProfiles().Single(item => item.Profile == "ReviewReport");
        Assert.Equal("shadow", reset.State);
        Assert.Equal(0, reset.Streak);
        Assert.Equal("Escalate", reset.LastMismatch!.Engine.Action);
        Assert.Equal(left.InputsDigest, reset.LastMismatch.Monolith.InputsDigest);
    }

    [Fact]
    public async Task Shadow_receipts_skip_application_and_rollback_keeps_pending_engine_occurrence()
    {
        using var fixture = new Fixture(threshold: 1);
        fixture.Rollout.Switch("GateResult", "shadow", "operator", "begin comparison");
        var shadow = fixture.Store.Create("project", "card", OrchestrationStage.GateDispatch,
            "{\"monolithGateResult\":true}", "gate:one", TaskStates.AutoReview,
            "attempt", 1, 1, profile: "GateResult", shadow: true,
            monolithAction: "ReturnToReady", monolithReason: "Gate failed.");
        var claim = fixture.Store.Claim(new("engine", "first", [OrchestrationStage.GateDispatch]));
        var output = JsonSerializer.Serialize(new { transition = TransitionDecisionPolicy.Create(
            TransitionAction.ReturnToReady, "Gate failed.", "card", "attempt", 0, 4) });
        await fixture.Store.CompleteAsync(shadow.RunId,
            new("engine", "first", claim.Lease!.LeaseId, claim.Lease.Fence,
                OrchestrationStage.GateDispatch, OrchestrationAction.Reissue, output, "complete:shadow"), default);
        Assert.Equal(0, fixture.Applier.Count);
        Assert.NotNull(fixture.Store.GetOccurrence(shadow.RunId)!.EngineReceipt);
        Assert.Equal("ready", fixture.Rollout.GetProfiles().Single(item => item.Profile == "GateResult").State);

        fixture.Rollout.Switch("GateResult", "engine", "operator", "matched comparison");
        var pending = fixture.Store.Create("project", "card", OrchestrationStage.GateDispatch,
            "{}", "gate:pending", TaskStates.AutoReview, "attempt", 1, 1,
            profile: "GateResult");
        fixture.Rollout.Switch("GateResult", "rollback", "operator", "engine unavailable");
        Assert.Equal("pending", fixture.Store.Get(pending.RunId)!.Status);
        Assert.Equal("empty", fixture.Store.Claim(new("engine", "second",
            [OrchestrationStage.GateDispatch])).Status);
        Assert.Equal(pending.RunId, fixture.Store.ClaimRolledBack(pending.RunId).Run!.RunId);
        Assert.Equal(OrchestrationExecutionMode.Monolith,
            MonolithTransitionSwitch.For(fixture.Configuration, "GateResult"));
        Assert.Equal(3, fixture.Rollout.SwitchReceipts().Count);
        Assert.Equal("operator", fixture.Rollout.SwitchReceipts().Last().Actor);
    }

    [Fact]
    public void Recovery_wait_list_excludes_unactivated_review_occurrences()
    {
        using var fixture = new Fixture();
        fixture.Store.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{}", "review:prepared", TaskStates.AutoReview, "attempt", 1, 1,
            claimable: false);
        Assert.Empty(fixture.Store.WaitingOccurrences());
        Assert.Single(fixture.Store.AwaitingActivation());
    }

    [Fact]
    public async Task Operator_api_switches_once_and_exposes_the_receipt()
    {
        using var fixture = new Fixture();
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = fixture.Configuration["TaskRepository"],
                    ["Security:Profile"] = "local",
                }));
        });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var switched = await client.PostAsJsonAsync(
            "/api/orchestration/rollout/profiles/CodingCompletion/switch",
            new { target = "shadow", reason = "Start comparison" });
        switched.EnsureSuccessStatusCode();
        var profiles = await client.GetFromJsonAsync<OrchestrationProfileStatus[]>(
            "/api/orchestration/rollout/profiles");
        Assert.Equal("shadow", profiles!.Single(item => item.Profile == "CodingCompletion").State);
        var receipts = await client.GetFromJsonAsync<OrchestrationSwitchReceipt[]>(
            "/api/orchestration/rollout/switch-receipts");
        Assert.Equal("Start comparison", Assert.Single(receipts!).Reason);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "orchestration-rollout-" + Guid.NewGuid().ToString("N"));
        public IConfiguration Configuration { get; }
        public OrchestrationRolloutStore Rollout { get; }
        public CountingApplier Applier { get; } = new();
        public MonolithOrchestrationStore Store { get; }

        public Fixture(int threshold = 50)
        {
            Directory.CreateDirectory(_directory);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _directory,
                ["Orchestration:ShadowMatchThreshold"] = threshold.ToString(),
            }).Build();
            Rollout = new(Configuration);
            Store = new(Path.Combine(_directory, ".orchestration-occurrences"),
                new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)),
                Applier, Rollout);
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }

    private sealed class CountingApplier : IMonolithOrchestrationApplier
    {
        public int Count { get; private set; }
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
