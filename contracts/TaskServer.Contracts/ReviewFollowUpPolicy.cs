using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>The bounded action selected after a complete set of review verdicts.</summary>
public enum ReviewFollowUpAction
{
    Accept,
    ReviewFindingRound,
    ReviewConcernRound,
    RetryAspect,
}

/// <summary>Plane-neutral input used by both local and remote review paths.</summary>
public sealed record ReviewFollowUpFinding(
    string Aspect,
    string? Status,
    string? Summary,
    string? EvidenceChecked = null,
    string? Finding = null,
    string? Classification = null,
    bool InfrastructureFailure = false,
    string? ReportBody = null);

public sealed record ReviewFollowUpDecision(
    ReviewGrade Grade,
    ReviewFollowUpAction Action,
    IReadOnlyList<ReviewFollowUpFinding> Findings,
    string Reason)
{
    public bool StartsCodingRound =>
        Action is ReviewFollowUpAction.ReviewFindingRound or ReviewFollowUpAction.ReviewConcernRound;
}

/// <summary>
/// One review follow-up policy shared by local aspect review and Remote Review.
/// Session mechanics, lane writes, and prompt persistence remain application
/// concerns; this class owns only severity, actionability, and the round bound.
/// </summary>
public static partial class ReviewFollowUpPolicy
{
    [GeneratedRegex(@"(?:^|[\s`'(])(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+|\b[A-Za-z0-9_-]+\.(?:cs|ts|tsx|js|scss|css|html|md|json|ya?ml|xml|sh)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileReferenceRegex();

    public static ReviewFollowUpDecision Decide(
        IEnumerable<ReviewFollowUpFinding> findings,
        int concernRoundsUsed,
        int maxConcernRounds)
    {
        var all = findings?.ToArray() ?? [];
        var grade = ReviewGradingPolicy.Grade(all.Select(item => item.Status));
        var aspectRetry = all.Where(IsAspectInfrastructureResult).ToArray();
        if (aspectRetry.Length > 0)
        {
            return new ReviewFollowUpDecision(
                grade,
                ReviewFollowUpAction.RetryAspect,
                aspectRetry,
                $"Aspect verdict infrastructure failed for {string.Join(", ", aspectRetry.Select(item => item.Aspect))}; the aspect retry was exhausted.");
        }

        var blocks = all.Where(item => ReviewGradingPolicy.IsBlockingToken(item.Status)).ToArray();
        if (blocks.Length > 0)
        {
            return new ReviewFollowUpDecision(
                grade,
                ReviewFollowUpAction.ReviewFindingRound,
                blocks,
                $"{blocks.Length} blocking review finding(s) require a coding round.");
        }

        var actionable = all.Where(IsActionableConcern).ToArray();
        if (grade == ReviewGrade.PassWithConcerns
            && actionable.Length > 0
            && Math.Max(0, maxConcernRounds) > concernRoundsUsed)
        {
            return new ReviewFollowUpDecision(
                grade,
                ReviewFollowUpAction.ReviewConcernRound,
                actionable,
                $"{actionable.Length} actionable concern(s) qualify for bounded fix round {concernRoundsUsed + 1} of {Math.Max(0, maxConcernRounds)}.");
        }

        return new ReviewFollowUpDecision(
            grade,
            ReviewFollowUpAction.Accept,
            actionable,
            grade == ReviewGrade.PassWithConcerns
                ? "Concerns remain, but the configured concern-round budget is exhausted or no concern is actionable."
                : "Review passed without an actionable follow-up.");
    }

    public static bool IsActionableConcern(ReviewFollowUpFinding item)
    {
        if (!ReviewGradingPolicy.IsConcernToken(item.Status) || IsAspectInfrastructureResult(item))
            return false;
        if (Meaningful(item.Finding) || Meaningful(ExtractFindingSection(item.ReportBody))) return true;
        return (Meaningful(item.EvidenceChecked) && FileReferenceRegex().IsMatch(item.EvidenceChecked!))
               || (Meaningful(item.Summary) && FileReferenceRegex().IsMatch(item.Summary!));
    }

    public static string? ExtractFindingSection(string? reportBody)
    {
        if (string.IsNullOrWhiteSpace(reportBody)) return null;
        var lines = reportBody.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line =>
            line.TrimStart().StartsWith("## Finding", StringComparison.OrdinalIgnoreCase)
            || line.TrimStart().StartsWith("## Findings", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var body = lines.Skip(start + 1)
            .TakeWhile(line => !line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        var text = string.Join(" ", body);
        return text.Length == 0 ? null : text;
    }

    private static bool IsAspectInfrastructureResult(ReviewFollowUpFinding item)
        => item.InfrastructureFailure
           || string.Equals(item.Classification, "review:unparseable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Classification, "ReviewInfra", StringComparison.OrdinalIgnoreCase)
           || (item.Summary?.Contains("no parseable verdict", StringComparison.OrdinalIgnoreCase) ?? false)
           || (item.Summary?.Contains("infra crash", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool Meaningful(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(value.Trim(), "n/a", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Facts used to decide whether a prior aspect verdict may be carried over.</summary>
public sealed record ScopedReviewAspect(
    string Aspect,
    string Status,
    IReadOnlyList<string> EvidenceChecked,
    bool RaisedFinding);

public sealed record ScopedReviewFacts(
    bool Enabled,
    bool ReviewedTreeIdentityMatches,
    int DeltaFileCount,
    int MaximumDeltaFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<ScopedReviewAspect> PreviousAspects);

public sealed record ScopedReviewAspectDecision(string Aspect, bool Run, string Reason);

/// <summary>Pure scoped re-review planner. Deterministic gates are outside this list and always run.</summary>
public static class ScopedReviewPolicy
{
    public static IReadOnlyList<ScopedReviewAspectDecision> Plan(ScopedReviewFacts facts)
    {
        var full = !facts.Enabled
                   || !facts.ReviewedTreeIdentityMatches
                   || facts.DeltaFileCount < 0
                   || facts.DeltaFileCount > Math.Max(0, facts.MaximumDeltaFiles);
        return facts.PreviousAspects.Select(aspect =>
        {
            if (full) return new ScopedReviewAspectDecision(aspect.Aspect, true, "full review fallback");
            if (aspect.RaisedFinding) return new ScopedReviewAspectDecision(aspect.Aspect, true, "raised the finding");
            if (TouchesEvidence(facts.ChangedFiles, aspect.EvidenceChecked))
                return new ScopedReviewAspectDecision(aspect.Aspect, true, "delta touches cited evidence");
            if (string.Equals(aspect.Aspect, "documentation-impact", StringComparison.OrdinalIgnoreCase)
                && facts.ChangedFiles.Any(IsPublicContractOrSchema))
                return new ScopedReviewAspectDecision(aspect.Aspect, true, "public contract or schema changed");
            if (string.Equals(aspect.Aspect, "tests-and-evidence", StringComparison.OrdinalIgnoreCase)
                && facts.RemovedFiles.Any(IsTestFile))
                return new ScopedReviewAspectDecision(aspect.Aspect, true, "test file removed");
            return new ScopedReviewAspectDecision(aspect.Aspect, false, "carried over from the prior review");
        }).ToArray();
    }

    private static bool TouchesEvidence(IEnumerable<string> changed, IEnumerable<string> evidence)
        => changed.Any(file => evidence.Any(cited =>
            string.Equals(Normalize(file), Normalize(cited), StringComparison.OrdinalIgnoreCase)));

    private static bool IsPublicContractOrSchema(string file)
    {
        var path = Normalize(file);
        return path.StartsWith("contracts/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/contracts/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("schemas/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/schemas/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("openapi.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestFile(string file)
    {
        var path = Normalize(file);
        return path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
               || path.Contains(".tests/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/test/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('.', '/');
}
