using System.Globalization;
using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// Turns a case into the card draft a proposal carries. Pure text composition:
/// the evidence is already collected, and the house prompt style (Context with
/// timestamps and paths, Changes, Acceptance) is the only thing added.
/// </summary>
/// <remarks>
/// The draft deliberately reads as a repair card, not as a report. An operator
/// approving it should get a card another agent can pick up without first
/// reopening the Watcher case.
/// </remarks>
public static class WatcherCardDraftComposer
{
    public static WatcherCardDraft Compose(
        WatcherCase watcherCase,
        IReadOnlyList<string> overlappingCards,
        string? analysisNote = null)
    {
        ArgumentNullException.ThrowIfNull(watcherCase);
        ArgumentNullException.ThrowIfNull(overlappingCards);

        var prompt = new StringBuilder();
        AppendContext(prompt, watcherCase, overlappingCards, analysisNote);
        AppendChanges(prompt, watcherCase);
        AppendAcceptance(prompt, watcherCase);

        return new WatcherCardDraft
        {
            Title = watcherCase.Title,
            Prompt = prompt.ToString().TrimEnd() + Environment.NewLine,
            TaskType = TaskType(watcherCase.DetectorClass),
            Tags = [WatcherTags.Proposal, WatcherTags.ForClass(watcherCase.DetectorClass)],
            References = overlappingCards,
        };
    }

    /// <summary>
    /// Hygiene is bookkeeping the product already knows how to do, so it drafts
    /// as a chore. Everything else describes a system that is telling two
    /// different stories, which is a bug until proven otherwise.
    /// </summary>
    public static string TaskType(string detectorClass)
        => detectorClass == WatcherDetectorClasses.Hygiene ? TaskTypes.Chore : TaskTypes.Bug;

    private static void AppendContext(
        StringBuilder prompt,
        WatcherCase watcherCase,
        IReadOnlyList<string> overlappingCards,
        string? analysisNote)
    {
        prompt.AppendLine("## Context");
        prompt.AppendLine();
        prompt.AppendLine(watcherCase.Summary);
        prompt.AppendLine();
        prompt.AppendLine(
            $"The Watcher raised this as case `{watcherCase.CaseId}` from the `{watcherCase.DetectorClass}` detector "
            + $"(rule `{watcherCase.DetectorRule}`, fingerprint `{watcherCase.Fingerprint}`). "
            + $"It was first seen {Stamp(watcherCase.FirstSeenAtUtc)}, last seen {Stamp(watcherCase.LastSeenAtUtc)}, "
            + $"and observed {watcherCase.Occurrences} {(watcherCase.Occurrences == 1 ? "time" : "times")} "
            + $"across {watcherCase.SweepsSeen} sweeps.");
        prompt.AppendLine();

        if (analysisNote is { Length: > 0 })
        {
            prompt.AppendLine(analysisNote);
            prompt.AppendLine();
        }

        prompt.AppendLine("### Evidence");
        prompt.AppendLine();
        foreach (var item in watcherCase.Evidence.Items)
        {
            var value = item.Missing
                ? "absent (the collector looked and found nothing)"
                : Inline(item.Value);
            var stamp = item.ObservedAtUtc is null ? string.Empty : $" _(at {Stamp(item.ObservedAtUtc.Value)})_";
            prompt.AppendLine($"- **{item.Label}** from `{item.Source}`: {value}{stamp}");
        }

        prompt.AppendLine();
        prompt.AppendLine($"Evidence pack digest: `{watcherCase.EvidencePackDigest}`.");
        prompt.AppendLine();

        if (watcherCase.AffectedCards.Count > 0)
        {
            prompt.AppendLine(
                $"Affected cards: {string.Join(", ", watcherCase.AffectedCards)}.");
            prompt.AppendLine();
        }

        if (overlappingCards.Count > 0)
        {
            prompt.AppendLine(
                $"Overlapping open cards: {string.Join(", ", overlappingCards)}. Check them before starting; "
                + "this proposal may be better folded into one of them.");
            prompt.AppendLine();
        }
    }

    private static void AppendChanges(StringBuilder prompt, WatcherCase watcherCase)
    {
        prompt.AppendLine("## Changes");
        prompt.AppendLine();
        foreach (var line in ChangeLines(watcherCase))
            prompt.AppendLine($"{line}");
        prompt.AppendLine();
    }

    /// <summary>
    /// The work a class implies. The Watcher proposes the shape of the repair,
    /// never the repair itself: every line asks for a root cause first, because
    /// a detector proves that something is wrong, not why.
    /// </summary>
    private static IEnumerable<string> ChangeLines(WatcherCase watcherCase) => watcherCase.DetectorClass switch
    {
        WatcherDetectorClasses.Repetition =>
        [
            "1. Find why the same fingerprint recurs without any state change between occurrences.",
            "2. Fix the cause, or bound the loop so it cannot repeat unbounded.",
            "3. Add the regression coverage that would have failed while the loop was running.",
        ],
        WatcherDetectorClasses.Contradiction =>
        [
            "1. Establish which of the two sources is authoritative for this fact.",
            "2. Correct the projection that disagrees with it, or record the disagreement as an explicit, visible state.",
            "3. Add coverage that fails when the two sources drift apart again.",
        ],
        WatcherDetectorClasses.Silence =>
        [
            "1. Find why the expected signal stopped and whether the producer or the transport is at fault.",
            "2. Restore the signal, and make its absence visible where the operator already looks.",
            "3. Add coverage that proves the absent signal is reported rather than rendered as a healthy default.",
        ],
        WatcherDetectorClasses.Drift =>
        [
            "1. Confirm the version change is the cause and not a coincidence inside the drift window.",
            "2. Repair the dependent path for the new version, or pin the tool until it is repaired.",
            "3. Add coverage that fails on the version the dependent path cannot handle.",
        ],
        _ =>
        [
            "1. Repair each listed validation error at its source.",
            "2. Check whether the validator can reject the same shape earlier, before it reaches the catalogue.",
            "3. Add coverage for the shapes that were wrong here.",
        ],
    };

    private static void AppendAcceptance(StringBuilder prompt, WatcherCase watcherCase)
    {
        prompt.AppendLine("## Acceptance");
        prompt.AppendLine();
        prompt.AppendLine(
            $"- The signal behind fingerprint `{watcherCase.Fingerprint}` no longer reproduces, with the measurement that shows it.");
        prompt.AppendLine("- The root cause is named in the result, not only the symptom that was observed.");
        prompt.AppendLine("- Regression coverage exists and fails without the fix.");
        if (watcherCase.AffectedCards.Count > 0)
        {
            prompt.AppendLine(
                $"- The affected cards ({string.Join(", ", watcherCase.AffectedCards)}) are re-checked and either unblocked or explicitly reported as still blocked.");
        }

        prompt.AppendLine();
    }

    private static string Stamp(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Keep an evidence value on one Markdown line so the list stays readable.</summary>
    private static string Inline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_empty_";
        var single = value.ReplaceLineEndings(" ").Trim();
        if (single.Length > 300) single = single[..300] + "...";
        return "`" + single.Replace("`", "'", StringComparison.Ordinal) + "`";
    }
}
