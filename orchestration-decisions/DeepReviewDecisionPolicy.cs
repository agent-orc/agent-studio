using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Rollout of the deep-review step. <c>shadow</c> records every decision as a
/// receipt and never dispatches the review; <c>active</c> also freezes the
/// review command into the plan; <c>off</c> records no decision.
/// </summary>
public static class DeepReviewModes
{
    public const string Off = "off";
    public const string Shadow = "shadow";
    public const string Active = "active";

    /// <summary>Unknown or empty values fall back to <see cref="Shadow"/>, the observe-first default.</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Off => Off,
        Active => Active,
        _ => Shadow,
    };
}

public static class DeepReviewActions
{
    public const string Run = "run";
    public const string Skip = "skip";
    /// <summary>A person must decide: the hard floor demands the review and quota cannot serve it.</summary>
    public const string Ask = "ask";
}

/// <summary>The provider quota state for the deep-review route, as seen at the decision.</summary>
public static class DeepReviewQuotaStates
{
    public const string Comfortable = "comfortable";
    public const string NearCap = "near-cap";
    public const string Capped = "capped";
    public const string Unknown = "unknown";
}

/// <summary>Token Economy risk class of the card, derived from its correctness floor.</summary>
public static class DeepReviewRiskClasses
{
    /// <summary>A hard floor applies (P0, fencing, leases, security, data loss, destructive migration, subtle state machine).</summary>
    public const string Critical = "critical";
    /// <summary>At least Sol/medium (public protocol, persistent-state migration, three or more runtime subsystems).</summary>
    public const string Elevated = "elevated";
    public const string Standard = "standard";
}

/// <summary>One delivered deep review of the card: the delivery it graded and its blocking findings.</summary>
public sealed record DeepReviewRoundRecord(
    string ReviewAttemptId,
    string ResultSha,
    string Status,
    IReadOnlyList<string> BlockFingerprints);

/// <summary>
/// Frozen facts for one deep-review decision. The host collects them; the
/// policy reads no file, clock or configuration.
/// </summary>
public sealed record DeepReviewFacts(
    string Mode,
    string CardKey,
    string? ResultSha,
    string RiskClass,
    string? RiskReason,
    IReadOnlyList<string>? ChangedPaths,
    int? ChangedLines,
    string? ProducerModel,
    string? ProducerThinkingLevel,
    IReadOnlyList<DeepReviewRoundRecord> PriorRounds,
    int DeliveredReviewRounds,
    int PriorBlockingReviewRounds,
    int RedGates,
    string Quota,
    string? QuotaReason,
    string Model,
    IReadOnlyList<DecisionRuleV1>? Rules = null,
    string RuleVersion = DeepReviewDecisionPolicy.PlatformRuleVersion,
    int MaximumRounds = DeepReviewDecisionPolicy.DefaultMaximumRounds);

/// <summary>A ground the receipt shows. <c>Weight</c> is <c>critical</c>, <c>elevated</c> or <c>info</c>.</summary>
public sealed record DeepReviewGround(string Id, string Value, string Source, string Weight);

/// <summary>
/// The receipt of one deep-review decision. <see cref="Dispatch"/> is true only
/// for a <c>run</c> in <c>active</c> mode; a shadow <c>run</c> is recorded only.
/// </summary>
public sealed record DeepReviewDecision(
    string Action,
    string Mode,
    bool Dispatch,
    string? Model,
    string? ThinkingLevel,
    string Reason,
    IReadOnlyList<DeepReviewGround> Grounds,
    IReadOnlyList<string> TouchedAreas,
    string PolicyVersion,
    string RuleVersion);

/// <summary>A deep-review finding that blocked again after its previous fix round.</summary>
public sealed record DeepReviewRepeatedBlock(string Fingerprint, string Finding, int ConsecutiveRounds);

