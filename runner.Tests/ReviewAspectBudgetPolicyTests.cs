using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2820: ten cards routed to <c>claude-opus-5</c> had every review fail
/// because the per-aspect budget was a constant sized for a small model.
/// <c>aspect-code-quality</c> consumed 122202 ms against a 120000 ms limit and
/// was reported as a broken toolchain; <c>aspect-requirement-fit</c> on the same
/// model and the same attempt finished in 78020 ms and passed. The budget is now
/// derived, and the derivation is part of the contract.
/// </summary>
public sealed class ReviewAspectBudgetPolicyTests
{
    [Fact]
    public void A_flagship_model_gets_a_longer_budget_than_an_economy_model_on_the_same_toolchain()
    {
        var opus = ReviewAspectBudgetPolicy.Derive("claude", "claude-opus-5", "high", 0);
        var sonnet = ReviewAspectBudgetPolicy.Derive("claude", "claude-sonnet-5", "high", 0);
        var haiku = ReviewAspectBudgetPolicy.Derive("claude", "claude-haiku-4-5", "high", 0);

        Assert.True(opus.Seconds > sonnet.Seconds);
        Assert.True(sonnet.Seconds > haiku.Seconds);
    }

    /// <summary>
    /// The exact incident: the observed Opus aspect call needed 122202 ms, which
    /// the old flat 120000 ms limit refused. Whatever else the derivation does,
    /// it must clear that measured number.
    /// </summary>
    [Fact]
    public void The_observed_opus_aspect_call_fits_inside_the_derived_budget()
    {
        var budget = ReviewAspectBudgetPolicy.Derive("claude", "claude-opus-5", "high", 40_000);

        Assert.True(
            budget.LimitMilliseconds > 122_202,
            $"expected more than the 122202 ms the incident consumed, got {budget.LimitMilliseconds} ms");
    }

    [Fact]
    public void A_bigger_diff_buys_more_budget_up_to_a_bounded_ceiling()
    {
        var small = ReviewAspectBudgetPolicy.Derive("codex", "gpt-5.4-mini", "low", 10_000);
        var large = ReviewAspectBudgetPolicy.Derive("codex", "gpt-5.4-mini", "low", 400_000);
        var enormous = ReviewAspectBudgetPolicy.Derive("codex", "gpt-5.4-mini", "low", 40_000_000);

        Assert.True(large.Seconds > small.Seconds);
        Assert.Equal(
            ReviewAspectBudgetDefaults.CodexBaseSeconds
            + ReviewAspectBudgetDefaults.MaximumMaterialSeconds,
            enormous.Seconds);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void More_thinking_never_shortens_the_budget(string thinkingLevel)
    {
        var floor = ReviewAspectBudgetPolicy.Derive("claude", "claude-sonnet-5", "low", 0);
        var level = ReviewAspectBudgetPolicy.Derive("claude", "claude-sonnet-5", thinkingLevel, 0);

        Assert.True(level.Seconds >= floor.Seconds);
    }

    /// <summary>
    /// An unrecognized model is more likely to be a new flagship than a new
    /// economy model, and a budget that is too short produces a false
    /// infrastructure verdict about a healthy change.
    /// </summary>
    [Fact]
    public void An_unknown_model_is_budgeted_above_the_mid_tier_not_below_it()
    {
        var unknown = ReviewAspectBudgetPolicy.Derive("claude", "claude-nebula-9", "medium", 0);
        var mid = ReviewAspectBudgetPolicy.Derive("claude", "claude-sonnet-5", "medium", 0);

        Assert.True(unknown.Seconds > mid.Seconds);
    }

    [Fact]
    public void An_operator_base_override_still_scales_by_model_and_material()
    {
        var overridden = ReviewAspectBudgetPolicy.Derive(
            "claude", "claude-opus-5", "low", 0, configuredBaseSeconds: 100);

        Assert.Equal(200, overridden.Seconds);
        Assert.Contains("base=100s", overridden.Derivation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_budget_is_clamped_to_the_seven_thousand_two_hundred_second_ceiling()
    {
        var budget = ReviewAspectBudgetPolicy.Derive(
            "claude", "claude-opus-5", "max", 40_000_000, configuredBaseSeconds: 7200);

        Assert.Equal(ReviewAspectBudgetDefaults.CeilingSeconds, budget.Seconds);
    }

    /// <summary>
    /// A violation has to be legible without opening the plan: which model, what
    /// the limit was, and how the limit was reached.
    /// </summary>
    [Fact]
    public void The_description_names_the_model_the_limit_and_the_derivation()
    {
        var budget = ReviewAspectBudgetPolicy.Derive("claude", "claude-opus-5", "high", 12_000);

        var description = budget.Describe();

        Assert.Contains("model=claude-opus-5", description, StringComparison.Ordinal);
        Assert.Contains($"limit={budget.LimitMilliseconds}ms", description, StringComparison.Ordinal);
        Assert.Contains("base=", description, StringComparison.Ordinal);
        Assert.Contains("material=", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Material_characters_count_the_prompt_and_everything_appended_to_it()
    {
        Assert.Equal(0, ReviewAspectBudgetPolicy.MaterialCharacters(null));
        Assert.Equal(7, ReviewAspectBudgetPolicy.MaterialCharacters("abcd", "efg"));
    }
}
