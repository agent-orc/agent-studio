using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.OrchestrationDecisions;

public sealed record DecisionModelResponse(
    bool Valid,
    string? Failure,
    IReadOnlyList<DecisionChoiceV1> Chosen,
    IReadOnlyList<DecisionAlternativeConsideredV1> Alternatives,
    double? Confidence,
    string? Reasoning,
    IReadOnlyList<string> CitedFacts,
    string? Prediction,
    string? Uncertainty);

/// <summary>What the engine observed for one shadow decision occurrence.</summary>
public sealed record DecisionReceiptFacts(
    DecisionGroundsV1 Grounds,
    DecisionChoiceV1 Deterministic,
    string DeterministicReason,
    OrchestratorDecisionJobDto? Job,
    int Attempts,
    bool AlternateUsed,
    string? UnavailableReason,
    string Mode = DecisionReceiptPolicy.ShadowMode);

/// <summary>
/// Prompt, response validation and receipt construction for an AGT-W73
/// decision step. A model choice outside the closed union or outside the
/// available candidates of this occurrence is recorded as rejected, and the
/// degraded decision is ask(reason=decision-unavailable). OD-S1 runs in
/// shadow: the receipt records the choice and never dispatches it.
/// </summary>
public static class DecisionReceiptPolicy
{
    public const string SchemaVersion = "orchestrator-decision-receipt.v1";
    public const string PromptVersion = "od-s1.step-transition.v1";
    public const string ShadowMode = "shadow";
    public const string ActiveMode = "active";
    public const string ShadowDispatch = "not-dispatched:shadow";
    public const string ActiveDispatch = "submitted:authority-adapter";
    public const string ActiveHold = "held:operator-ai";
    public const string DegradedTarget = "decision-unavailable";
    public const int MaxReasoningChars = 2_000;
    public const int MaxResponseChars = 16_384;
    public const string ReceiptRoute = "/api/orchestration/decision-receipts/";

    public static string ReceiptId(string occurrenceId, string groundsDigest)
        => "odr_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            occurrenceId + "\n" + groundsDigest))).ToLowerInvariant()[..32];

    public static string EvidenceRef(string receiptId) => ReceiptRoute + receiptId;

    public static string RenderPrompt(DecisionGroundsV1 grounds)
    {
        var maxActions = grounds.Scope.Kind == "card" ? "exactly one" : "zero to three";
        return $$"""
            You decide the next typed action of one orchestrator transition in Agent Studio.
            Read only the frozen decision grounds below. Do not run tools, read files or query sessions.
            Choose {{maxActions}} action from the alternatives whose "available" is true.
            Signals are questions to weigh, not commands. Missing or unpriced cost raises uncertainty;
            it is never evidence that a run was cheap. Cite the fact ids your reasoning relies on.

            Answer with one JSON object and nothing else:
            {"chosenActions":[{"action":"<one of {{string.Join('|', DecisionActionKinds.All)}}>","target":"<target of that alternative>"}],
             "alternatives":[{"action":"<rejected action>","rejectionReason":"<why>"}],
             "confidence":<0..1>,
             "reasoning":"<at most 120 words>",
             "citedFacts":["<fact id>"],
             "prediction":"<expected next receipt, chance of completion, incremental spend and time>",
             "uncertainty":"<missing evidence or none>"}

            Decision grounds ({{grounds.SchemaVersion}}, digest {{grounds.ContentDigest}}):
            {{JsonSerializer.Serialize(grounds, DecisionGroundsPolicy.Json)}}
            """;
    }