/// <summary>
/// Decides per delivery whether a deep review runs, and at which model and
/// reasoning level (Token Economy routing policy: Sol/medium by default,
/// Sol/xhigh only at a hard floor, never because quota is available). It also
/// identifies repeated deep-review blocks so they escalate to a person.
/// </summary>
public static partial class DeepReviewDecisionPolicy
{
    public const string PolicyVersion = "deep-review.v1";
    public const string PlatformRuleVersion = "platform.v1";
    public const string AspectId = "deep-review";
    public const int DefaultMaximumRounds = 3;
    public const int RepeatedBlockRounds = 2;
    public const int LargeDiffLines = 500;
    public const int LargeDiffFiles = 25;
    public const int WeakProducerDiffLines = 150;
    public const string DefaultThinkingLevel = "medium";
    public const string HardFloorThinkingLevel = "xhigh";

    private static readonly (string Area, Regex Pattern)[] Areas =
    [
        ("security", SecurityPath()),
        ("fencing", FencingPath()),
        ("migration", MigrationPath()),
        ("concurrency", ConcurrencyPath()),
    ];

    /// <summary>The risk areas a changed path set touches, in a fixed order.</summary>
    public static IReadOnlyList<string> TouchedAreas(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return [];
        return Areas.Where(area => paths.Any(path => area.Pattern.IsMatch(path.Replace('\\', '/'))))
            .Select(area => area.Area).ToArray();
    }

    public static DeepReviewDecision Decide(DeepReviewFacts facts)
    {
        var mode = DeepReviewModes.Normalize(facts.Mode);
        var touched = TouchedAreas(facts.ChangedPaths);
        var grounds = new List<DeepReviewGround>();
        var priorRounds = facts.PriorRounds ?? [];
        var maximumRounds = Math.Clamp(facts.MaximumRounds, 1, 10);

        if (facts.RiskClass == DeepReviewRiskClasses.Critical)
            grounds.Add(new("risk.class", facts.RiskReason ?? "hard floor", "token-economy", "critical"));
        else if (facts.RiskClass == DeepReviewRiskClasses.Elevated)
            grounds.Add(new("risk.class", facts.RiskReason ?? "elevated floor", "token-economy", "elevated"));
        else
            grounds.Add(new("risk.class", DeepReviewRiskClasses.Standard, "token-economy", "info"));
        foreach (var area in touched)
            grounds.Add(new("diff.area." + area, "touched", "changed-paths", "critical"));

        var files = facts.ChangedPaths?.Count;
        var lines = facts.ChangedLines;
        var large = lines >= LargeDiffLines || files >= LargeDiffFiles;
        grounds.Add(new("diff.size", $"{Count(files)} files, {Count(lines)} lines", "git-diff",
            large ? "elevated" : "info"));

        var weakProducer = IsBelowDefaultRoute(facts.ProducerModel, facts.ProducerThinkingLevel);
        grounds.Add(new("producer.route",
            $"{facts.ProducerModel ?? "unknown"}/{facts.ProducerThinkingLevel ?? "unknown"}", "card",
            weakProducer && lines >= WeakProducerDiffLines ? "elevated" : "info"));

        var priorDeepBlock = priorRounds.Any(round => IsBlocking(round.Status));
        grounds.Add(new("deep-review.rounds", $"{priorRounds.Count} of {maximumRounds}", "deep-review-ledger",
            priorDeepBlock ? "elevated" : "info"));
        grounds.Add(new("review.blocking-rounds",
            $"{facts.PriorBlockingReviewRounds} of {facts.DeliveredReviewRounds} delivered", "review-round-budget",
            facts.PriorBlockingReviewRounds >= 2 ? "elevated" : "info"));
        grounds.Add(new("gate.red", facts.RedGates.ToString(CultureInfo.InvariantCulture), "review-round-budget",
            facts.RedGates > 0 ? "elevated" : "info"));
        grounds.Add(new("quota.state", facts.Quota + (facts.QuotaReason is null ? string.Empty : $" ({facts.QuotaReason})"),
            "quota-cache", "info"));

        var ruleModel = (string?)null;
        foreach (var rule in (facts.Rules ?? []).Where(rule => rule.Trigger == DecisionRuleTriggers.DeepReview).Take(16))
        {
            var value = RuleFact(rule.FactId, facts, priorRounds.Count);
            var fired = value is not null && rule.WarningThreshold is { } threshold
                && decimal.TryParse(threshold, NumberStyles.Number, CultureInfo.InvariantCulture, out var limit)
                && value.Value >= limit;
            grounds.Add(new("rule." + rule.Id,
                value is null ? "missing" : $"{value.Value.ToString(CultureInfo.InvariantCulture)} (threshold {rule.WarningThreshold})",
                "rule-version:" + facts.RuleVersion, fired ? "elevated" : "info"));
            if (fired && ruleModel is null && !string.IsNullOrWhiteSpace(rule.Model)) ruleModel = rule.Model.Trim();
        }

        var critical = grounds.Where(ground => ground.Weight == "critical").ToArray();
        var elevated = grounds.Where(ground => ground.Weight == "elevated").ToArray();

        DeepReviewDecision Result(string action, string reason, string? level = null)
        {
            var run = action == DeepReviewActions.Run;
            return new(action, mode, run && mode == DeepReviewModes.Active,
                run ? ruleModel ?? facts.Model : null, run ? level : null, reason, grounds, touched,
                PolicyVersion, facts.RuleVersion);
        }

        if (mode == DeepReviewModes.Off)
            return Result(DeepReviewActions.Skip, "deep-review-off: the deep-review step is switched off.");
        if (string.IsNullOrWhiteSpace(facts.ResultSha))
            return Result(DeepReviewActions.Skip, "no-result-sha: a deep review needs an exact delivery.");
        if (priorRounds.Any(round => string.Equals(round.ResultSha, facts.ResultSha, StringComparison.OrdinalIgnoreCase)))
            return Result(DeepReviewActions.Skip, "delivery-already-deep-reviewed: one deep review per delivery.");
        if (priorRounds.Count >= maximumRounds)
            return Result(DeepReviewActions.Skip,
                $"deep-review-round-budget-spent: {priorRounds.Count} of {maximumRounds} deep reviews used on this card.");
        if (critical.Length == 0 && elevated.Length == 0)
            return Result(DeepReviewActions.Skip,
                "no-deep-review-ground: no hard floor, risk area, size, producer, history or rule ground; quota headroom alone is never a ground.");

        var level = critical.Length > 0 ? HardFloorThinkingLevel : DefaultThinkingLevel;
        var because = string.Join(", ", (critical.Length > 0 ? critical : elevated).Select(ground => ground.Id));
        if (facts.Quota == DeepReviewQuotaStates.Capped)
        {
            return critical.Length > 0
                ? Result(DeepReviewActions.Ask,
                    $"quota-capped-at-hard-floor: {because} require a deep review at {level}, the route is capped, and quota never lowers a hard floor.")
                : Result(DeepReviewActions.Skip, $"quota-capped: {because} would justify a deep review, but the route is capped.");
        }
        if (facts.Quota == DeepReviewQuotaStates.NearCap && critical.Length == 0 && elevated.Length < 2)
            return Result(DeepReviewActions.Skip,
                $"quota-near-cap: a single elevated ground ({because}) does not justify a deep review near the quota cap.");
        return Result(DeepReviewActions.Run,
            critical.Length > 0
                ? $"hard-floor: {because} require a deep review at {level}."
                : $"elevated: {because} justify a deep review at the default {level} level.",
            level);
    }

