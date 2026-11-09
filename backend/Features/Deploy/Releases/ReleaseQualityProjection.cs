using AgentStudio.QualityStudio;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Deploy;

public sealed record ReleaseQualityCard(string Key, string Verdict);
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
        Func<string, string, string, string?>? storedVerdict = null)
    {
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
            var cards = release.CardKeys.Select(key => new ReleaseQualityCard(key,
                byKey.TryGetValue(key, out var task) ? CurrentVerdict(task) : "not-reviewed")).ToArray();
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
                range is not null ? null : index == 0 ? "The repository root range is unavailable."
                    : "The release range is unavailable until the repository mapping and commits can be read.") };
        }
        return visible.Select(release => views[release.Id]).ToArray();
    }

    private static string NormalizeVerdict(string? verdict)
        => verdict is "pass" or "concerns" or "block" ? verdict : "not-reviewed";

    private static string CurrentVerdict(TaskInfo task)
    {
        var latest = QualityChangeReviewStore.Read(task.FolderPath).LastOrDefault();
        if (latest is null) return "not-reviewed";
        var head = AgentStudio.Pipeline.ReviewSubjectStore.Read(task.FolderPath)?.ResultSha;
        var state = QualityChangeReviewProjection.State(latest.RepositoryId, head, latest, task.State);
        return state is "pass" or "concerns" or "block" ? state : "not-reviewed";
    }
}
