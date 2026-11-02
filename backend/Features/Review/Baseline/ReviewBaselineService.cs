using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Tasks;

namespace AgentStudio.Review;

/// <summary>
/// Reads the evidence of the S2 review baseline for one project: each task's
/// pipeline record and review observation journal, the cycle-time rows of the
/// window, and the material size from the published Git facts. A read starts
/// no Git process; a commit the fact store has not published leaves the
/// material unknown.
/// </summary>
public sealed class ReviewBaselineService(
    TaskScannerService scanner, PipelineExecutionLog pipeline, ITokenPricing pricing,
    ProjectCycleTimeService cycleTime, GitCardFactReader gitFacts, TimeProvider clock)
{
    public ReviewBaselineReport Build(string project, string watchPath, int days)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var reviewOther = (cycleTime.Build(project, $"{days}d", now)?.Tasks ?? [])
            .Where(row => row.Stages is not null)
            .GroupBy(row => row.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Stages!.ReviewOther, StringComparer.OrdinalIgnoreCase);
        var facts = gitFacts.Read(watchPath).Facts;
        var cards = new List<ReviewBaselineCard>();
        foreach (var task in scanner.ScanAllAutomationJobsWithArchive())
        {
            if (!string.Equals(task.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)) continue;
            var samples = new List<ReviewStepSample>();
            if (pipeline.Read(task.FolderPath) is { } record)
                samples.AddRange(ReviewStepSampleReader.Read(task.Id, record, pricing));
            samples.AddRange(ReviewObservationStore.Read(task.FolderPath).Reviews.Select(ObservationSample)
                .Where(sample => sample is not null).Select(sample => sample!));
            if (samples.Count == 0) continue;
            var (material, bySha) = facts is null
                ? (null, new Dictionary<string, ReviewMaterialSize>(StringComparer.OrdinalIgnoreCase))
                : MaterialSizes(task.Commits, facts.Catalog.Commit);
            cards.Add(new ReviewBaselineCard(task.Id, string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key,
                task.Dossier?.Path, samples,
                reviewOther.TryGetValue(task.Id, out var seconds) ? seconds : null, material, bySha));
        }
        return ReviewBaselineFold.Build(project, days, now, cards);
    }

    /// <summary>
    /// A review recorded through the observation API. A journal row with no
    /// token counts or no duration reported them as unknown, not as zero.
    /// </summary>
    internal static ReviewStepSample? ObservationSample(ReviewObservation review)
    {
        var step = ReviewSteps.Of(review.Aspect) ?? review.Level switch
        {
            "grade" => ReviewSteps.Grade,
            "deep" => "deep-review",
            _ => null,
        };
        if (step is null) return null;
        var tokens = new ReviewStepTokens(review.InputTokens, review.OutputTokens,
            review.CacheReadTokens, review.CacheCreationTokens);
        return new ReviewStepSample(review.JobId, review.ReviewRoundId ?? review.Id, step,
            review.ReviewedAt.ToUniversalTime(), review.DurationMs > 0 ? review.DurationMs / 60000d : null,
            tokens.Prompt + tokens.Output > 0 ? tokens : null, ReviewObservationPolicy.Approved(review),
            review.ResultSha, review.FilesReviewed);
    }

    /// <summary>
    /// Material size of the card and of each delivered SHA: changed files and
    /// added plus removed lines of the attributed commits up to that SHA, in
    /// attribution order. Superseded commits are left out. Unknown (null) when
    /// the card has no commits or one of them is not published yet.
    /// </summary>
    /// <summary>
    /// The card's changed paths and added plus removed lines over the same
    /// attributed commits as <see cref="MaterialSizes"/>; package formation
    /// unions the paths of its members. Null when a commit is not published.
    /// </summary>
    internal static (IReadOnlyList<string> Paths, int Lines)? MaterialPaths(
        IReadOnlyList<TaskCommitInfo> commits, Func<string, GitCommitFact?> lookup)
    {
        var active = commits.Where(commit => !TaskCommitSupersession.IsSuperseded(commit)
            && !string.IsNullOrWhiteSpace(commit.Sha)).ToList();
        if (active.Count == 0) return null;
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var lines = 0;
        foreach (var commit in active)
        {
            if (lookup(commit.Sha) is not { } fact) return null;
            foreach (var file in fact.Files) paths.Add(file.Path);
            lines += fact.Added + fact.Removed;
        }
        return (paths.ToArray(), lines);
    }

    internal static (ReviewMaterialSize? Card, IReadOnlyDictionary<string, ReviewMaterialSize> BySha) MaterialSizes(
        IReadOnlyList<TaskCommitInfo> commits, Func<string, GitCommitFact?> lookup)
    {
        var bySha = new Dictionary<string, ReviewMaterialSize>(StringComparer.OrdinalIgnoreCase);
        var active = commits.Where(commit => !TaskCommitSupersession.IsSuperseded(commit)
            && !string.IsNullOrWhiteSpace(commit.Sha)).ToList();
        if (active.Count == 0) return (null, bySha);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var lines = 0;
        foreach (var commit in active)
        {
            if (lookup(commit.Sha) is not { } fact) return (null, bySha);
            foreach (var file in fact.Files) paths.Add(file.Path);
            lines += fact.Added + fact.Removed;
            var size = new ReviewMaterialSize(paths.Count, lines);
            bySha[commit.Sha] = size;
            if (!string.IsNullOrWhiteSpace(commit.ResultSha)) bySha[commit.ResultSha] = size;
        }
        return (new ReviewMaterialSize(paths.Count, lines), bySha);
    }
}
