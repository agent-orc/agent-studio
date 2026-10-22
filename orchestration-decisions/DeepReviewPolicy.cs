using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Frozen delivery facts used to decide whether a second semantic review is justified.</summary>
public sealed record DeepReviewGrounds(
    string TaskId,
    string ResultSha,
    string RiskClass,
    IReadOnlyList<string> ChangedPaths,
    int ChangedLines,
    string? ProducingModel,
    string? ProducingEffort,
    int PreviousBlocks,
    int DeliveredRounds,
    int MaximumRounds,
    int GateFailures,
    string QuotaState);

/// <summary>A stable per-delivery decision, carried with the immutable review plan.</summary>
public sealed record DeepReviewDecision(
    string ReceiptId,
    string Mode,
    bool Selected,
    bool Dispatched,
    string Model,
    string ThinkingLevel,
    IReadOnlyList<string> Grounds,
    string? RuleVersionId = null);

/// <summary>
/// Pure Token Economy review selection. Quota can defer discretionary spend,
/// but it cannot lower the reasoning floor of a correctness-critical delivery.
/// </summary>
public static class DeepReviewPolicy
{
    public const int MaximumDeepRounds = 3;
    public const string Model = "gpt-6-sol";
    public const string Shadow = "shadow";
    public const string Active = "active";

    public static DeepReviewDecision Decide(DeepReviewGrounds facts, string mode,
        string? ruleVersionId = null, bool? ruleSelection = null, string? ruleModel = null,
        string? ruleThinkingLevel = null)
    {
        var grounds = new List<string>();
        var critical = facts.RiskClass.Equals("critical", StringComparison.OrdinalIgnoreCase)
            || facts.RiskClass.Equals("p0", StringComparison.OrdinalIgnoreCase);
        if (critical) grounds.Add("card-risk-critical");
        if (facts.ChangedPaths.Any(CriticalPath))
        {
            critical = true;
            grounds.Add("critical-touched-area");
        }
        if (facts.ChangedLines >= 500) grounds.Add("large-diff");
        if (facts.ChangedLines < 0) grounds.Add("diff-size-unavailable");
        if (facts.PreviousBlocks > 0) grounds.Add("previous-review-block");
        if (facts.GateFailures > 0) grounds.Add("gate-failure-history");
        if (critical && !string.Equals(facts.ProducingEffort, "xhigh", StringComparison.OrdinalIgnoreCase))
            grounds.Add("producer-below-critical-floor");
        if (critical && facts.ProducingModel is not null
            && !facts.ProducingModel.Contains("sol", StringComparison.OrdinalIgnoreCase)
            && !facts.ProducingModel.Contains("astra", StringComparison.OrdinalIgnoreCase))
            grounds.Add("producer-model-below-critical-floor");
        if (facts.DeliveredRounds >= Math.Min(MaximumDeepRounds, Math.Max(1, facts.MaximumRounds)))
            grounds.Add("deep-round-limit");
        if (facts.QuotaState is "capped" or "unavailable") grounds.Add("quota-limited");

        var selected = critical || facts.ChangedLines is < 0 or >= 500 || facts.PreviousBlocks > 0
            || facts.GateFailures > 0;
        if (ruleSelection is { } configured && !critical)
        {
            selected = configured;
            grounds.Add(configured ? "rule-selected" : "rule-declined");
        }
        if (!critical && facts.QuotaState is ("capped" or "unavailable")) selected = false;
        if (facts.DeliveredRounds >= Math.Min(MaximumDeepRounds, Math.Max(1, facts.MaximumRounds)))
            selected = false;
        var selectedMode = mode == Active ? Active : Shadow;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', JsonSerializer.Serialize(facts), selectedMode, ruleVersionId ?? "",
                ruleSelection?.ToString() ?? "", ruleModel ?? "", ruleThinkingLevel ?? ""))));
        var model = critical || string.IsNullOrWhiteSpace(ruleModel) ? Model : ruleModel;
        var thinking = critical ? "xhigh" : ruleThinkingLevel is "medium" or "high"
            ? ruleThinkingLevel : "medium";
        return new("deep-review-" + digest[..24], selectedMode, selected,
            selected && selectedMode == Active, model, thinking,
            grounds, ruleVersionId);
    }

    private static bool CriticalPath(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("security", StringComparison.Ordinal)
            || normalized.Contains("fencing", StringComparison.Ordinal)
            || normalized.Contains("migration", StringComparison.Ordinal)
            || normalized.Contains("concurrency", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, @"(?<!re)lease", RegexOptions.CultureInvariant)
            || normalized.Contains("orchestration-decisions/", StringComparison.Ordinal);
    }
}
