using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Deterministic pre-pipeline check that fires before the core agent run on a
/// re-issued task: it answers two questions a human reviewer would ask before
/// letting an orchestrator blindly restart a card -
/// <list type="number">
/// <item>Is this run a re-issue (the auto-review loop sent the card back)?</item>
/// <item>Does the card still carry open items from the previous run
/// (the auto-review follow-up reason, unchecked checklist boxes, or aspect
/// concern/block summaries)?</item>
/// </list>
/// When BOTH hold the orchestrator must intervene rather than restart from
/// scratch: it foregrounds the concrete open items so the rerun addresses them
/// first (and, once a card has bounced too many times, flags it for human
/// review). When either is false the check is a no-op - a fresh run, or a
/// re-issue with nothing left open, is left exactly as it was.
///
/// <para>
/// This file is intentionally free of I/O (mirrors
/// <see cref="OrchestratorPrepRules"/>): it takes a small input record and
/// returns a decision plus the ready-to-use prompt block. The
/// <c>ProjectRunner</c> recorder owns reading the job folder, prepending the
/// block to the run prompt, posting the orchestrator chat note, and recording
/// the pipeline step.
/// </para>
/// </summary>
public static class ReissueOpenItemsPreCheck
{
    /// <summary>
    /// Once a card has already completed this many prior runs and is being
    /// re-issued again with the same open items, the check escalates: the
    /// foreground framing tells the agent to fix only those items or stop, and
    /// the chat note flags the card for human attention. Below the threshold it
    /// just foregrounds.
    /// </summary>
    public const int EscalateAfterReissues = 2;

    /// <summary>Upper bound on foregrounded items so a runaway follow-up cannot
    /// bloat the run prompt. The count is the load-bearing signal; the body is a
    /// pointer to the full follow-up / aspect reports.</summary>
    public const int MaxOpenItems = 12;

    private const int MaxItemLength = 200;

