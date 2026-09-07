using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// Turns a persistent case into a ticket proposal. Drafting is deterministic:
/// the model economy of dossier section 5 forbids a model for anything the
/// server can already state, and a card draft built from an evidence pack is
/// exactly that.
/// </summary>
/// <remarks>
/// The output is a draft, never a card. Review mode owns the transition to
/// Ready, so <see cref="WatcherCardDraft.TargetState"/> is always the proposal
/// lane and the card carries the <c>watcher-proposal</c> tag.
/// </remarks>
public static class WatcherProposalDrafting
{
    /// <summary>
    /// Structural task type asked of the routing policy per detector class.
    /// Contradiction and drift describe a system that reports two different
    /// truths, which is a bug; repetition, silence, and hygiene describe work
    /// that is known and bounded.
    /// </summary>
    public static string TaskTypeFor(WatcherDetectorClass detectorClass) => detectorClass switch
    {
        WatcherDetectorClass.Contradiction => "bug",
        WatcherDetectorClass.Drift => "bug",
        WatcherDetectorClass.Silence => "bug",
        WatcherDetectorClass.Repetition => "feature",
        _ => "chore",
    };

    /// <summary>
    /// Builds the proposal. <paramref name="recommendation"/> comes from the
    /// routing policy registry; this function never invents a route, so the
    /// policy stays the single authority over model selection.
    /// </summary>
    public static WatcherProposal Draft(
        WatcherCase watcherCase,
        WatcherModelRecommendation recommendation,
        DateTime createdAt,
        string? openCardForFingerprint = null)
    {
        var draft = new WatcherCardDraft(
            Title: Title(watcherCase),
            PromptMarkdown: Prompt(watcherCase, recommendation),
            TaskType: TaskTypeFor(watcherCase.DetectorClass),
            TargetState: WatcherProposalConventions.ProposalLane,
            Tags:
            [
                WatcherProposalConventions.ProposalTag,
                WatcherProposalConventions.ClassTag(watcherCase.DetectorClass),
            ],
            RelatedCards: watcherCase.AffectedCards,
            Project: watcherCase.Project);

        return new WatcherProposal
        {
            ProposalId = $"wp-{watcherCase.CaseId}",
            CaseId = watcherCase.CaseId,
            CardDraft = draft,
            Recommendation = recommendation,
            CreatedAt = createdAt,
            EvidencePackDigest = watcherCase.EvidencePackDigest,
            DetectorRule = watcherCase.DetectorRule,
            Decision = WatcherProposalDecision.Pending,
            CommentOnCard = string.IsNullOrWhiteSpace(openCardForFingerprint) ? null : openCardForFingerprint,
        };
    }

    private static string Title(WatcherCase watcherCase)
    {
        var prefix = watcherCase.DetectorClass switch
        {
            WatcherDetectorClass.Repetition => "Recurring",
            WatcherDetectorClass.Contradiction => "Contradiction",
            WatcherDetectorClass.Silence => "Missing signal",
            WatcherDetectorClass.Drift => "Tool drift",
            _ => "Hygiene",
        };
        var summary = watcherCase.Summary.Length <= 110
            ? watcherCase.Summary
            : watcherCase.Summary[..110].TrimEnd() + "...";
        return $"{prefix}: {summary}";
    }

