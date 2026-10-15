using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Freezes host-collected facts into the canonical <see cref="DecisionGroundsV1"/>
/// pack (AGT-W73). The pack follows the bounded WatcherEvidencePack pattern:
/// fixed row caps, truncation markers, at most 32 KiB, and an input-token
/// budget that leaves room for the prompt inside the 8,000-token call cap.
/// Required identities, blockers and candidate actions win truncation; if they
/// cannot fit, the pack is marked incomplete and the step must ask a human.
/// The same input always yields the same pack and content digest.
/// </summary>
public static class DecisionGroundsPolicy
{
    public const string SchemaVersion = "decision-grounds.v1";
    public const int MaxPackBytes = 32 * 1024;
    public const int MaxCallInputTokens = 8_000;
    public const int MaxCallOutputTokens = 1_000;
    public const int PromptReserveTokens = 1_500;
    public const int MaxFacts = 48;
    public const int MaxAlternatives = 16;
    public const int MaxPriorDecisions = 3;
    public const int MaxRounds = 12;
    public const int MaxOmissions = 24;
    public const int MaxValueChars = 512;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int PackTokenBudget => MaxCallInputTokens - PromptReserveTokens;

    /// <summary>Four UTF-8 bytes per token, the estimator used for bounded model context.</summary>
    public static int EstimateTokens(int bytes) => (bytes + 3) / 4;

    public static DecisionGroundsV1 Freeze(DecisionGroundsInputV1 input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.OccurrenceId);
        var markers = new List<string>();
        var omissions = input.Omissions.ToList();

        var facts = input.Facts
            .Select((fact, index) => (fact, index))
            .OrderBy(item => item.fact.Required ? 0 : 1).ThenBy(item => item.index)
            .Select(item => BoundValue(item.fact, markers))
            .ToList();
        if (facts.Count > MaxFacts)
        {
            var dropped = facts.Skip(MaxFacts).Where(fact => !fact.Required).ToArray();
            facts = facts.Except(dropped).ToList();
            markers.Add($"facts: {dropped.Length} lower-priority rows omitted by the {MaxFacts}-row cap");
            omissions.Add(new("facts", $"{dropped.Length} rows over the row cap"));
        }

        var alternatives = input.Alternatives
            .OrderBy(item => item.Available ? 0 : 1)
            .Take(MaxAlternatives).ToList();
        if (input.Alternatives.Count > MaxAlternatives)
            markers.Add($"alternatives: {input.Alternatives.Count - MaxAlternatives} unavailable candidates omitted");

        var prior = input.PriorDecisions.OrderByDescending(item => item.DecidedAt)
            .ThenBy(item => item.ReceiptId, StringComparer.Ordinal)
            .Take(MaxPriorDecisions).OrderBy(item => item.DecidedAt).ToList();
        if (input.PriorDecisions.Count > MaxPriorDecisions)
        {
            var older = input.PriorDecisions.Count - MaxPriorDecisions;
            markers.Add($"priorDecisions: {older} older decisions counted, not included");
            omissions.Add(new("priorDecisions", $"{older} older decisions outside the last {MaxPriorDecisions}"));
        }

        var rounds = input.RunLedger.Rounds.TakeLast(MaxRounds).ToList();
        if (input.RunLedger.Rounds.Count > MaxRounds)
            markers.Add($"runLedger.rounds: {input.RunLedger.Rounds.Count - MaxRounds} older rounds summarized in totals");

        var budgetBytes = Math.Min(MaxPackBytes, PackTokenBudget * 4);
        DecisionGroundsV1 Compose() => Build(input, facts, alternatives, prior,
            input.RunLedger with { Rounds = rounds },
            omissions.Take(MaxOmissions).ToList(), markers, null);

        var bytes = BodyBytes(Compose());
        while (bytes > budgetBytes)
        {
            if (rounds.Count > 1)
            {
                rounds.RemoveAt(0);
                markers.Add("runLedger.rounds: oldest round dropped for the token budget");
            }
            else if (facts.FindLastIndex(fact => !fact.Required) is var optional and >= 0)
            {
                markers.Add($"facts.{facts[optional].Id}: dropped for the token budget");
                omissions.Add(new($"facts.{facts[optional].Id}", "token budget"));
                facts.RemoveAt(optional);
            }
            else if (prior.Count > 0)
            {
                prior.RemoveAt(0);
                markers.Add("priorDecisions: oldest decision dropped for the token budget");
            }
            else if (alternatives.FindLastIndex(item => !item.Available) is var unavailable and >= 0)
            {
                alternatives.RemoveAt(unavailable);
                markers.Add("alternatives: unavailable candidate dropped for the token budget");
            }
            else break;
            bytes = BodyBytes(Compose());
        }

