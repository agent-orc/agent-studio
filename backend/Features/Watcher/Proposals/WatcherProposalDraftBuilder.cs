using System.Globalization;
using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// Composes the card draft a proposal carries: Context with timestamps and
/// paths, Changes, Acceptance. Pure, so the exact text a proposal will put on
/// the board is testable without touching the task API.
/// </summary>
/// <remarks>
/// The Watcher may state what it observed and what would prove the problem
/// gone. It may not state a fix it has not verified, so the Changes section is
/// written as bounded investigation and correction steps anchored in the
/// evidence, and a model's suggestions are quoted as suggestions.
/// </remarks>
public static class WatcherProposalDraftBuilder
{
    /// <summary>Maximum card title length, so a board row stays readable.</summary>
    public const int MaxTitleLength = 110;

    public static WatcherCardDraft Build(
        WatcherCase item,
        WatcherEvidencePack pack,
        WatcherAnalysis? analysis,
        IReadOnlyList<string>? overlappingCards = null,
        IReadOnlyList<string>? manualPrecedent = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(pack);

        var related = (overlappingCards ?? [])
            .Concat(item.AffectedCards)
            .Where(card => !string.IsNullOrWhiteSpace(card))
            .Select(card => card.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(card => card, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WatcherCardDraft
        {
            Title = Title(item),
            TaskType = WatcherModelRouting.TaskTypeFor(item.DetectorClass),
            Tags =
            [
                WatcherTags.Proposal,
                WatcherTags.DetectorClass(item.DetectorClass),
                WatcherTags.Fingerprint(item.FingerprintDigest),
            ],
            RelatedTo = related,
            PromptMarkdown = Prompt(item, pack, analysis, related, manualPrecedent ?? []),
        };
    }

    /// <summary>
    /// The comment body used when the fingerprint already has an open card. It
    /// carries the same evidence, so the operator does not have to open the case
    /// to see what changed.
    /// </summary>
    public static string Comment(WatcherCase item, WatcherEvidencePack pack)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(pack);
        var builder = new StringBuilder();
        builder.Append("The Watcher saw fingerprint `").Append(item.Fingerprint)
               .Append("` again (case ").Append(item.Id).Append(", ")
               .Append(item.Occurrences.ToString(CultureInfo.InvariantCulture))
               .AppendLine(" occurrences). This card already covers that fingerprint, so no new card was created.");
        builder.AppendLine();
        builder.Append(pack.ToMarkdown());
        return builder.ToString();
    }

    private static string Title(WatcherCase item)
    {
        var title = item.Title.Trim();
        if (title.Length > MaxTitleLength)
            title = title[..(MaxTitleLength - 1)].TrimEnd() + "…";
        return title;
    }

    private static string Prompt(
        WatcherCase item,
        WatcherEvidencePack pack,
        WatcherAnalysis? analysis,
        IReadOnlyList<string> related,
        IReadOnlyList<string> manualPrecedent)
    {
        var builder = new StringBuilder();

        builder.AppendLine("## Context");
        builder.AppendLine();
        builder.Append("The global Watcher opened case `").Append(item.Id)
               .Append("` on ").Append(Stamp(item.FirstSeenAtUtc))
               .Append(" under the ").Append(item.DetectorClass)
               .AppendLine(" detector and confirmed it across two sweeps before proposing this card.");
        builder.AppendLine();
        builder.Append("Detector rule: ").AppendLine(item.DetectorRule);
        builder.AppendLine();
        builder.AppendLine("### Evidence");
        builder.AppendLine();
        builder.Append(pack.ToMarkdown());
        builder.AppendLine();
        builder.Append("Evidence pack digest: `").Append(pack.Digest).AppendLine("`.");

        if (related.Count > 0)
        {
            builder.AppendLine();
            builder.Append("Overlapping cards: ").Append(string.Join(", ", related)).AppendLine(".");
        }
        if (manualPrecedent.Count > 0)
        {
            builder.AppendLine();
            builder.Append("Manual precedent for this finding class: ")
                   .Append(string.Join(", ", manualPrecedent))
                   .AppendLine(". Those cards were written by hand; the Watcher did not create them.");
        }

        if (analysis is not null)
        {
            builder.AppendLine();
            builder.AppendLine("### Bounded analysis");
            builder.AppendLine();
            builder.Append("Root cause (").Append(analysis.Confidence).Append(" confidence): ")
                   .AppendLine(analysis.RootCause);
            if (analysis.CompetingHypotheses.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Competing hypotheses:");
                foreach (var hypothesis in analysis.CompetingHypotheses)
                    builder.Append("- ").AppendLine(hypothesis);
            }
            if (analysis.Unknowns.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Unknowns the analysis could not close:");
                foreach (var unknown in analysis.Unknowns)
                    builder.Append("- ").AppendLine(unknown);
            }
            builder.AppendLine();
            builder.AppendLine(
                "The analysis is a preparation aid. It carries no authority and was not verified against the running system.");
        }

        builder.AppendLine();
        builder.AppendLine("## Changes");
        builder.AppendLine();
        foreach (var change in Changes(item))
            builder.Append("- ").AppendLine(change);
        if (analysis is { SuggestedChanges.Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("Suggested by the bounded analysis, to confirm before acting:");
            foreach (var change in analysis.SuggestedChanges)
                builder.Append("- ").AppendLine(change);
        }

        builder.AppendLine();
        builder.AppendLine("## Acceptance");
        builder.AppendLine();
        foreach (var acceptance in Acceptance(item))
            builder.Append("- ").AppendLine(acceptance);

        return builder.ToString();
    }

    private static IReadOnlyList<string> Changes(WatcherCase item) => item.DetectorClass switch
    {
        WatcherDetectorClasses.Repetition =>
        [
            $"Find why `{item.Fingerprint}` recurs and stop it at the producer that emits it, not at the surface that reports it.",
            "Decide whether the repetition should break a loop, escalate, or be retried, and make that decision explicit in code.",
            "Add the counter or breaker that would have surfaced the repetition before it reached this count.",
        ],
        WatcherDetectorClasses.Contradiction =>
        [
            "Establish which of the two sources in the evidence table is authoritative for this fact, and record that decision.",
            "Make the non-authoritative projection derive from the authoritative one, or make it report the disagreement instead of a value.",
            "Never resolve the contradiction by silencing one side.",
        ],
        WatcherDetectorClasses.Silence =>
        [
            "Restore the missing signal, or make its absence a typed, visible fact rather than a stale value in the UI.",
            "Check every surface that reads this signal for a reason string it renders while the underlying state is unknown.",
            "Give the signal an explicit staleness threshold that a surface can render.",
        ],
        WatcherDetectorClasses.Drift =>
        [
            "Confirm from the evidence whether the recorded version change caused the dependent failure, or rule it out.",
            "Repair or pin the changed tool, and make the dependency between it and the failing step explicit.",
            "Add the check that would have caught the incompatibility at the version change instead of at the first dependent failure.",
        ],
        WatcherDetectorClasses.Hygiene =>
        [
            "Fix the validation errors listed in the evidence table, or record why each remaining one is acceptable.",
            "Make the validator's output reachable from a surface an operator already looks at.",
        ],
        _ =>
        [
            "Investigate the evidence table and decide on a bounded correction.",
        ],
    };

    private static IReadOnlyList<string> Acceptance(WatcherCase item) => item.DetectorClass switch
    {
        WatcherDetectorClasses.Repetition =>
        [
            $"Fingerprint `{item.Fingerprint}` no longer recurs without a state change between occurrences.",
            "A regression test proves the repetition is now detected or prevented at its producer.",
            $"Watcher case `{item.Id}` reaches a resolved terminal on the next sweep.",
        ],
        WatcherDetectorClasses.Contradiction =>
        [
            "The two sources named in the evidence table agree, or the disagreement is reported as a typed fact.",
            "A test covers the exact state sequence that produced the contradiction.",
            $"Watcher case `{item.Id}` reaches a resolved terminal on the next sweep.",
        ],
        WatcherDetectorClasses.Silence =>
        [
            "The expected signal arrives inside its declared cadence, or its absence is rendered as unknown rather than as a stale reason.",
            "A test covers the overdue case and the surface it drives.",
            $"Watcher case `{item.Id}` reaches a resolved terminal on the next sweep.",
        ],
        WatcherDetectorClasses.Drift =>
        [
            "The dependent step succeeds again on the current tool version.",
            "The dependency between the tool version and the step is asserted by a test that fails on an incompatible version.",
            $"Watcher case `{item.Id}` reaches a resolved terminal on the next sweep.",
        ],
        WatcherDetectorClasses.Hygiene =>
        [
            "The validator reports zero errors, or every remaining error has a recorded, referenced justification.",
            $"Watcher case `{item.Id}` reaches a resolved terminal on the next sweep.",
        ],
        _ =>
        [
            $"Watcher case `{item.Id}` reaches a resolved terminal on the next sweep.",
        ],
    };

    private static string Stamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