    private static string Prompt(WatcherCase watcherCase, WatcherModelRecommendation recommendation)
    {
        var body = new StringBuilder();

        body.AppendLine("## Context");
        body.AppendLine();
        body.AppendLine($"The orchestrator Watcher opened case `{watcherCase.CaseId}` from deterministic signals the Task Server already held. No model was used to detect it.");
        body.AppendLine();
        body.AppendLine($"- Detector class: `{watcherCase.DetectorClass.ToString().ToLowerInvariant()}`");
        body.AppendLine($"- Detector rule: {watcherCase.DetectorRule}");
        body.AppendLine($"- Fingerprint: `{watcherCase.Fingerprint}`");
        body.AppendLine($"- Evidence pack digest: `{watcherCase.EvidencePackDigest}`");
        body.AppendLine($"- First seen: {Stamp(watcherCase.FirstSeenAt)}");
        body.AppendLine($"- Last seen: {Stamp(watcherCase.LastSeenAt)}");
        body.AppendLine($"- Occurrences: {watcherCase.Occurrences} across {watcherCase.SweepCount} sweeps");
        if (!string.IsNullOrWhiteSpace(watcherCase.Project))
            body.AppendLine($"- Project: {watcherCase.Project}");
        if (watcherCase.AffectedCards.Count > 0)
            body.AppendLine($"- Affected cards: {string.Join(", ", watcherCase.AffectedCards)}");
        body.AppendLine();

        body.AppendLine("### Evidence");
        body.AppendLine();
        foreach (var item in watcherCase.Evidence)
        {
            var value = string.IsNullOrWhiteSpace(item.Value) ? "_not recorded_" : item.Value;
            var source = string.IsNullOrWhiteSpace(item.Source) ? "" : $" (source: {item.Source})";
            body.AppendLine($"- **{item.Label}**: {value}{source}");
        }
        body.AppendLine();

        body.AppendLine("## Changes");
        body.AppendLine();
        foreach (var line in ChangeGuidance(watcherCase.DetectorClass))
            body.AppendLine($"- {line}");
        body.AppendLine();

        body.AppendLine("## Acceptance");
        body.AppendLine();
        foreach (var line in Acceptance(watcherCase))
            body.AppendLine($"- {line}");
        body.AppendLine();

        body.AppendLine("## Model recommendation");
        body.AppendLine();
        body.AppendLine($"`{recommendation.Model}` / `{recommendation.ThinkingLevel}` (tier `{recommendation.Tier}`, policy `{recommendation.PolicyVersion}`). {recommendation.Reason}");

        return body.ToString();
    }

    private static IReadOnlyList<string> ChangeGuidance(WatcherDetectorClass detectorClass) => detectorClass switch
    {
        WatcherDetectorClass.Repetition =>
        [
            "Find why the same fingerprint repeats without any state change between occurrences.",
            "Fix the underlying fault rather than the retry that exposes it; a retry budget is not a repair.",
            "If the repetition is legitimate, give the producer a state token that changes so the run stops looking identical.",
        ],
        WatcherDetectorClass.Contradiction =>
        [
            "Establish which of the two sources is authoritative for this fact and record that decision.",
            "Correct the projection that disagrees with the authority, or make the disagreement impossible to represent.",
            "Do not reconcile by overwriting one side silently; a contradiction that can recur needs a guard.",
        ],
        WatcherDetectorClass.Silence =>
        [
            "Restore the missing producer, or make the absence visible where the operator already looks.",
            "Never render a downstream reason derived from an unknown state; report the missing signal as missing.",
        ],
        WatcherDetectorClass.Drift =>
        [
            "Confirm the version change caused the dependent failure before changing the parser or probe.",
            "Make the dependent component tolerant of the new version, or pin the version deliberately.",
        ],
        _ =>
        [
            "Repair the listed subjects so the validation the product already computes passes.",
            "If the rule is wrong rather than the data, change the rule and say so.",
        ],
    };

    private static IReadOnlyList<string> Acceptance(WatcherCase watcherCase)
    {
        var lines = new List<string>
        {
            $"The Watcher sweep no longer opens a case for fingerprint `{watcherCase.Fingerprint}`.",
            "The fix is covered by a test that fails without it.",
        };
        if (watcherCase.AffectedCards.Count > 0)
            lines.Add($"The affected cards ({string.Join(", ", watcherCase.AffectedCards)}) are re-checked and their state is explained.");
        lines.Add("No unrelated behaviour changes; the diff stays inside the named subsystem.");
        return lines;
    }

    private static string Stamp(DateTime at) => at.ToUniversalTime().ToString("u");
}