    // `- [ ]` / `* [ ]` / `+ [ ]` unchecked checklist line. A checked `[x]`
    // box is deliberately not matched (it is a resolved item).
    private static readonly Regex UncheckedItemRegex = new(
        @"^\s*[-*+]\s*\[\s*\]\s*(?<text>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Inputs to one pre-check tick on a single card. All fields are
    /// derived deterministically from the job folder at pickup time.</summary>
    public sealed record PreCheckInput
    {
        /// <summary>The card carries the auto-review re-issue tag
        /// (<see cref="ReviewDecisionOrchestrator.ReissueTagId"/>).</summary>
        public bool HasReissueTag { get; init; }
        /// <summary>Explicit instructions persisted by an operator reissue move.</summary>
        public bool HasOperatorInstructions { get; init; }

        /// <summary>A prior pipeline run for this card has crossed the agent
        /// boundary - either completed normally or recorded core/post work
        /// before a short-circuit moved it back to Ready. Together with
        /// <see cref="HasReissueTag"/> this is the deterministic "this is a
        /// re-issue restart" signal.</summary>
        public bool PriorRunCompleted { get; init; }

        /// <summary>Number of prior completed runs for this card (the pipeline
        /// record's attempt counter). Drives the escalate threshold.</summary>
        public int PriorRunCount { get; init; }

        /// <summary>Body of <c>orchestrator-follow-up.md</c> (the reason the
        /// auto-review loop sent the card back), or empty when absent.</summary>
        public string FollowUpText { get; init; } = "";

        /// <summary>Concern/block one-line summaries lifted from the
        /// <c>aspect-*.md</c> frontmatter of the previous run.</summary>
        public IReadOnlyList<string> AspectConcernSummaries { get; init; } = [];

        /// <summary>Typed cause copied from the latest quality-loop reopen event.</summary>
        public string ReissueCause { get; init; } = "unknown";

        /// <summary>Stable analysis stratum derived from <see cref="ReissueCause"/>.</summary>
        public string PromptFamily { get; init; } = "other-reissue";
    }

    public enum PreCheckAction
    {
        /// <summary>Not a re-issue, or a re-issue with no open items: do nothing.</summary>
        None,

        /// <summary>Re-issue with open items under the bounce budget: foreground
        /// the items into the run prompt and post an informational note.</summary>
        ForegroundOpenItems,

        /// <summary>Re-issue with open items past the bounce budget: foreground
        /// with a fix-only-or-stop framing and flag the card for human review.</summary>
        Escalate,
    }

    public sealed record PreCheckDecision
    {
        public bool IsReissue { get; init; }
        public IReadOnlyList<string> OpenItems { get; init; } = [];
        public string? FollowUpText { get; init; }
        public PreCheckAction Action { get; init; } = PreCheckAction.None;
        public int PriorRunCount { get; init; }
        public string ReissueCause { get; init; } = "unknown";
        public string PromptFamily { get; init; } = "other-reissue";

        /// <summary>One-line orchestrator chat note describing the intervention,
        /// or null when <see cref="Action"/> is <see cref="PreCheckAction.None"/>.</summary>
        public string? Note { get; init; }

        /// <summary>Markdown block to prepend to the run prompt so the open items
        /// lead the agent's context, or null when there is nothing to foreground.</summary>
        public string? ForegroundBlock { get; init; }

        public bool Intervenes => Action != PreCheckAction.None;
        public bool HasOpenItems => OpenItems.Count > 0;
    }

    /// <summary>
    /// Map the deterministic inputs to a decision. Returns
    /// <see cref="PreCheckAction.None"/> when the run is not a re-issue or the
    /// re-issue has no open items (the two no-intervention cases); otherwise
    /// foregrounds the open items, escalating once the card has bounced past
    /// <see cref="EscalateAfterReissues"/>.
    /// </summary>
    public static PreCheckDecision Evaluate(PreCheckInput input)
    {
        var isReissue = input.HasOperatorInstructions || (input.HasReissueTag && input.PriorRunCompleted);
        if (!isReissue)
            return new PreCheckDecision
            {
                IsReissue = false,
                Action = PreCheckAction.None,
                PriorRunCount = input.PriorRunCount,
                ReissueCause = input.ReissueCause,
                PromptFamily = input.PromptFamily,
            };

        var openItems = ExtractOpenItems(input.FollowUpText, input.AspectConcernSummaries);
        if (openItems.Count == 0)
            return new PreCheckDecision
            {
                IsReissue = true,
                OpenItems = openItems,
                Action = PreCheckAction.None,
                PriorRunCount = input.PriorRunCount,
                ReissueCause = input.ReissueCause,
                PromptFamily = input.PromptFamily,
            };

        var escalate = input.PriorRunCount >= EscalateAfterReissues;
        var action = escalate ? PreCheckAction.Escalate : PreCheckAction.ForegroundOpenItems;
        var note = escalate
            ? $"Reissue #{input.PriorRunCount}: {openItems.Count} open item(s) still unresolved across attempts. " +
              "Foregrounding them with a fix-only-or-stop framing and flagging the card for human review."
            : $"Reissue with {openItems.Count} open item(s): foregrounding them so the rerun resolves them before anything else.";

        return new PreCheckDecision
        {
            IsReissue = true,
            OpenItems = openItems,
            FollowUpText = input.FollowUpText,
            Action = action,
            PriorRunCount = input.PriorRunCount,
            ReissueCause = input.ReissueCause,
            PromptFamily = input.PromptFamily,
            Note = note,
            ForegroundBlock = BuildForegroundBlock(openItems, escalate),
        };
    }

    /// <summary>
    /// Pull the concrete open items out of the previous run's artefacts:
    /// unchecked checklist boxes in the follow-up, then aspect concern/block
    /// summaries. When neither yields anything but the follow-up still carries a
    /// reason, the first meaningful follow-up line stands in as a single item -
    /// a re-issue always has a reason, and that reason is the open item.
    /// Deterministic, de-duplicated (case-insensitive), and capped at
    /// <see cref="MaxOpenItems"/>.
    /// </summary>
    public static IReadOnlyList<string> ExtractOpenItems(
        string? followUpText, IReadOnlyList<string>? aspectConcernSummaries)
    {
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            var normalized = Normalize(candidate);
            if (normalized.Length == 0) return;
            if (seen.Add(normalized)) items.Add(normalized);
        }

        var lines = (followUpText ?? "").Replace("\r\n", "\n").Split('\n');

        var sawChecklist = false;
        foreach (var raw in lines)
        {
            var match = UncheckedItemRegex.Match(raw);
            if (!match.Success) continue;
            sawChecklist = true;
            Add(match.Groups["text"].Value);
        }

        if (aspectConcernSummaries != null)
            foreach (var summary in aspectConcernSummaries)
                Add(summary);

        // Fallback: a re-issue with no checklist and no aspect concern still has
        // a follow-up reason. Treat the first meaningful line as the open item
        // so the intervention never silently drops a real bounce reason.
        if (!sawChecklist && items.Count == 0)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('#')) continue;   // "# Orchestrator follow-up" heading
                if (line.StartsWith("---")) continue;  // horizontal rule
                Add(line);
                break;
            }
        }

        return items.Count > MaxOpenItems ? items.Take(MaxOpenItems).ToList() : items;
    }

    /// <summary>
    /// Render the markdown block prepended to the run prompt. It names the open
    /// items as an unchecked checklist so the rerun has a concrete list to work
    /// down, and switches to a fix-only-or-stop framing once the card is being
    /// escalated.
    /// </summary>
    public static string BuildForegroundBlock(IReadOnlyList<string> openItems, bool escalate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Reissue: resolve these open items first");
        sb.AppendLine();
        if (escalate)
        {
            sb.AppendLine(
                "This task has been sent back multiple times and the items below are still open. " +
                "Address ONLY these items in this run. If you cannot resolve them, stop and end with " +
                "`[[TASK_BLOCKED:missing-dependency-xyz]]`, replacing the example reason with the actual short reason, so a human can step in - do not start unrelated work.");
        }
        else
        {
            sb.AppendLine(
                "This task was sent back (reissue) with unfinished items from the previous run. " +
                "Resolve these open items before anything else, then continue with the task as written:");
        }
        sb.AppendLine();
        foreach (var item in openItems)
            sb.AppendLine($"- [ ] {item}");
        sb.AppendLine();
        sb.AppendLine("Details are in `orchestrator-follow-up.md` and the `aspect-*.md` reports in the job folder.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "";
        var text = candidate.Replace("\r", " ").Replace("\n", " ").Trim();
        // Strip a leading markdown bullet / checkbox the source may still carry.
        text = Regex.Replace(text, @"^[-*+]\s*(\[\s*[xX ]?\s*\]\s*)?", "");
        text = WhitespaceRun.Replace(text, " ").Trim();
        if (text.Length > MaxItemLength)
            text = text[..(MaxItemLength - 1)].TrimEnd() + "…";
        return text;
    }
}