    /// <summary>
    /// Fingerprints of the blocking deep-review findings. A finding is the
    /// same finding when it cites the same files: the reviewer's wording
    /// changes between rounds, the cited location does not. A block without a
    /// file citation falls back to its normalized summary.
    /// </summary>
    public static IReadOnlyList<(string Fingerprint, string Finding)> BlockFingerprints(
        IEnumerable<ReviewFollowUpFinding> findings)
        => findings.Where(item => string.Equals(item.Aspect, AspectId, StringComparison.OrdinalIgnoreCase)
                && ReviewGradingPolicy.IsBlockingToken(item.Status)
                && !ReviewRoundBudgetPolicy.IsUncitedBlock(item.Classification))
            .Select(item =>
            {
                var cited = FileReference().Matches($"{item.Finding}\n{item.EvidenceChecked}")
                    .Select(match => match.Value.Trim().TrimStart('`', '\'', '(').ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var key = cited.Length > 0
                    ? "files:" + string.Join("|", cited)
                    : "summary:" + Whitespace().Replace((item.Summary ?? string.Empty).Trim().ToLowerInvariant(), " ");
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AspectId + "\n" + key)))
                    .ToLowerInvariant();
                return (fingerprint, $"{AspectId}: {item.Summary?.Trim()}");
            })
            .DistinctBy(item => item.fingerprint)
            .ToArray();

    /// <summary>
    /// The repeated block that must escalate instead of opening another fix
    /// round: a current blocking deep-review finding that the immediately
    /// preceding deep-review rounds already blocked on. Null when none repeats.
    /// </summary>
    public static DeepReviewRepeatedBlock? RepeatedBlock(
        IEnumerable<ReviewFollowUpFinding> current,
        IReadOnlyList<DeepReviewRoundRecord> priorRoundsOldestFirst,
        string currentAttemptId,
        int repeatedRounds = RepeatedBlockRounds)
    {
        var limit = Math.Clamp(repeatedRounds, 2, 10);
        var prior = priorRoundsOldestFirst
            .Where(round => !string.Equals(round.ReviewAttemptId, currentAttemptId, StringComparison.Ordinal))
            .Reverse().ToArray();
        DeepReviewRepeatedBlock? selected = null;
        foreach (var (fingerprint, finding) in BlockFingerprints(current))
        {
            var rounds = 1 + prior.TakeWhile(round => round.BlockFingerprints.Contains(fingerprint, StringComparer.Ordinal))
                .Count();
            if (rounds >= limit && (selected is null || rounds > selected.ConsecutiveRounds))
                selected = new(fingerprint, finding, rounds);
        }
        return selected;
    }

    /// <summary>
    /// True when the producing route is below the Sol/medium default: a
    /// low or minimal reasoning level, or a Luna, Terra, Mini or Haiku class
    /// model. An unknown route is not called weak.
    /// </summary>
    public static bool IsBelowDefaultRoute(string? model, string? thinkingLevel)
    {
        var level = thinkingLevel?.Trim().ToLowerInvariant();
        if (level is "minimal" or "low" or "none") return true;
        var id = model?.Trim().ToLowerInvariant() ?? string.Empty;
        return id.Contains("luna", StringComparison.Ordinal) || id.Contains("terra", StringComparison.Ordinal)
            || id.Contains("mini", StringComparison.Ordinal) || id.Contains("haiku", StringComparison.Ordinal);
    }

    private static decimal? RuleFact(string factId, DeepReviewFacts facts, int roundsUsed) => factId switch
    {
        "diff.changed-lines" => facts.ChangedLines,
        "diff.changed-files" => facts.ChangedPaths?.Count,
        "review.blocking-rounds" => facts.PriorBlockingReviewRounds,
        "review.delivered-rounds" => facts.DeliveredReviewRounds,
        "gate.red-count" => facts.RedGates,
        "deep-review.rounds-used" => roundsUsed,
        _ => null,
    };

    private static bool IsBlocking(string? status) => ReviewGradingPolicy.IsBlockingToken(status);

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    [GeneratedRegex(@"(^|/)(auth|security|secrets?|credentials?|permissions?|identity|tokens?)(/|\.|$)|(Auth|Security|Secret|Credential|Permission|Token|Principal|Scope)[A-Za-z]*\.(cs|ts)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityPath();

    [GeneratedRegex(@"(Fenc|Lease|Authority|Epoch|Idempoten)[A-Za-z]*\.(cs|ts)$|(^|/)leases?/", RegexOptions.IgnoreCase)]
    private static partial Regex FencingPath();

    [GeneratedRegex(@"(^|/)migrations?/|Migration[A-Za-z]*\.(cs|ts|sql)$|\.sql$|(^|/)schemas?/", RegexOptions.IgnoreCase)]
    private static partial Regex MigrationPath();

    [GeneratedRegex(@"(Lock|Concurren|Semaphore|StateMachine|Journal|Settlement|Recovery|Reconcil)[A-Za-z]*\.(cs|ts)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConcurrencyPath();

    [GeneratedRegex(@"(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+\.[A-Za-z0-9]+|\b[A-Za-z0-9_-]+\.(?:cs|ts|tsx|js|scss|css|html|md|json|ya?ml|xml|sh|sql)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileReference();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
