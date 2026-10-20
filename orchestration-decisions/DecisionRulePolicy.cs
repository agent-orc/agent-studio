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

/// <summary>One catalogue model, the CLI that serves it, and whether it clears the trusted floor.</summary>
public sealed record DecisionModelOptionV1(string Model, string Cli, bool Trusted);

/// <summary>The models the configured decision CLI can be asked to run.</summary>
public sealed record DecisionRuleModelCatalogue(string Cli, IReadOnlyList<DecisionModelOptionV1> Models);

/// <summary>
/// The closed trigger list. Each entry is read by a live decision path: the
/// coding completion step transition, or the card-free capacity check.
/// </summary>
public static class DecisionRuleTriggers
{
    public const string StepTransition = "step-transition";
    public const string CodingCompletion = "CodingCompletion";
    public const string CapacityCheck = "capacity-check";

    public static readonly IReadOnlyList<string> Card = [StepTransition, CodingCompletion];
    public static readonly IReadOnlyList<string> Host = [CapacityCheck];
    public static readonly IReadOnlyList<string> All = [.. Card, .. Host];

    /// <summary>Facts every coding completion occurrence carries.</summary>
    public static readonly IReadOnlyList<string> CodingCompletionFacts =
    [
        "run.outcome", "attempt.fence", "attempt.epoch", "card.lane", "transition.proposed-next",
        "result.sha", "result.base-sha", "result.envelope", "result.ref", "review.last-outcome",
        "ledger.tokens", "ledger.priced-usd", "ledger.price-status", "ledger.unpriced-rows",
        "ledger.excluded-duplicates", "progress.rounds-without-progress", "progress.exhaustions",
        "progress.last-verified-tree", "signal.gate-duration", "signal.review-aspect",
        "signal.coding-run", "signal.salvage-continuation",
    ];

    /// <summary>The capacity check evidence vocabulary; a host occurrence submits no other fact.</summary>
    public static readonly IReadOnlyList<string> CapacityCheckFacts =
    [
        "host.load-band", "host.cpu-percent", "host.load15", "host.memory-percent",
        "host.role-occupancy", "host.role-throttling", "host.queue", "host.oldest-wait-minutes",
        "host.admission-verdicts", "host.allocation-changes",
    ];

    public static bool IsCard(string? trigger) => trigger is not null && Card.Contains(trigger, StringComparer.Ordinal);

    public static bool IsHost(string? trigger) => trigger is not null && Host.Contains(trigger, StringComparer.Ordinal);

    /// <summary>
    /// Facts observed at an earlier event rather than at the occurrence. They are
    /// usually stale, so an ask or hold rule on them would stop most occurrences.
    /// </summary>
    public static readonly IReadOnlyList<string> HistoricalFacts = ["review.last-outcome"];

    public static IReadOnlyList<string> KnownFacts(string? trigger)
        => IsCard(trigger) ? CodingCompletionFacts : IsHost(trigger) ? CapacityCheckFacts : [];

