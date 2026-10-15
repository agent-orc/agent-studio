using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>A bounded, editable signal rule. Hard authority floors remain platform code.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DecisionRuleV1(
    string Id, string Trigger, string FactId, string? WarningThreshold,
    IReadOnlyList<string> CandidateActions, string AutonomyClass,
    string? Model, string MissingDataBehavior, int FreshnessMinutes = 60);

public sealed record DecisionRuleValidation(IReadOnlyList<string> Refusals)
{
    public bool Accepted => Refusals.Count == 0;
}

public sealed record DecisionRuleReplaySignals(IReadOnlyList<string> Signals,
    IReadOnlyList<string> Candidates, bool MissingEvidence);

public sealed record DecisionAuthorityFloorV1(string Id, string Scope, string Value,
    string Source, string Owner = "platform", string Version = "platform.v1");

/// <summary>Checks editable rules before the API can persist or activate a version.</summary>
public static class DecisionRulePolicy
{
    public static readonly IReadOnlyList<DecisionAuthorityFloorV1> HardFloors =
    [
        new("floor.fence", "card", "Current epoch, fence and settled result ref must match", "ActiveDecisionPolicy"),
        new("floor.a3", "all", "A3 requires a Human Operator", "ActiveDecisionPolicy"),
        new("floor.emergency-ceiling", "card", "24 hours maximum", "run-limit authority"),
        new("floor.card-spend", "card", "24 calls and 5 USD priced spend", "DecisionCostCapPolicy"),
        new("floor.host-spend", "host", "48 scheduled plus 12 event calls; 180000 input, 90000 output tokens; 10 USD", "DecisionCostCapPolicy"),
    ];

    public static DecisionRuleReplaySignals Replay(IReadOnlyList<DecisionRuleV1> rules,
        OrchestratorDecisionReceiptV1 receipt)
    {
        var applicable = rules.Where(rule => rule.Trigger == receipt.Trigger.Kind
            || rule.Trigger == receipt.Trigger.Profile).ToArray();
        var missing = applicable.Any(rule => receipt.Grounds.Facts.All(fact => fact.Id != rule.FactId));
        var signals = applicable.Select(rule => receipt.Grounds.Facts.FirstOrDefault(fact => fact.Id == rule.FactId)
                is { } fact ? $"{rule.Id}: {fact.Value} ({ThresholdState(rule, fact.Value)})"
                : $"{rule.Id}: missing").ToArray();
        var candidates = applicable.SelectMany(rule => rule.CandidateActions)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray();
        return new(signals, candidates, missing);
    }

    public static IReadOnlyList<GroundsFactV1> Signals(IReadOnlyList<DecisionRuleV1> rules,
        string trigger, IReadOnlyList<GroundsFactV1> facts, string policyVersion, DateTime observedAt)
        => rules.Where(rule => rule.Trigger == trigger).Take(16).Select(rule =>
        {
            var source = facts.FirstOrDefault(fact => fact.Id == rule.FactId);
            var value = source is null ? "missing"
                : rule.WarningThreshold is null ? "observed"
                : rule.WarningThreshold is { } threshold
                    && decimal.TryParse(source.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var actual)
                    && decimal.TryParse(threshold, NumberStyles.Number, CultureInfo.InvariantCulture, out var limit)
                    ? actual >= limit ? "warning" : "below-threshold" : "unknown";
            return new GroundsFactV1("rule." + rule.Id, "rule", "Rule signal", value, null,
                new GroundsSourceRefV1("rule-version", policyVersion), observedAt,
                source is null ? "missing" : source.Coverage,
                source is null ? GroundsReliability.Unreliable : source.Reliability);
        }).ToArray();

    public static DecisionRuleValidation Validate(IReadOnlyList<DecisionRuleV1> rules, bool projectOverlay)
    {
        var refusals = new List<string>();
        if (rules is null) return new(["Rules are required."]);
        if (rules.Count > 64) refusals.Add("Rule count exceeds the 64-rule bound.");
        foreach (var duplicate in rules.Where(rule => rule is not null && rule.Id is not null)
                     .GroupBy(rule => rule.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            refusals.Add($"Duplicate rule id: {duplicate.Key}.");
        foreach (var rule in rules)
        {
            if (rule is null) { refusals.Add("A rule entry is null."); continue; }
            if (rule.Id is null || rule.Trigger is null || rule.FactId is null
                || rule.CandidateActions is null || rule.AutonomyClass is null
                || rule.MissingDataBehavior is null)
            {
                refusals.Add("A rule has a missing required field.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 80
                || !Regex.IsMatch(rule.Id, "^[a-z][a-z0-9.-]*$", RegexOptions.CultureInvariant)
                || string.IsNullOrWhiteSpace(rule.Trigger) || rule.Trigger.Length > 80
                || string.IsNullOrWhiteSpace(rule.FactId) || rule.FactId.Length > 100)
                refusals.Add($"Rule {rule.Id} has an invalid id, trigger, or fact id.");
            if (rule.Id.StartsWith("floor.", StringComparison.OrdinalIgnoreCase)
                || rule.FactId.Contains("raw-session", StringComparison.OrdinalIgnoreCase)
                || rule.FactId.Contains("filesystem", StringComparison.OrdinalIgnoreCase))
                refusals.Add($"Rule {rule.Id} cannot edit authority floors or read raw sessions or paths.");
            if (rule.CandidateActions.Count > 9 || rule.CandidateActions.Any(action =>
                    !DecisionActionKinds.All.Contains(action, StringComparer.Ordinal)))
                refusals.Add($"Rule {rule.Id} has an action outside the closed union.");
            if (rule.AutonomyClass is not ("A0" or "A1" or "A2" or "A3"))
                refusals.Add($"Rule {rule.Id} has an invalid autonomy class.");
            if (rule.MissingDataBehavior is not ("ask" or "hold" or "signal"))
                refusals.Add($"Rule {rule.Id} has an invalid missing-data behavior.");
            if (rule.FreshnessMinutes is < 1 or > 1440)
                refusals.Add($"Rule {rule.Id} has an unbounded freshness window.");
            if (rule.WarningThreshold is { Length: > 0 } threshold
                && !decimal.TryParse(threshold, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                refusals.Add($"Rule {rule.Id} needs a numeric warning threshold.");
            if (projectOverlay && rule.Trigger is ("capacity-check" or "host-wide"))
                refusals.Add($"Project rule {rule.Id} cannot govern host-wide actions.");
            if (rule.Model is { Length: > 0 } model && !TrustedModel(model))
                refusals.Add($"Rule {rule.Id} sets a model below the trusted correctness floor or an unknown model.");
        }
        return new(refusals);
    }

    // The registry's exact model routing remains authoritative at call time. The
    // editor accepts only the strong model families chosen for this decision role.
    private static bool TrustedModel(string model)
        => model.StartsWith("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-6-sol", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-6-astra", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("claude-opus", StringComparison.OrdinalIgnoreCase);

    private static string ThresholdState(DecisionRuleV1 rule, string value)
    {
        if (rule.WarningThreshold is null) return "observed";
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var observed)
               && decimal.TryParse(rule.WarningThreshold, NumberStyles.Number,
                   CultureInfo.InvariantCulture, out var threshold)
            ? observed >= threshold ? "warning" : "below threshold" : "unknown";
    }
}
