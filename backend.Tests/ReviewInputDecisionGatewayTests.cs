using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewInputDecisionGatewayTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_switch_preserves_the_monolith_route()
    {
        var config = Configuration();
        var clock = new FakeTimeProvider(At);
        var runs = new MonolithOrchestrationStore(null, clock, new NoApplier());
        var receipts = new OrchestratorDecisionStore((string?)null, clock, new NoPricing());
        var gateway = Gateway(config, runs, receipts, clock);

        Assert.False(gateway.EngineDecides);
        Assert.Empty(runs.List(null, null));
        Assert.Throws<InvalidOperationException>(() => gateway.Submit(Request()));
    }

    [Fact]
    public void Engine_switch_freezes_bounded_input_and_exposes_one_claimable_occurrence()
    {
        var config = Configuration("Engine");
        var clock = new FakeTimeProvider(At);
        var runs = new MonolithOrchestrationStore(null, clock, new NoApplier());
        var receipts = new OrchestratorDecisionStore((string?)null, clock, new NoPricing());
        var gateway = Gateway(config, runs, receipts, clock);

        var first = gateway.Submit(Request());
        clock.Advance(TimeSpan.FromMinutes(1));
        var repeated = gateway.Submit(Request());
        var claim = runs.Claim(new OrchestrationClaimRequest("engine_test", "instance",
            [OrchestrationStage.ReviewInputDecision]));

        Assert.True(gateway.EngineDecides);
        Assert.Equal(first.RunId, repeated.RunId);
        Assert.Equal(first.RunId, claim.Run?.RunId);
        Assert.Equal("ReviewDecision", runs.GetOccurrence(first.RunId)?.Profile);
        var frozen = receipts.GetInputs(first.RunId);
        Assert.NotNull(frozen);
        Assert.Equal(DecisionReceiptPolicy.ActiveMode, frozen.Mode);
        Assert.Contains(frozen.Grounds.Omissions, item => item.Reason == "secret");
        Assert.Contains(frozen.Grounds.Facts, item => item.Source.Ref ==
            $"/api/orchestration/review-inputs/{first.RunId}");
        Assert.DoesNotContain("cli-output.log", first.PayloadJson);
    }

    [Fact]
    public void Oversize_and_secret_summary_are_omitted_before_the_occurrence_is_saved()
    {
        var clock = new FakeTimeProvider(At);
        var runs = new MonolithOrchestrationStore(null, clock, new NoApplier());
        var receipts = new OrchestratorDecisionStore((string?)null, clock, new NoPricing());
        var gateway = Gateway(Configuration("Engine"), runs, receipts, clock);
        var secret = "api_key=" + new string('x', 24);

        var run = gateway.Submit(Request() with
        {
            Subject = "/tmp/sessions/run/cli-output.log " + secret,
            Summary = secret + " " + new string('q', 10_000),
        });

        Assert.DoesNotContain(secret, run.PayloadJson);
        Assert.True(run.PayloadJson.Length < ReviewInputDecisionGateway.MaxSummaryChars + 2_000);
        var input = receipts.GetInputs(run.RunId)!;
        Assert.Contains(input.Grounds.Omissions, item => item.Reason == "secret");
        Assert.Contains(input.Grounds.Omissions, item => item.Reason == "oversize");
        Assert.Contains(input.Grounds.Omissions, item => item.Reason == "raw-session-path");
        Assert.All(input.Grounds.Facts, item => Assert.DoesNotContain(secret, item.Value));
        Assert.DoesNotContain("/tmp/sessions/run/cli-output.log", run.PayloadJson);
    }

    [Fact]
    public void Exhausted_abort_loop_is_frozen_for_a_model_choice()
    {
        var clock = new FakeTimeProvider(At);
        var runs = new MonolithOrchestrationStore(null, clock, new NoApplier());
        var receipts = new OrchestratorDecisionStore((string?)null, clock, new NoPricing());
        var gateway = Gateway(Configuration("Engine"), runs, receipts, clock);
        var signal = LoopExhaustionPolicy.Signal("abort-review.rerun-per-job", 2, 2,
            "per-card", "attempt-2", ["retry", "ask"]);

        var run = gateway.Submit(Request() with { Kind = "abort-review", LoopSignal = signal });
        var input = receipts.GetInputs(run.RunId)!;

        Assert.Equal(signal, Assert.Single(input.Grounds.LoopSignals));
        Assert.True(input.Grounds.Alternatives.Single(item => item.Action == DecisionActionKinds.Retry).Available);
    }

    private static IConfiguration Configuration(string? mode = null)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestration:Transitions:ReviewDecision"] = mode,
        }).Build();

    private static ReviewInputDecisionGateway Gateway(IConfiguration config,
        MonolithOrchestrationStore runs, OrchestratorDecisionStore receipts, TimeProvider clock)
    {
        var services = new ServiceCollection().AddSingleton(runs).BuildServiceProvider();
        return new ReviewInputDecisionGateway(config, services, receipts, clock);
    }

    private static ReviewInputDecisionRequest Request()
        => new("needs-input", "project", "card", TaskStates.AutoReview, "card:signal:2",
            "The card needs a typed choice.", "sha256:" + new string('a', 64),
            [new GroundsOmissionV1("log.window", "secret")], "card");

    private sealed class NoApplier : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoPricing : ITokenPricing
    {
        public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
            long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null)
            => new(0, 0, 0, 0, 0, modelId ?? "unknown", false,
                TokenEconomy.PriceStatus.UnknownModel, null);

        public IReadOnlyList<PricedTokenEntry> Price(IReadOnlyList<OrchestratorLogEntry> ledger)
            => throw new NotSupportedException();
    }
}
