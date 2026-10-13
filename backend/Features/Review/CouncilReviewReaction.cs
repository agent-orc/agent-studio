using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Review;

/// <summary>Structured finding extraction for the quality-grade protocol.</summary>
public static class CodeReviewFindingParsing
{
    private static readonly Regex FindingSentinel = new(
        @"\[\[CODE_REVIEW_FINDING:\s*text=(?<text>.+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static IReadOnlyList<string> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();
        return FindingSentinel.Matches(output)
            .Select(match => Normalize(match.Groups["text"].Value))
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string Normalize(string value)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim().Trim('-', '*', ' ');
        return text.Length <= 600 ? text : text[..597].TrimEnd() + "...";
    }
}

/// <summary>Pure bounded-loop policy for reacting to every named review deficiency.</summary>
public static class CouncilReviewPolicy
{
    public static CouncilReviewReaction Derive(
        string reviewFileName, CodeReviewGrade? grade, IReadOnlyList<string> findings,
        int priorReissues, int maxReissues, string jobId, int? targetRunAttempt = null,
        string? executionError = null, DateTime? now = null)
        => AgentStudio.OrchestrationDecisions.CouncilDecisionPolicy.Derive(
            reviewFileName, grade is null ? "?" : CodeReviewGradeParsing.GradeToken(grade.Value),
            findings, priorReissues, maxReissues, jobId, targetRunAttempt, executionError, now ?? DateTime.UtcNow);

    /// <summary>
    /// The implementer's next-round prompt. Finding text is reviewer output, so
    /// it is quoted as data inside a nonce fence (<see cref="ReviewFindingDataBlock"/>)
    /// and never spliced into the directions around it.
    /// </summary>
    public static string BuildTargetedFollowUp(CouncilReviewReaction reaction, string? nonce = null)
    {
        var findings = reaction.Assessments
            .Where(a => a.Action == CouncilFindingAction.FixNextRound)
            .Select(a => "- " + Regex.Replace(a.Finding, @"\s+", " ").Trim())
            .ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append("Council reaction to quality grade ").Append(reaction.Grade).AppendLf(": fix the named findings below.");
        sb.AppendLf("Do not redo unrelated work. For each finding, implement the fix and add the focused test or evidence that proves it.");
        sb.AppendLf(ReviewFindingDataBlock.Render(findings, nonce));
        sb.AppendLf();
        sb.AppendLf("Finish with a concise verification summary and the required terminal sentinel.");
        return sb.ToString().TrimEnd();
    }

    public static CouncilReviewReaction EscalateBecause(CouncilReviewReaction reaction, string reason)
        => AgentStudio.OrchestrationDecisions.CouncilDecisionPolicy.EscalateBecause(reaction, reason);

}

public static class CouncilReviewReactionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathFor(string jobFolderPath, string reviewFileName)
        => Path.Combine(jobFolderPath, reviewFileName + ".council-reaction.json");

    public static void Write(string jobFolderPath, CouncilReviewReaction reaction)
    {
        Directory.CreateDirectory(jobFolderPath);
        File.WriteAllText(PathFor(jobFolderPath, reaction.ReviewFileName), JsonSerializer.Serialize(reaction, Json));
    }

    public static CouncilReviewReaction? Read(string jobFolderPath, string reviewFileName)
    {
        var path = PathFor(jobFolderPath, reviewFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CouncilReviewReaction>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
