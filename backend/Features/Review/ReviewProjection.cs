using System.Globalization;

namespace AgentStudio.Review;

/// <summary>Which review plane produced a <see cref="ReviewAttempt"/>.</summary>
public enum ReviewPlane
{
    /// <summary>The user-triggered / automatic local quality-grade step (<c>code-review-grade-*.md</c>).</summary>
    Local,

    /// <summary>The fenced Remote Review pipeline (<c>remote-review-grade-*.md</c>).</summary>
    Remote,
}

/// <summary>One row of a Remote Review attempt's aspect verdict table.</summary>
public sealed record ReviewAspectVerdict
{
    public required string Aspect { get; init; }
    public required string Status { get; init; }
    public string Classification { get; init; } = "";
    public string Summary { get; init; } = "";
}

/// <summary>
/// One review round, from either plane, normalized to a single shape (AGT-2717).
/// Every review surface (escalation banner, Evidence tab, Result header, board
/// chip) renders off <see cref="ReviewProjectionView"/> instead of parsing its
/// own subset of Markdown, so the four surfaces cannot disagree.
/// </summary>
public sealed record ReviewAttempt
{
    public required ReviewPlane Plane { get; init; }
    public required string AttemptId { get; init; }
    public DateTime? ReceivedAt { get; init; }

    /// <summary>
    /// Remote plane: the report's <c>outcome</c> frontmatter field (e.g.
    /// <c>Pass</c>, <c>ProductFailure</c>). Local plane: the grade step's
    /// <c>verdict</c> field (<c>pass</c>/<c>concerns</c>/<c>block</c>).
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>Quality grade A-D for a local grade attempt; null otherwise.</summary>
    public string? Grade { get; init; }

    /// <summary>One of <c>passed</c>/<c>failed</c>/<c>not-proven</c>, same vocabulary as <see cref="AgentStudio.TestRuns.TaskTestEvidenceSource.Result"/>.</summary>
    public required string BuildTestsResult { get; init; }
    public string? BuildTestsReason { get; init; }

    /// <summary>Empty for a local attempt; the local grade step carries no per-aspect breakdown.</summary>
    public IReadOnlyList<ReviewAspectVerdict> Aspects { get; init; } = [];

    public string? SubjectSha { get; init; }
    public required string ReportRef { get; init; }
}

