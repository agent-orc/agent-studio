using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the planner is pure and receives the fixed Now below.

/// <summary>
/// Rules of the shared <see cref="ModelSelectionPlanner"/> beyond the golden
/// matrix: hard pin and continuation (rules 1 and 2), the correctness floor of
/// a fallback (rule 7), the selection-ledger entry, the remote claim route
/// and the provider-rejection sibling lookup.
/// </summary>
public sealed class ModelSelectionPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ModelRoutingPolicyRegistry Registry = new();

    [Fact]
    public void Capped_primary_switches_to_the_profile_fallback_and_names_the_rule()
    {
        var result = ModelSelectionPlanner.Plan(Request(), Facts(claudeUsed: 97));

        Assert.Equal(SelectionOutcome.Fallback, result.Outcome);
        Assert.Equal(new SelectionRoute(CliTypes.Codex, ModelIds.Gpt56Sol, "high"), result.Route);
        Assert.Equal(new SelectionRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "high"), result.Primary);
        Assert.Equal(SelectionRules.QuotaCap, result.Rule);
        Assert.Equal("override", result.Equivalence?.Source);
        Assert.Equal(Now.AddDays(3), result.ReturnToPrimaryAt);
        Assert.Equal("Weekly", result.Quota?.Window);
        Assert.Equal(97, result.Quota?.UsedPct);
    }

    [Fact]
    public void Healthy_primary_launches_on_the_pin()
    {
        var result = ModelSelectionPlanner.Plan(Request(), Facts(claudeUsed: 30));

        Assert.Equal(SelectionOutcome.Primary, result.Outcome);
        Assert.Equal(SelectionRules.Pin, result.Rule);
        Assert.Equal(result.Primary, result.Route);
        Assert.Null(SelectionLedgerEntry.For(Request(), result, "local-coding-launch", "id", Now));
    }

    [Fact]
    public void Hard_pin_waits_instead_of_switching()
    {
        var request = Request() with
        {
            Pins = new SelectionPins(CliTypes.Claude, ModelIds.ClaudeOpus5, "high", SelectionPinMode.Hard),
        };

        var result = ModelSelectionPlanner.Plan(request, Facts(claudeUsed: 97));

        Assert.Equal(SelectionOutcome.Wait, result.Outcome);
        Assert.Equal(SelectionRules.Pin, result.Rule);
        Assert.Equal(new SelectionRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "high"), result.Route);
        Assert.Equal(Now.AddDays(3), result.RetryAt);
        Assert.Null(result.Fallback);
        Assert.StartsWith("waiting: hard pin keeps claude/claude-opus-5", result.Reason);
    }

    [Fact]
    public void Same_session_continuation_keeps_the_session_provider()
    {
        var request = Request() with
        {
            Pins = new SelectionPins(CliTypes.Codex, ModelIds.Gpt6Sol, "high"),
            Continuation = new SelectionContinuation(CliTypes.Claude, ModelIds.ClaudeOpus5),
        };

        var capped = ModelSelectionPlanner.Plan(request, Facts(claudeUsed: 97));
        var healthy = ModelSelectionPlanner.Plan(request, Facts(claudeUsed: 30));

        Assert.Equal(SelectionOutcome.Wait, capped.Outcome);
        Assert.Equal(CliTypes.Claude, capped.Route.CliType);
        Assert.Contains("same-session continuation", capped.Reason);
        Assert.Equal(SelectionOutcome.Primary, healthy.Outcome);
        Assert.Equal(new SelectionRoute(CliTypes.Claude, ModelIds.ClaudeOpus5, "high"), healthy.Route);
    }

    [Fact]
    public void Fallback_below_the_correctness_floor_waits_with_the_floor_rule()
    {
        var floor = Registry.Policy.Tiers.Single(tier => tier.Id == "sol-xhigh");
        var floorReads = 0;
        var facts = Facts(claudeUsed: 97) with
        {
            Tiers = Registry.Policy.Tiers,
            NormalizeModelId = ModelMetadataRegistry.NormalizeId,
            CorrectnessFloor = () => { floorReads++; return floor; },
        };

        var held = ModelSelectionPlanner.Plan(Request(), facts);
        var healthy = ModelSelectionPlanner.Plan(Request(), facts with { SnapshotFor = Snapshots(30) });

        Assert.Equal(SelectionOutcome.Wait, held.Outcome);
        Assert.Equal(SelectionRules.Floor, held.Rule);
        Assert.Equal(Now.AddDays(3), held.RetryAt);
        Assert.Equal(
            $"waiting: no host-accepted fallback clears this card's correctness floor; next reset {Now.AddDays(3):O}",
            held.Reason);
        Assert.Equal(SelectionOutcome.Primary, healthy.Outcome);
        Assert.Equal(1, floorReads);
    }

    [Fact]
    public void Ledger_entry_records_the_switch_with_scope_and_rule()
    {
        var request = Request() with { Project = "alpha", TaskKey = "AGT-1", Host = new SelectionHost("host-a") };
        var result = ModelSelectionPlanner.Plan(request, Facts(claudeUsed: 97));

        var entry = SelectionLedgerEntry.For(request, result, "remote-coding-claim", "event-1", Now);

        Assert.NotNull(entry);
        Assert.Equal(SelectionLedgerKinds.Switched, entry!.Kind);
        Assert.Equal(SelectionConsumers.Coding, entry.Consumer);
        Assert.Equal("host-a", entry.Host);
        Assert.Equal("alpha", entry.Project);
        Assert.Equal("AGT-1", entry.TaskKey);
        Assert.Equal(result.Primary, entry.From);
        Assert.Equal(result.Route, entry.To);
        Assert.Equal(SelectionRules.QuotaCap, entry.Rule);
        Assert.Equal(Now.AddDays(3), entry.ReturnToPrimaryAt);
    }

    [Theory]
    [InlineData(SelectionConsumers.ReviewAspect, true)]
    [InlineData(SelectionConsumers.ReviewDeep, true)]
    [InlineData(SelectionConsumers.ReviewGrade, true)]
    [InlineData(SelectionConsumers.ConceptFit, true)]
    [InlineData(SelectionConsumers.Coding, false)]
    [InlineData("one-shot:title", false)]
    public void Review_consumers_require_the_same_capability_class(string consumer, bool review)
        => Assert.Equal(review, SelectionConsumers.IsReview(consumer));

    [Fact]
    public void Claim_route_prefers_mechanical_then_operator_selection_then_provider_fallback()
    {
        var mechanical = new MechanicalFreshRunRoute(CliTypes.Codex, ModelIds.Gpt6Sol, "medium", "semantic-conflict");
        var fallback = new ProviderModelFallback(ModelIds.Gpt6Sol, ModelIds.Gpt56Sol, "capacity", CliTypes.Codex, "high");
        var intent = Intent(CliTypes.Claude, ModelIds.ClaudeOpus5, "xhigh", new ContinuationSelectionMask(Model: false, CliType: false, ThinkingLevel: true));

        Assert.Equal(
            new ClaimRouteSelection(CliTypes.Codex, ModelIds.Gpt6Sol, "medium", CleanContext: true, HasRoute: true),
            ModelSelectionPlanner.ClaimRoute(mechanical, intent, fallback));
        // An explicit thinking-only selection keeps the host model and never borrows the refusal route.
        Assert.Equal(
            new ClaimRouteSelection(CliTypes.Claude, null, "xhigh", CleanContext: false, HasRoute: true),
            ModelSelectionPlanner.ClaimRoute(null, intent, fallback));
        Assert.Equal(
            new ClaimRouteSelection(CliTypes.Codex, ModelIds.Gpt56Sol, "high", CleanContext: true, HasRoute: true),
            ModelSelectionPlanner.ClaimRoute(null, null, fallback));
        Assert.Equal(
            new ClaimRouteSelection(null, null, null, CleanContext: false, HasRoute: false),
            ModelSelectionPlanner.ClaimRoute(null, null, null));
    }

    [Fact]
    public void Rejection_sibling_uses_the_host_normalisation()
    {
        var rules = new[]
        {
            new ProviderModelFallback(ModelIds.Gpt6Sol, ModelIds.Gpt56Sol, "capacity", CliTypes.Codex, null),
        };

        Assert.Same(rules[0], ModelSelectionPlanner.RejectionSibling(
            rules, rule => (rule.CliType, rule.From), "CODEX", ModelIds.Gpt6Sol));
        Assert.Null(ModelSelectionPlanner.RejectionSibling(
            rules, rule => (rule.CliType, rule.From), CliTypes.Claude, ModelIds.Gpt6Sol));
        Assert.Equal(
            [ModelIds.Gpt6Sol, ModelIds.Gpt56Sol],
            ModelSelectionPlanner.SiblingChain(rules, rule => (rule.From, rule.To), ModelIds.Gpt6Sol));
    }

    private static SelectionRequest Request() => new()
    {
        Consumer = SelectionConsumers.Coding,
        Pins = new SelectionPins(CliTypes.Claude, ModelIds.ClaudeOpus5, "high"),
    };

    private static SelectionFacts Facts(double claudeUsed) => new()
    {
        NowUtc = Now,
        Caps = new FixedCaps(),
        SnapshotFor = Snapshots(claudeUsed),
        Routing = new SelectionRoutingFacts(
            cli => cli == CliTypes.Claude
                ? new CliModelRouteProfile
                {
                    CliType = CliTypes.Claude,
                    FallbackCliType = CliTypes.Codex,
                    FallbackModel = ModelIds.Gpt56Sol,
                    FallbackThinkingLevel = "high",
                }
                : null,
            null,
            (_, _) => []),
    };

    private static Func<string?, QuotaSnapshot?> Snapshots(double claudeUsed) => cli => cli switch
    {
        CliTypes.Claude => Snapshot(CliTypes.Claude, claudeUsed),
        CliTypes.Codex => Snapshot(CliTypes.Codex, 10),
        _ => null,
    };

    private static QuotaSnapshot Snapshot(string cli, double used) => new()
    {
        CliType = cli,
        FetchedAt = Now,
        Windows = [new QuotaWindow { Label = "Weekly", UsedPct = used, ResetAt = Now.AddDays(3) }],
    };

    private static ContinuationIntentProjection Intent(
        string cli, string model, string thinking, ContinuationSelectionMask selection)
        => new(
            new ContinuationIntentReceipt(
                "intent-1", "PROJ-1", "task-1", 1, 2, 1, 1, 1, "operator", "steer", Now, "policy-1",
                ExplicitSelection: true),
            "pending", "continue", model, cli, thinking, "continue", null, null, null, selection);

    private sealed class FixedCaps : IQuotaCapTable
    {
        public int GetCap(string cliType, string windowLabel) => QuotaCapDefaults.CapPct;
    }
}
