using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

/// <summary>
/// Deterministic post-aspect gate for solution-quality concerns that are too
/// strong to advance as ordinary accept-with-concerns. Aspect reviewers still
/// classify the evidence; this rule engine only upgrades narrow, non-shippable
/// concern wording into a bounded reissue / escalate decision.
/// </summary>
public static class SolutionQualityGate
{
    public const string RequirementFitAspectId = "requirement-fit";
    public const string CodeQualityAspectId = "code-quality";
    public const int MaxFindings = 8;

    private static readonly Regex RequirementFailureRegex = new(
        @"(?ix)
        (?:
            \b(?:does\s+not|doesn'?t|fails?\s+to)\s+
                (?:meet|satisfy|address|solve|implement|fulfill)\b |
            \b(?:goal|task|prompt|requirement|acceptance\s+criteri(?:on|a)|core\s+ask)\b
                .{0,80}\b(?:not\s+met|missing|unsatisfied|unaddressed|contradicted|failed)\b |
            \b(?:only|merely)\s+(?:renames?|documents?|comments?|formats?|moves?)\b
                .{0,80}\b(?:without|but\s+not)\b.{0,80}\b(?:implement|solv|address)\b
        )",
        RegexOptions.Compiled);

    private static readonly Regex RedundantWorkRegex = new(
        @"(?ix)
        (?:
            \bredundant\b.{0,80}\b(?:already|existing|re[-\s]?implement|work|migration|task|solution)\b |
            \balready\s+(?:exists?|existed|done|implemented|covered)\b |
            \b(?:duplicates?|duplicated)\s+(?:existing|already)\b |
            \bre[-\s]?implements?\s+(?:existing|already)\b
        )",
        RegexOptions.Compiled);

    private static readonly Regex HalfFinishedRegex = new(
        @"(?ix)
        (?:
            \bhalf[-\s]?finished\b |
            \bplaceholder\b |
            \bstub(?:bed|s)?\b |
            \bnot\s+(?:wired|connected|called|used)\b |
            \b(?:todo|fixme)\b.{0,80}\b(?:left|placeholder|stub|unfinished|not\s+implemented|required|blocking)\b |
            \bdead\s+(?:path|branch|implementation)\b |
            \bbroken\s+(?:path|flow|implementation|type)\b |
            \btype\s+errors?\b |
            \b(?:obvious|visible|introduced|introduces?)\s+regression\b |
            \bregression\b.{0,80}\b(?:breaks?|introduced|visible|ships?)
        )",
        RegexOptions.Compiled);

    public enum SolutionQualityGateAction
    {
        Pass,
        Reissue,
        Escalate,
    }

    public sealed record Decision
    {
        public SolutionQualityGateAction Action { get; init; } = SolutionQualityGateAction.Pass;
        public IReadOnlyList<string> Findings { get; init; } = [];
        public string Reason { get; init; } = "No blocking solution-quality concerns found.";

        public bool IsBlocking => Action != SolutionQualityGateAction.Pass;
    }

    public static Decision Evaluate(AspectRunReport report, int priorReissues, int maxReissues)
    {
        var findings = ExtractFindings(report);
        if (findings.Count == 0)
        {
            return new Decision();
        }

        if (findings.Count > MaxFindings)
        {
            findings = findings.Take(MaxFindings).ToList();
        }

        if (priorReissues >= maxReissues)
        {
            return new Decision
            {
                Action = SolutionQualityGateAction.Escalate,
                Findings = findings,
                Reason = $"Solution-quality gate could not clear {findings.Count} blocking concern(s) after {priorReissues} prior reissue(s); user attention required.",
            };
        }

        return new Decision
        {
            Action = SolutionQualityGateAction.Reissue,
            Findings = findings,
            Reason = $"Solution-quality gate found {findings.Count} blocking concern(s); reissuing instead of accepting with concerns.",
        };
    }

    public static string BuildFollowUp(Decision decision)
    {
        var sb = new StringBuilder();
        sb.AppendLf("Auto-review found solution-quality concerns that are not acceptable as ordinary human-review notes.");
        sb.AppendLf("Resolve every item below before ending with [[TASK_DONE]] again:");
        sb.AppendLf();
        foreach (var finding in decision.Findings.Take(MaxFindings))
        {
            sb.AppendLf($"- [ ] {finding}");
        }
        sb.AppendLf();
        sb.AppendLf("Do not redo already-complete work. First compare against the current code/task state, then make only the missing or corrective change. If the task goal is already satisfied, prove that with evidence and end with [[TASK_DONE]]. If it cannot be completed, stop with [[TASK_BLOCKED:missing-dependency-xyz]], replacing the example reason with the actual short reason.");
        return sb.ToString();
    }

    private static List<string> ExtractFindings(AspectRunReport report)
    {
        var findings = new List<string>();
        foreach (var verdict in report.Verdicts)
        {
            if (verdict.Status != AspectStatus.Concerns) continue;

            var text = $"{verdict.Summary}\n{verdict.Body}";
            var isRequirementFit = string.Equals(verdict.Aspect, RequirementFitAspectId, StringComparison.OrdinalIgnoreCase);
            var isCodeQuality = string.Equals(verdict.Aspect, CodeQualityAspectId, StringComparison.OrdinalIgnoreCase);
            if (!isRequirementFit && !isCodeQuality) continue;

            var matched = isRequirementFit
                ? RequirementFailureRegex.IsMatch(text) || RedundantWorkRegex.IsMatch(text) || HalfFinishedRegex.IsMatch(text)
                : HalfFinishedRegex.IsMatch(text) || RedundantWorkRegex.IsMatch(text);

            if (!matched) continue;

            var summary = string.IsNullOrWhiteSpace(verdict.Summary)
                ? "Concern summary was empty."
                : verdict.Summary.Trim();
            findings.Add($"{verdict.Aspect}: {summary}");
        }

        return findings;
    }
}
