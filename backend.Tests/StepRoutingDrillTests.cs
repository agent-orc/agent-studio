using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: all quota/reset and circuit observations use fixed injected time.
public sealed class StepRoutingDrillTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Every_model_step_switches_in_both_directions_and_waits_when_both_are_full(string provider)
    {
        foreach (var work in new[] { SelectionWorkClasses.Coding, SelectionWorkClasses.Review,
                     SelectionWorkClasses.Grade, SelectionWorkClasses.Summary, SelectionWorkClasses.Enrichment,
                     SelectionWorkClasses.Conflict, SelectionWorkClasses.Concept })
        {
            var request = Request(provider) with { WorkClass = work };
            foreach (var continuation in new[] { false, true })
            {
                var call = request with { Continuation = continuation
                    ? new SelectionContinuation(provider, request.Pins.Model) : null };
                var switched = ModelSelectionPlanner.Plan(call, Facts(provider, bothFull: false));
                Assert.Equal(SelectionOutcome.Fallback, switched.Outcome);
                Assert.NotEqual(provider, switched.Route.CliType);
                Assert.Equal("fixture-equivalence-v1", switched.Equivalence?.CatalogueVersion);
                var receipt = SelectionLedgerEntry.For(call, switched, "drill", "switch", Now);
                Assert.Equal(provider, receipt?.From.CliType);
                Assert.Equal(switched.Route, receipt?.To);
                Assert.NotEmpty(receipt!.Reason);
                var waiting = ModelSelectionPlanner.Plan(call, Facts(provider, bothFull: true));
                Assert.Equal(SelectionOutcome.Wait, waiting.Outcome);
                Assert.False(waiting.ShouldLaunch);
                Assert.NotEmpty(waiting.Reason);
                Assert.NotNull(waiting.RetryAt);
            }
        }
    }

    [Fact]
    public void A_held_review_switch_keeps_the_published_reasoning_mapping()
    {
        var request = new SelectionRequest { Consumer = SelectionConsumers.ReviewAspect,
            Pins = new("claude", "claude-sonnet-5", "high"), Required = new(SameClassOnly: true) };
        var facts = Facts("claude", false) with
        {
            Routing = new(_ => null, new ModelEquivalenceCatalog(TokenTestServices.Pricing()), ModelMetadataRegistry.ThinkingLevelsFor),
        };
        var first = ModelSelectionPlanner.Plan(request, facts);
        Assert.Equal("medium", first.Route.ThinkingLevel);
        var held = ModelSelectionPlanner.Plan(request, facts with
        {
            NowUtc = Now.AddMinutes(1),
            ActiveSwitch = SelectionLedgerEntry.For(request, first, "drill", "active", Now),
        });
        Assert.Equal(SelectionOutcome.Fallback, held.Outcome);
        Assert.Equal(first.Route, held.Route);
    }

    [Fact]
    public void Reserve_moves_new_coding_but_allows_short_steps_and_healthy_resumes()
    {
        var facts = Facts("claude", false, used: 86) with { ShortStepReservePoints = 10 };
        var coding = Request("claude") with { WorkClass = SelectionWorkClasses.Coding };
        Assert.Equal(SelectionOutcome.Fallback, ModelSelectionPlanner.Plan(coding, facts).Outcome);
        Assert.Equal(SelectionOutcome.Primary, ModelSelectionPlanner.Plan(coding with
        {
            Continuation = new SelectionContinuation("claude", coding.Pins.Model),
        }, facts).Outcome);
        foreach (var work in new[] { SelectionWorkClasses.Review, SelectionWorkClasses.Grade,
                     SelectionWorkClasses.Summary, SelectionWorkClasses.Enrichment })
            Assert.Equal(SelectionOutcome.Primary,
                ModelSelectionPlanner.Plan(coding with { WorkClass = work }, facts).Outcome);
        Assert.Equal(SelectionOutcome.Wait, ModelSelectionPlanner.Plan(coding with
        {
            Pins = coding.Pins with { Mode = SelectionPinMode.Hard },
        }, facts).Outcome);
    }

    [Fact]
    public void Short_review_uses_the_equivalent_instead_of_waiting_for_a_nearby_reset()
    {
        var result = ModelSelectionPlanner.Plan(Request("claude") with { WorkClass = SelectionWorkClasses.Review,
                Pins = new("claude", "fixture-claude", "low") },
            Facts("claude", false) with
            {
                WaitPolicy = new ResolvedCliQuotaWaitPolicy(true, 60, "fixture", null, null, true, 60),
                SnapshotFor = provider => new QuotaSnapshot { CliType = provider!, FetchedAt = Now,
                    Windows = [new() { Label = "Weekly", UsedPct = provider == "claude" ? 100 : 10,
                        ResetAt = Now.AddMinutes(10) }] },
            });
        Assert.Equal(SelectionOutcome.Fallback, result.Outcome);
    }

    [Theory]
    [InlineData("sign-in unavailable")]
    [InlineData("probe unhealthy")]
    [InlineData("provider error rate circuit open")]
    public void Unavailable_provider_switches_before_start_and_hard_pin_waits(string reason)
    {
        var facts = Facts("claude", false, used: 10) with
        {
            ProviderUnavailable = cli => cli == "claude" ? reason : null,
        };
        var request = Request("claude");
        var result = ModelSelectionPlanner.Plan(request, facts);
        Assert.Equal(SelectionOutcome.Fallback, result.Outcome);
        Assert.Contains(reason, result.Reason);
        Assert.Equal(SelectionOutcome.Wait, ModelSelectionPlanner.Plan(request with
        {
            Pins = request.Pins with { Mode = SelectionPinMode.Hard },
        }, facts).Outcome);
        Assert.Equal(SelectionOutcome.Wait, ModelSelectionPlanner.Plan(request,
            facts with { ProviderUnavailable = _ => reason }).Outcome);
    }

    [Fact]
    public void Missing_equivalence_waits_and_names_the_missing_pair()
    {
        var request = Request("claude") with { Pins = new("claude", "unqualified-model", "low") };
        var result = ModelSelectionPlanner.Plan(request, Facts("claude", false));
        Assert.Equal(SelectionOutcome.Wait, result.Outcome);
        Assert.Contains("equivalent", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Healthy_continuation_ignores_operator_conservation_and_projection()
    {
        var request = Request("claude") with { Continuation = new("claude", "fixture-claude") };
        var result = ModelSelectionPlanner.Plan(request, Facts("claude", false, 20) with
        {
            OperatorSwitch = new CliFallbackPreference("claude", true, Now, Now.AddDays(1)),
        });
        Assert.Equal(SelectionOutcome.Primary, result.Outcome);
        Assert.Equal("claude", result.Route.CliType);
    }

    private static SelectionRequest Request(string provider) => new()
    {
        Consumer = SelectionConsumers.Coding,
        Pins = new(provider, "fixture-" + provider, "high"),
    };

    private static SelectionFacts Facts(string fullProvider, bool bothFull, double used = 100) => new()
    {
        NowUtc = Now,
        Caps = new Caps(),
        SnapshotFor = provider => new QuotaSnapshot
        {
            CliType = provider!, FetchedAt = Now,
            Windows = [new() { Label = "Weekly", UsedPct = bothFull || provider == fullProvider ? used : 10,
                ResetAt = Now.AddDays(2) }],
        },
        Routing = new(_ => null, new Catalogue(), (_, _) => ["low", "medium", "high"]),
    };

    private sealed class Caps : IQuotaCapTable
    {
        public int GetCap(string cliType, string windowLabel) => 95;
    }

    private sealed class Catalogue : IModelEquivalenceCatalog
    {
        public string Version => "fixture-equivalence-v1";
        public IReadOnlyList<ModelEquivalenceRoute> Routes => [];
        public bool SameCapabilityClass(string? fromModel, string? toModel) => true;
        public (string Model, string? ThinkingLevel)? TryGetEquivalent(string fromCliType, string? fromModel,
            string? fromThinkingLevel, string toCliType)
            => TryGetRoute(fromCliType, fromModel, fromThinkingLevel, toCliType) is { } route
                ? (route.ToModel, route.ToThinkingLevel) : null;
        public ModelEquivalenceRoute? TryGetRoute(string fromCliType, string? fromModel,
            string? fromThinkingLevel, string toCliType)
            => fromModel != "fixture-" + fromCliType ? null : new(fromCliType, fromModel, fromThinkingLevel,
                toCliType, "fixture-" + toCliType, fromThinkingLevel, Version, null, null, null, null, "fixture");
    }
}