        var incomplete = bytes > budgetBytes
            || !alternatives.Any(item => item.Available)
            || string.IsNullOrWhiteSpace(input.Scope.Kind);
        if (incomplete) markers.Add("pack: required grounds do not fit or no available candidate exists");
        var pack = new GroundsPackV1(bytes, EstimateTokens(bytes), MaxPackBytes, PackTokenBudget,
            markers.Count > 0, incomplete, markers.ToArray());
        var unsigned = Build(input, facts, alternatives, prior, input.RunLedger with { Rounds = rounds },
            omissions.Take(MaxOmissions).ToList(), markers, pack);
        return unsigned with { ContentDigest = Digest(unsigned) };
    }

    /// <summary>SHA-256 over the canonical JSON with an empty digest field.</summary>
    public static string Digest(DecisionGroundsV1 grounds)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(grounds with { ContentDigest = string.Empty }, Json)))
            .ToLowerInvariant();

    public static bool IsIntact(DecisionGroundsV1 grounds)
        => string.Equals(Digest(grounds), grounds.ContentDigest, StringComparison.Ordinal);

    private static DecisionGroundsV1 Build(DecisionGroundsInputV1 input, IReadOnlyList<GroundsFactV1> facts,
        IReadOnlyList<GroundsAlternativeV1> alternatives, IReadOnlyList<GroundsPriorDecisionV1> prior,
        CardRunLedgerV1 ledger, IReadOnlyList<GroundsOmissionV1> omissions, IReadOnlyList<string> markers,
        GroundsPackV1? pack)
        => new(SchemaVersion, input.OccurrenceId, input.Trigger, input.Scope, input.PriorStep,
            input.ProposedNextStep, input.PolicyVersion, input.ModelRoute, facts.ToArray(),
            alternatives.ToArray(), prior.ToArray(), ledger, input.Progress, omissions.ToArray(),
            pack ?? new GroundsPackV1(0, 0, MaxPackBytes, PackTokenBudget, markers.Count > 0, false, markers.ToArray()),
            string.Empty);

    private static int BodyBytes(DecisionGroundsV1 grounds)
        => JsonSerializer.SerializeToUtf8Bytes(grounds, Json).Length;

    private static GroundsFactV1 BoundValue(GroundsFactV1 fact, List<string> markers)
    {
        if (fact.Value.Length <= MaxValueChars) return fact;
        markers.Add($"facts.{fact.Id}.value: {fact.Value.Length - MaxValueChars} characters truncated");
        return fact with
        {
            Value = fact.Value[..MaxValueChars] + $" [truncated {fact.Value.Length - MaxValueChars} chars]",
            Coverage = "truncated",
        };
    }

    /// <summary>
    /// Candidates of the coding-completion transition (the first OD-S1 profile).
    /// The deterministic library decision is always available; actions that
    /// need a later slice or human approval stay visible as floor-rejected.
    /// </summary>
    public static IReadOnlyList<GroundsAlternativeV1> CodingCompletionAlternatives(
        TransitionDecision deterministic, bool hasSettledEnvelope)
    {
        var chosen = CodingCompletionChoice(deterministic);
        GroundsAlternativeV1 Candidate(string action, string target, string reversibility,
            string approval, bool available, string? rejection)
        {
            var open = available || action == chosen.Action && target == chosen.Target;
            return new(action, target, reversibility, approval, open, open ? null : rejection);
        }
        return
        [
            Candidate(DecisionActionKinds.Prepare, "review-subject", "reversible", "A0",
                hasSettledEnvelope, "No settled immutable result envelope exists for this attempt."),
            Candidate(DecisionActionKinds.Retry, "card:ready", "reversible", "A0", true, null),
            Candidate(DecisionActionKinds.Ask, "operator:human-review", "reversible", "A0", true, null),
            Candidate(DecisionActionKinds.Defer, "card:bounded-pause", "reversible", "A0", true, null),
            Candidate(DecisionActionKinds.Continue, "current-lane", "reversible", "A0", false,
                "The settled coding attempt must leave the coding step."),
            Candidate(DecisionActionKinds.Checkpoint, "verified-tree", "reversible", "A1", false,
                "Checkpoint dispatch is not yet an authorized action of this profile (OD-S2)."),
            Candidate(DecisionActionKinds.Adapt, "route-upgrade", "reversible", "A1", false,
                "No route change is authorized for the coding-completion profile."),
            Candidate(DecisionActionKinds.Halt, "card:park", "manual-restore", "A2", false,
                "Parking a card is an A2 proposal that needs human approval."),
        ];
    }

    /// <summary>The deterministic coding-completion decision as a union action and target.</summary>
    public static DecisionChoiceV1 CodingCompletionChoice(TransitionDecision deterministic)
    {
        var action = Union(deterministic.Action);
        var target = action switch
        {
            DecisionActionKinds.Prepare => "review-subject",
            DecisionActionKinds.Retry => "card:ready",
            DecisionActionKinds.Ask => "operator:human-review",
            DecisionActionKinds.Halt => "card:park",
            _ => "current-lane",
        };
        return new(action, target, "applied:deterministic");
    }

    public static string Union(TransitionAction action)
        => TransitionDecisionPolicy.Union(action).ToString().ToLowerInvariant();
}