    /// <summary>Facts an ask or hold rule may read: known facts observed at the occurrence.</summary>
    public static IReadOnlyList<string> EvidenceFacts(string? trigger)
        => KnownFacts(trigger).Except(HistoricalFacts, StringComparer.Ordinal).ToArray();
}

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
        var missing = applicable.Any(rule => FreshFact(rule, receipt.Grounds.Facts,
            receipt.Trigger.ObservedAt) is null);
        var signals = applicable.Select(rule => FreshFact(rule, receipt.Grounds.Facts,
                receipt.Trigger.ObservedAt) is { } fact
                ? $"{rule.Id}: {fact.Value} ({ThresholdState(rule, fact.Value)})"
                : $"{rule.Id}: missing or stale").ToArray();
        // A frozen receipt retains every offered candidate. Remove only refusals
        // introduced by the old rule version before evaluating a new version.
        var offered = receipt.Grounds.Alternatives.Select(candidate =>
            candidate.RejectionReason is "rule-candidate-excluded" or "rule-missing-evidence-hold"
                or "rule-missing-evidence-ask" or "human-operator-required"
                ? candidate with { Available = true, RejectionReason = null } : candidate).ToArray();
        var candidates = Apply(rules, receipt.Trigger.Kind, receipt.Trigger.Profile,
            receipt.Grounds.Facts, offered, receipt.Trigger.ObservedAt)
            .Where(candidate => candidate.Available).Select(candidate => candidate.Action)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray();
        return new(signals, candidates, missing);
    }

    /// <summary>Apply editable limits to host-offered candidates without raising platform authority.</summary>
    public static IReadOnlyList<GroundsAlternativeV1> Apply(IReadOnlyList<DecisionRuleV1> rules,
        string trigger, string profile, IReadOnlyList<GroundsFactV1> facts,
        IReadOnlyList<GroundsAlternativeV1> alternatives, DateTime observedAt)
    {
        var applicable = rules.Where(rule => rule.Trigger == trigger || rule.Trigger == profile).ToArray();
        if (applicable.Length == 0) return alternatives.ToArray();
        var allowed = applicable.SelectMany(rule => rule.CandidateActions).ToHashSet(StringComparer.Ordinal);
        var missing = applicable.Where(rule => FreshFact(rule, facts, observedAt) is null).ToArray();
        var hold = missing.Any(rule => rule.MissingDataBehavior == "hold");
        var ask = missing.Any(rule => rule.MissingDataBehavior == "ask");
        var result = alternatives.Select(candidate =>
        {
            var matching = applicable.Where(rule => rule.CandidateActions.Contains(candidate.Action,
                StringComparer.Ordinal)).ToArray();
            var approval = matching.Select(rule => rule.AutonomyClass)
                .Append(candidate.RequiredApproval).OrderByDescending(value => value,
                    StringComparer.Ordinal).First();
            var refusal = !candidate.Available ? candidate.RejectionReason
                : !allowed.Contains(candidate.Action)
                    && !(ask && candidate.Action == DecisionActionKinds.Ask)
                    ? "rule-candidate-excluded"
                : hold ? "rule-missing-evidence-hold"
                : ask && candidate.Action != DecisionActionKinds.Ask ? "rule-missing-evidence-ask"
                : approval == "A3" ? "human-operator-required" : null;
            return candidate with { RequiredApproval = approval,
                Available = candidate.Available && refusal is null, RejectionReason = refusal };
        }).ToList();
        if (ask && !hold && result.All(candidate => candidate.Action != DecisionActionKinds.Ask))
            result.Add(new(DecisionActionKinds.Ask, "operator:ai", "reversible", "A1", true));
        return result;
    }

    private static GroundsFactV1? FreshFact(DecisionRuleV1 rule,
        IReadOnlyList<GroundsFactV1> facts, DateTime observedAt)
        => facts.FirstOrDefault(fact => fact.Id == rule.FactId
            && fact.ObservedAt <= observedAt
            && observedAt - fact.ObservedAt <= TimeSpan.FromMinutes(rule.FreshnessMinutes));

    public static IReadOnlyList<GroundsFactV1> Signals(IReadOnlyList<DecisionRuleV1> rules,
        string trigger, IReadOnlyList<GroundsFactV1> facts, string policyVersion, DateTime observedAt)
        => rules.Where(rule => rule.Trigger == trigger).Take(16).Select(rule =>
        {
            var source = FreshFact(rule, facts, observedAt);
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

    public static DecisionRuleValidation Validate(IReadOnlyList<DecisionRuleV1> rules, bool projectOverlay,
        DecisionRuleModelCatalogue models)
    {
        var refusals = new List<string>();
        if (rules is null) return new(["Rules are required."]);
        if (rules.Count > 64) refusals.Add("Rule count exceeds the 64-rule bound.");
        foreach (var duplicate in rules.Where(rule => rule is not null && rule.Id is not null)
                     .GroupBy(rule => rule.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            refusals.Add($"Duplicate rule id: {duplicate.Key}.");
        var complete = true;
        foreach (var rule in rules)
        {
            if (rule is null) { refusals.Add("A rule entry is null."); complete = false; continue; }
            if (rule.Id is null || rule.Trigger is null || rule.FactId is null
                || rule.CandidateActions is null || rule.AutonomyClass is null
                || rule.MissingDataBehavior is null)
            {
                refusals.Add("A rule has a missing required field.");
                complete = false;
                continue;
            }
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 80
                || !Regex.IsMatch(rule.Id, "^[a-z][a-z0-9.-]*$", RegexOptions.CultureInvariant)
                || string.IsNullOrWhiteSpace(rule.Trigger) || rule.Trigger.Length > 80
                || string.IsNullOrWhiteSpace(rule.FactId) || rule.FactId.Length > 100)
                refusals.Add($"Rule {rule.Id} has an invalid id, trigger, or fact id.");
            if (!DecisionRuleTriggers.All.Contains(rule.Trigger, StringComparer.Ordinal))
                refusals.Add($"Rule {rule.Id} uses trigger {rule.Trigger}, which no live decision path reads.");
            if (rule.Id.StartsWith("floor.", StringComparison.OrdinalIgnoreCase)
                || rule.FactId.Contains("raw-session", StringComparison.OrdinalIgnoreCase)
                || rule.FactId.Contains("filesystem", StringComparison.OrdinalIgnoreCase))
                refusals.Add($"Rule {rule.Id} cannot edit authority floors or read raw sessions or paths.");
            if (rule.CandidateActions.Count == 0)
                refusals.Add($"Rule {rule.Id} needs at least one candidate action.");
            if (rule.CandidateActions.Count > 9 || rule.CandidateActions.Any(action =>
                    !DecisionActionKinds.All.Contains(action, StringComparer.Ordinal)))
                refusals.Add($"Rule {rule.Id} has an action outside the closed union.");
            if (rule.AutonomyClass is not ("A0" or "A1" or "A2" or "A3"))
                refusals.Add($"Rule {rule.Id} has an invalid autonomy class.");
            if (rule.MissingDataBehavior is not ("ask" or "hold" or "signal"))
                refusals.Add($"Rule {rule.Id} has an invalid missing-data behavior.");
            // An ask or hold on a fact the trigger never carries, or carries from an
            // earlier event, would ask or hold nearly every occurrence.
            if (rule.MissingDataBehavior is "ask" or "hold"
                && DecisionRuleTriggers.HistoricalFacts.Contains(rule.FactId, StringComparer.Ordinal))
                refusals.Add($"{Behavior(rule)} rule {rule.Id} reads {rule.FactId}, which is observed at an earlier event and usually stale; use signal.");
            else if (rule.MissingDataBehavior is "ask" or "hold"
                && !DecisionRuleTriggers.KnownFacts(rule.Trigger).Contains(rule.FactId, StringComparer.Ordinal))
                refusals.Add($"{Behavior(rule)} rule {rule.Id} reads {rule.FactId}, which is not a known fact of {rule.Trigger}.");
            if (rule.FreshnessMinutes is < 1 or > 1440)
                refusals.Add($"Rule {rule.Id} has an unbounded freshness window.");
            if (rule.WarningThreshold is { Length: > 0 } threshold
                && !decimal.TryParse(threshold, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                refusals.Add($"Rule {rule.Id} needs a numeric warning threshold.");
            if (projectOverlay && DecisionRuleTriggers.IsHost(rule.Trigger))
                refusals.Add($"Project rule {rule.Id} cannot govern host-wide actions.");
            if (rule.Model is { Length: > 0 } model && ModelRefusal(model, models) is { } modelRefusal)
                refusals.Add($"Rule {rule.Id} {modelRefusal}");
        }
        // A project overlay is checked against its merged workspace rules by the caller.
        if (complete && !projectOverlay && CardAlternativeRefusal(rules) is { } cardRefusal)
            refusals.Add(cardRefusal);
        return new(refusals);
    }

    /// <summary>
    /// Null when the card rules leave a non-ask coding completion alternative
    /// inside the A0/A1 floor for every deterministic outcome, with or without a
    /// settled envelope, and without a verified tree, result SHA or ref. Only
    /// facts every coding completion carries are present. Without such an
    /// alternative, cards fall to ask or hold.
    /// </summary>
    public static string? CardAlternativeRefusal(IReadOnlyList<DecisionRuleV1> rules)
    {
        var card = rules.Where(rule => DecisionRuleTriggers.IsCard(rule.Trigger)).ToArray();
        if (card.Length == 0) return null;
        var at = DateTime.UnixEpoch;
        var known = DecisionRuleTriggers.EvidenceFacts(DecisionRuleTriggers.CodingCompletion);
        var facts = card.Select(rule => rule.FactId).Where(id => known.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(id => new GroundsFactV1(id, "rule", id, "0", null, new("rule-check", id), at,
                "complete", GroundsReliability.Reliable)).ToArray();
        foreach (var outcome in Enum.GetValues<TransitionAction>())
        foreach (var settled in new[] { false, true })
        {
            var offered = DecisionGroundsPolicy.CodingCompletionAlternatives(
                new TransitionDecision(outcome, "rule check",
                    new DecisionReceipt("rule-check", "", "", 0, 0, []), [], []),
                settled, lastVerifiedTreeSha: null, resultSha: null, resultRef: null);
            var applied = Apply(card, DecisionRuleTriggers.StepTransition, DecisionRuleTriggers.CodingCompletion,
                facts, offered, at);
            if (!applied.Any(candidate => candidate.Available && candidate.Action != DecisionActionKinds.Ask
                    && candidate.RequiredApproval is "A0" or "A1"))
                return "Card rules leave no available non-ask CodingCompletion alternative within the A0/A1 floor"
                    + $" when the deterministic outcome is {DecisionGroundsPolicy.Union(outcome)}"
                    + $" ({outcome}), the envelope is {(settled ? "settled" : "not settled")},"
                    + " and no verified tree, result SHA or ref exists.";
        }
        return null;
    }

    /// <summary>Null when the configured decision CLI serves the model at the trusted floor.</summary>
    public static string? ModelRefusal(string model, DecisionRuleModelCatalogue models)
    {
        var option = models.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Model, model.Trim(), StringComparison.OrdinalIgnoreCase));
        if (option is null) return $"sets model {model}, which the model catalogue does not know.";
        if (!string.Equals(option.Cli, models.Cli, StringComparison.OrdinalIgnoreCase))
            return $"sets model {model}, which the configured decision CLI {models.Cli} cannot serve.";
        return option.Trusted ? null
            : $"sets model {model}, which is below the trusted correctness floor.";
    }

    private static string Behavior(DecisionRuleV1 rule)
        => rule.MissingDataBehavior == "hold" ? "Hold" : "Ask";

    private static string ThresholdState(DecisionRuleV1 rule, string value)
    {
        if (rule.WarningThreshold is null) return "observed";
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var observed)
               && decimal.TryParse(rule.WarningThreshold, NumberStyles.Number,
                   CultureInfo.InvariantCulture, out var threshold)
            ? observed >= threshold ? "warning" : "below threshold" : "unknown";
    }
}
