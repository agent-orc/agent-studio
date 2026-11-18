using AgentStudio.QualityStudio;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Deploy;

/// <summary>
/// One card's verdict. <see cref="Source"/> is <c>quality-studio</c> or
/// <c>review</c> (the card's latest Remote Review or local grade);
/// <see cref="Reason"/> says why a card has no verdict.
/// </summary>
public sealed record ReleaseQualityCard(string Key, string Verdict, string? Source = null, string? Reason = null);
public sealed record ReleaseQualityRange(string RepositoryId, string BaseSha, string HeadSha,
    bool RequiresConfirmation);
public sealed record ReleaseQuality(IReadOnlyList<ReleaseQualityCard> Cards, int Pass, int Concerns,
    int Block, int NotReviewed, string WorstVerdict, string RangeVerdict, ReleaseQualityRange? Range,
    string? RangeUnavailableReason);

/// <summary>Read-only release view. Counts are derived from the same card rows sent to the UI.</summary>
public static class ReleaseQualityProjection
{
    public static IReadOnlyList<Release> Project(IReadOnlyList<Release> releases,
        IEnumerable<TaskInfo> tasks, string? repositoryId, Func<string, string, string?> mergeBase,
        IReadOnlyList<Release>? visible = null, Func<string, string?>? rootCommit = null,
        Func<string, string, string, string?>? storedVerdict = null,
        Func<TaskInfo, ReviewVerdict?>? reviewVerdict = null)
    {
        reviewVerdict ??= LatestReview;
        var ordered = releases.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        visible ??= releases;
        var visibleIds = visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var byKey = tasks.GroupBy(task => string.IsNullOrWhiteSpace(task.Key) ? task.TaskKey : task.Key!,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var views = new Dictionary<string, Release>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
        {
            var release = ordered[index];
            if (!visibleIds.Contains(release.Id)) continue;
            var cards = release.CardKeys.Select(key => byKey.TryGetValue(key, out var task)
                ? CurrentVerdict(key, task, reviewVerdict)
                : new ReleaseQualityCard(key, "not-reviewed", Reason: "The card is not on this project's board.")).ToArray();
            var pass = cards.Count(card => card.Verdict == "pass");
            var concerns = cards.Count(card => card.Verdict == "concerns");
            var block = cards.Count(card => card.Verdict == "block");
            var range = string.IsNullOrWhiteSpace(repositoryId) ? null
                : index > 0 ? mergeBase(ordered[index - 1].CommitSha, release.CommitSha)
                : rootCommit?.Invoke(release.CommitSha);
            views[release.Id] = release with { Quality = new ReleaseQuality(cards, pass, concerns, block,
                cards.Length - pass - concerns - block,
                block > 0 ? "block" : concerns > 0 ? "concerns" : pass > 0 ? "pass" : "not-reviewed",
                range is null ? "not-reviewed" : NormalizeVerdict(storedVerdict?.Invoke(repositoryId!, range, release.CommitSha)),
                range is null ? null : new ReleaseQualityRange(repositoryId!, range, release.CommitSha, index == 0),
                range is not null ? null : RangeUnavailable(repositoryId, index)) };
        }
        return visible.Select(release => views[release.Id]).ToArray();
    }

    private static string NormalizeVerdict(string? verdict)
        => verdict is "pass" or "concerns" or "block" ? verdict : "not-reviewed";

    private static string RangeUnavailable(string? repositoryId, int index)
        => string.IsNullOrWhiteSpace(repositoryId)
            ? "The release range is not reviewed because no Quality Studio repository is linked to this project."
            : index == 0
                ? "The first release has no earlier release to compare with, and the repository's first commit could not be read."
                : "The release range cannot be read: the commits of this release and the previous one are not in the linked repository.";

    /// <summary>A Quality Studio change review wins; without one, the card's latest review verdict counts.</summary>
    private static ReleaseQualityCard CurrentVerdict(string key, TaskInfo task, Func<TaskInfo, ReviewVerdict?> reviewVerdict)
    {
        var latest = QualityChangeReviewStore.Read(task.FolderPath).LastOrDefault();
        if (latest is not null)
        {
            var head = AgentStudio.Pipeline.ReviewSubjectStore.Read(task.FolderPath)?.ResultSha;
            var state = QualityChangeReviewProjection.State(latest.RepositoryId, head, latest, task.State);
            if (state is "pass" or "concerns" or "block") return new(key, state, "quality-studio");
        }
        var review = reviewVerdict(task);
        if (review?.Verdict is { } verdict) return new(key, verdict, "review");
        return new(key, "not-reviewed", Reason: review?.Reason
            ?? (latest is null ? "The card has no recorded review." : "The card's Quality Studio review is not current."));
    }

    public sealed record ReviewVerdict(string? Verdict, string? Reason = null);

    /// <summary>Maps the newest review attempt: a Remote Review outcome or a local grade verdict.</summary>
    internal static ReviewVerdict? LatestReview(TaskInfo task)
    {
        var attempt = AgentStudio.Review.ReviewProjectionReader.Read(task, [], null).Attempts.FirstOrDefault();
        return attempt is null ? null : VerdictForOutcome(attempt.Outcome);
    }

    internal static ReviewVerdict VerdictForOutcome(string? outcome) => outcome?.Trim() switch
    {
        var value when string.Equals(value, "pass", StringComparison.OrdinalIgnoreCase) => new("pass"),
        var value when string.Equals(value, "concerns", StringComparison.OrdinalIgnoreCase) => new("concerns"),
        var value when string.Equals(value, "block", StringComparison.OrdinalIgnoreCase) => new("block"),
        nameof(ReviewTerminalOutcome.ProductFailure) => new("block"),
        nameof(ReviewTerminalOutcome.IntegrationBranchDefect) => new("concerns"),
        _ => new(null, string.IsNullOrWhiteSpace(outcome)
            ? "The card's latest review has no verdict yet."
            : $"The card's latest review ended without a verdict ({outcome.Trim()})."),
    };
}