/// <summary>A blocking aspect on the latest review attempt, with its quoted reason.</summary>
public sealed record ReviewProjectionBlockingAspect
{
    public required string Aspect { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Closed vocabulary for <see cref="ReviewDeliveryState.Status"/>.</summary>
public static class ReviewDeliveryStates
{
    public const string Integrated = "integrated";
    public const string GateFailed = "gate-failed";
    public const string NotAttempted = "not-attempted";
}

/// <summary>Is this task's reviewed work actually in develop/main, derived from the integration timeline.</summary>
public sealed record ReviewDeliveryState
{
    public required string Status { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Whether a human decision is pending on this card, and where that fact came from.</summary>
public sealed record ReviewDecisionRequired
{
    public required bool Required { get; init; }

    /// <summary>One of <c>parked-blocker</c>, <c>escalation-event</c>, <c>lane-change</c>, or null.</summary>
    public string? Source { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Canonical, task-scoped projection of every review attempt across both
/// planes (AGT-2717). Computed at read time from the artifacts each plane
/// already writes; nothing here is persisted separately.
/// </summary>
public sealed record ReviewProjectionView
{
    /// <summary>Newest first.</summary>
    public IReadOnlyList<ReviewAttempt> Attempts { get; init; } = [];

    public int Rounds { get; init; }
    public string? LatestPlane { get; init; }
    public string? LatestOutcome { get; init; }
    public DateTime? LatestReceivedAt { get; init; }

    /// <summary>Non-build-tests aspects blocking the latest attempt, empty when the latest attempt passed clean.</summary>
    public IReadOnlyList<ReviewProjectionBlockingAspect> BlockingAspects { get; init; } = [];

    public ReviewDeliveryState Delivery { get; init; } = new() { Status = ReviewDeliveryStates.NotAttempted };
    public ReviewDecisionRequired DecisionRequired { get; init; } = new() { Required = false };

    public static ReviewProjectionView Empty { get; } = new();
}

/// <summary>Plain facts parsed from one <c>code-review-grade-*.md</c> local attempt.</summary>
internal sealed record LocalReviewAttemptFacts(
    string AttemptId,
    string ReportRef,
    string? Commit,
    DateTime ObservedAt,
    string Verdict,
    string? Grade);

/// <summary>
/// Pure read-time merge of the local and remote review planes into one
/// <see cref="ReviewProjectionView"/> (AGT-2717). Reuses
/// <see cref="TaskScopedTestEvidenceReader.ReadRemoteReviewAttempts"/> for the
/// remote plane's build-tests reading rule instead of re-parsing the report.
/// </summary>
public static class ReviewProjectionReader
{
    public static ReviewProjectionView Read(
        TaskInfo task,
        IReadOnlyList<TimelineEvent> timeline,
        ParkedBlockerRecord? parkedBlocker)
    {
        var attempts = new List<ReviewAttempt>();
        attempts.AddRange(TaskScopedTestEvidenceReader.ReadRemoteReviewAttempts(task).Select(ToAttempt));
        attempts.AddRange(ReadLocalReviewAttempts(task).Select(ToAttempt));
        attempts = attempts
            .OrderByDescending(attempt => attempt.ReceivedAt ?? DateTime.MinValue)
            .ToList();

        var latest = attempts.FirstOrDefault();
        var blocking = latest?.Aspects
            .Where(aspect => !aspect.Aspect.Equals("build-tests", StringComparison.OrdinalIgnoreCase)
                              && TaskScopedTestEvidenceReader.IsBlocking(aspect.Status))
            .Select(aspect => new ReviewProjectionBlockingAspect
            {
                Aspect = aspect.Aspect,
                Reason = TrimSentence(aspect.Summary),
            })
            .ToList() ?? [];

        return new ReviewProjectionView
        {
            Attempts = attempts,
            Rounds = attempts.Count,
            LatestPlane = latest?.Plane == ReviewPlane.Remote ? "remote" : latest?.Plane == ReviewPlane.Local ? "local" : null,
            LatestOutcome = latest?.Outcome,
            LatestReceivedAt = latest?.ReceivedAt,
            BlockingAspects = blocking,
            Delivery = ResolveDelivery(timeline),
            DecisionRequired = ResolveDecisionRequired(timeline, parkedBlocker),
        };
    }

    private static ReviewAttempt ToAttempt(RemoteReviewAttemptFacts fact) => new()
    {
        Plane = ReviewPlane.Remote,
        AttemptId = fact.AttemptId,
        ReceivedAt = fact.ObservedAt,
        Outcome = fact.Outcome,
        Grade = null,
        BuildTestsResult = fact.BuildResult,
        BuildTestsReason = fact.BuildReason,
        Aspects = fact.VerdictRows
            .Select(row => new ReviewAspectVerdict
            {
                Aspect = row.Aspect,
                Status = row.Status,
                Classification = row.Classification,
                Summary = row.Summary,
            })
            .ToList(),
        SubjectSha = fact.Commit,
        ReportRef = fact.ReportRef,
    };

    private static ReviewAttempt ToAttempt(LocalReviewAttemptFacts fact) => new()
    {
        Plane = ReviewPlane.Local,
        AttemptId = fact.AttemptId,
        ReceivedAt = fact.ObservedAt,
        Outcome = fact.Verdict,
        Grade = fact.Grade,
        BuildTestsResult = "not-proven",
        BuildTestsReason = null,
        Aspects = [],
        SubjectSha = string.IsNullOrWhiteSpace(fact.Commit) ? null : fact.Commit,
        ReportRef = fact.ReportRef,
    };

    /// <summary>
    /// Every <c>code-review-grade-*.md</c> local attempt in the task folder.
    /// Uses the same generic <see cref="AgentStudio.Cli.FrontmatterParser"/>
    /// the <c>code-review/list</c> endpoint reads with, so this is a second
    /// read of the same primitive, not a second frontmatter grammar.
    /// </summary>
    private static IReadOnlyList<LocalReviewAttemptFacts> ReadLocalReviewAttempts(TaskInfo task)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath) || !Directory.Exists(task.FolderPath))
            return [];

        var facts = new List<LocalReviewAttemptFacts>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         task.FolderPath,
                         "code-review-grade-*.md",
                         SearchOption.TopDirectoryOnly))
            {
                if (ParseLocalAttempt(path) is { } fact) facts.Add(fact);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReviewProjectionReader: folder enumeration failure reading code-review-grade attempts");
        }

        return facts.OrderByDescending(fact => fact.ObservedAt).ToList();
    }

    private static LocalReviewAttemptFacts? ParseLocalAttempt(string path)
    {
        string content;
        try { content = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var frontmatter = AgentStudio.Cli.FrontmatterParser.Parse(content);
        if (!frontmatter.Ok) return null;
        var fields = frontmatter.Fields;

        var observedAt = DateTime.TryParse(
            fields.GetValueOrDefault("runAt"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : File.GetLastWriteTimeUtc(path);

        return new LocalReviewAttemptFacts(
            AttemptId: Path.GetFileNameWithoutExtension(path),
            ReportRef: Path.GetFileName(path),
            Commit: fields.GetValueOrDefault("commit"),
            ObservedAt: observedAt,
            Verdict: fields.GetValueOrDefault("verdict") ?? "unknown",
            Grade: fields.GetValueOrDefault("grade"));
    }

    /// <summary>
    /// Honest delivery state from the integration timeline: the most recent of
    /// <c>integration_succeeded</c> / <c>integration_failed</c> /
    /// <c>integration_overridden</c> decides, so a later successful retry after
    /// a failed gate reads as integrated. Never attempted when none exist.
    /// </summary>
    private static ReviewDeliveryState ResolveDelivery(IReadOnlyList<TimelineEvent> timeline)
    {
        var latest = timeline
            .Where(evt => evt.Kind is TimelineEventKinds.IntegrationSucceeded
                or TimelineEventKinds.IntegrationFailed
                or TimelineEventKinds.IntegrationOverridden)
            .OrderByDescending(evt => evt.Ts)
            .FirstOrDefault();
        if (latest is null) return new ReviewDeliveryState { Status = ReviewDeliveryStates.NotAttempted };

        if (latest.Kind == TimelineEventKinds.IntegrationFailed)
        {
            var reason = latest.Details?.GetValueOrDefault("reason");
            reason = string.IsNullOrWhiteSpace(reason) ? latest.Summary : reason;
            return new ReviewDeliveryState
            {
                Status = ReviewDeliveryStates.GateFailed,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            };
        }

        return new ReviewDeliveryState { Status = ReviewDeliveryStates.Integrated };
    }

    /// <summary>
    /// Whether a human decision is outstanding: an operator-decision park wins
    /// (it is the most deliberate signal), then the newest unresolved
    /// escalation, then a lane move into Human Review.
    /// </summary>
    private static ReviewDecisionRequired ResolveDecisionRequired(
        IReadOnlyList<TimelineEvent> timeline,
        ParkedBlockerRecord? parkedBlocker)
    {
        if (parkedBlocker is not null)
        {
            return new ReviewDecisionRequired
            {
                Required = true,
                Source = "parked-blocker",
                Reason = string.IsNullOrWhiteSpace(parkedBlocker.Reason) ? null : parkedBlocker.Reason,
            };
        }

        var escalation = timeline
            .Where(evt => evt.Kind == TimelineEventKinds.OrchestratorEscalated)
            .OrderByDescending(evt => evt.Ts)
            .FirstOrDefault();
        if (escalation is not null)
        {
            return new ReviewDecisionRequired
            {
                Required = true,
                Source = "escalation-event",
                Reason = string.IsNullOrWhiteSpace(escalation.Summary) ? null : escalation.Summary,
            };
        }

        var laneChange = timeline
            .Where(evt => evt.Kind == TimelineEventKinds.LaneChanged
                          && string.Equals(evt.Details?.GetValueOrDefault("to"), TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(evt => evt.Ts)
            .FirstOrDefault();
        if (laneChange is not null)
        {
            var reason = laneChange.Details?.GetValueOrDefault("reason");
            reason = string.IsNullOrWhiteSpace(reason) ? laneChange.Summary : reason;
            return new ReviewDecisionRequired
            {
                Required = true,
                Source = "lane-change",
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            };
        }

        return new ReviewDecisionRequired { Required = false };
    }

    private static string TrimSentence(string value) => value.Trim().TrimEnd('.', ';', ':');
}