    public static DecisionModelResponse Parse(string? text, DecisionGroundsV1 grounds)
    {
        static DecisionModelResponse Invalid(string failure, IReadOnlyList<DecisionChoiceV1>? chosen = null)
            => new(false, failure, chosen ?? [], [], null, null, [], null, null);
        if (string.IsNullOrWhiteSpace(text)) return Invalid("empty-response");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return Invalid("no-json-object");
        JsonDocument document;
        try { document = JsonDocument.Parse(text[start..(end + 1)]); }
        catch (JsonException) { return Invalid("invalid-json"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid("invalid-json");
            var chosen = new List<DecisionChoiceV1>();
            if (root.TryGetProperty("chosenActions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                foreach (var item in actions.EnumerateArray())
                    chosen.Add(new(Text(item, "action")?.Trim().ToLowerInvariant() ?? string.Empty,
                        Text(item, "target")?.Trim() ?? string.Empty, string.Empty));
            var card = grounds.Scope.Kind == "card";
            if (card ? chosen.Count != 1 : chosen.Count > 3)
                return Invalid("chosen-action-count", Rejected(chosen, "chosen-action-count"));
            foreach (var choice in chosen)
            {
                if (!DecisionActionKinds.All.Contains(choice.Action))
                    return Invalid("action-outside-union", Rejected(chosen, "action-outside-union"));
                var offered = grounds.Alternatives.Where(item => item.Action == choice.Action).ToArray();
                var match = offered.FirstOrDefault(item => string.Equals(item.Target, choice.Target,
                        StringComparison.OrdinalIgnoreCase))
                    ?? (string.IsNullOrEmpty(choice.Target) && offered.Length == 1 ? offered[0] : null);
                if (match is null)
                    return Invalid("candidate-not-offered", Rejected(chosen, "candidate-not-offered"));
                if (!match.Available)
                    return Invalid("forbidden-action", Rejected(chosen, "forbidden-action:" + match.RejectionReason));
            }
            double? confidence = root.TryGetProperty("confidence", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : null;
            if (confidence is not (>= 0 and <= 1))
                return Invalid("confidence-out-of-range", Rejected(chosen, "confidence-out-of-range"));
            var reasoning = Text(root, "reasoning")?.Trim();
            if (string.IsNullOrWhiteSpace(reasoning))
                return Invalid("missing-reasoning", Rejected(chosen, "missing-reasoning"));
            var factIds = grounds.Facts.Select(fact => fact.Id).ToHashSet(StringComparer.Ordinal);
            var cited = root.TryGetProperty("citedFacts", out var facts) && facts.ValueKind == JsonValueKind.Array
                ? facts.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).Where(factIds.Contains).Distinct().ToArray()
                : [];
            if (cited.Length == 0)
                return Invalid("insufficient-evidence", Rejected(chosen, "insufficient-evidence"));
            var considered = root.TryGetProperty("alternatives", out var alternatives)
                && alternatives.ValueKind == JsonValueKind.Array
                ? alternatives.EnumerateArray().Take(DecisionGroundsPolicy.MaxAlternatives)
                    .Select(item => new DecisionAlternativeConsideredV1(
                        Text(item, "action")?.Trim().ToLowerInvariant() ?? string.Empty,
                        Bound(Text(item, "rejectionReason"), 400) ?? string.Empty)).ToArray()
                : [];
            return new(true, null, chosen.Select(choice => choice with { Outcome = ShadowDispatch }).ToArray(),
                considered, confidence, Bound(reasoning, MaxReasoningChars), cited,
                Bound(Text(root, "prediction"), 1_000), Bound(Text(root, "uncertainty"), 1_000));
        }
    }

    public static OrchestratorDecisionReceiptV1 Build(DecisionReceiptFacts facts)
    {
        var grounds = facts.Grounds;
        var job = facts.Job;
        var result = job?.Result;
        var unavailable = grounds.Pack.Incomplete ? DecisionCallStatuses.IncompletePack
            : facts.UnavailableReason is not null ? DecisionCallStatuses.Unavailable
            : job?.Status == DecisionJobStatuses.CapExhausted ? DecisionCallStatuses.CapExhausted
            : result is null || !result.Success ? DecisionCallStatuses.Unavailable
            : null;
        var response = unavailable is null ? Parse(result!.ResponseText, grounds) : null;
        var callStatus = unavailable ?? (response!.Valid ? DecisionCallStatuses.Completed
            : DecisionCallStatuses.InvalidResponse);
        var dispatch = facts.Mode == ActiveMode
            ? response is { Valid: true } && response.Chosen.Count == 1
                && response.Chosen[0].Action != DecisionActionKinds.Ask
                ? ActiveDispatch : ActiveHold
            : ShadowDispatch;
        var degraded = new DecisionChoiceV1(DecisionActionKinds.Ask, "operator:ai", dispatch);
        IReadOnlyList<DecisionChoiceV1> chosen = response is { Valid: true }
            ? response.Chosen.Select(choice => choice with { Outcome = dispatch }).ToArray()
            : [.. response?.Chosen ?? [], degraded];
        var matches = response is { Valid: true } && response.Chosen.Count == 1
            && response.Chosen[0].Action == facts.Deterministic.Action
            && string.Equals(response.Chosen[0].Target, facts.Deterministic.Target, StringComparison.OrdinalIgnoreCase);
        var tokens = result?.Tokens ?? DecisionTokenUsageV1.Zero;
        var receiptId = ReceiptId(grounds.OccurrenceId, grounds.ContentDigest);
        var call = new DecisionModelCallV1(
            callStatus,
            grounds.ModelRoute.Custody,
            job?.JobId,
            result?.HostId ?? job?.RunnerId,
            result?.Provider ?? job?.Cli,
            result?.Model ?? job?.Model,
            result?.ThinkingLevel ?? job?.ThinkingLevel,
            facts.Attempts,
            facts.AlternateUsed,
            tokens,
            result?.Price ?? new DecisionPriceV1(DecisionPriceStatuses.NoUsage, null),
            result?.ProviderCallIds ?? [],
            result?.ExcludedDuplicateFrames ?? 0,
            tokens.UncachedInput + tokens.CachedInput + tokens.CacheWrite > DecisionGroundsPolicy.MaxCallInputTokens
                || tokens.Output > DecisionGroundsPolicy.MaxCallOutputTokens,
            Bound(facts.UnavailableReason ?? result?.Error ?? job?.Error ?? response?.Failure
                ?? (grounds.Pack.Incomplete ? "incomplete-pack" : null), 400));
        var reasoning = response is { Valid: true } ? response.Reasoning
            : $"Degraded mode: {response?.Failure ?? facts.UnavailableReason ?? job?.Error ?? unavailable}. "
              + "The transition rests in ask(reason=decision-unavailable).";
        return new OrchestratorDecisionReceiptV1(
            SchemaVersion,
            receiptId,
            grounds.OccurrenceId,
            facts.Mode,
            grounds.Trigger,
            grounds.Scope,
            grounds.PriorStep,
            grounds.ProposedNextStep,
            grounds.ContentDigest,
            grounds.Facts.Select(fact => new DecisionSignalReadV1(fact.Id, fact.Value, fact.ObservedAt,
                fact.Reliability)).ToArray(),
            grounds.Alternatives.Where(item => item.Available).ToArray(),
            grounds.Alternatives.Where(item => !item.Available).ToArray(),
            chosen,
            response?.Alternatives ?? [],
            response is { Valid: true } ? response.Confidence : null,
            reasoning,
            response?.CitedFacts ?? [],
            response?.Prediction,
            response?.Uncertainty,
            $"{facts.Deterministic.Action}({facts.Deterministic.Target})",
            facts.DeterministicReason,
            matches,
            grounds.PolicyVersion,
            PromptVersion,
            grounds.ModelRoute,
            call,
            dispatch,
            EvidenceRef(receiptId),
            EvidenceRef(receiptId) + "/outcome",
            result?.StartedAt ?? grounds.Trigger.ObservedAt,
            result?.EndedAt ?? grounds.Trigger.ObservedAt,
            grounds,
            Bound(result?.ResponseText, MaxResponseChars));
    }

    private static IReadOnlyList<DecisionChoiceV1> Rejected(IReadOnlyList<DecisionChoiceV1> chosen, string reason)
        => chosen.Select(choice => choice with { Outcome = "rejected:" + reason }).ToArray();

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Bound(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max] + $" [truncated {value.Length - max} chars]";
}
