namespace AgentStudio.Deploy;

public sealed record StudioReleaseCard(string Key, string Title, string? Type, string Href,
    IReadOnlyList<string> Shas, DateTime? IntegratedAtUtc, string? IntegrationSha = null);
public sealed record StudioRevision(string Revision, DateTime AtUtc, bool Observed, string? TimeBasis = null,
    string? OccurrenceId = null);
public sealed record StudioReleaseEntry(string Revision, DateTime AtUtc, bool Observed,
    bool Running, IReadOnlyList<StudioReleaseCard> Cards, string OccurrenceId, string? TimeBasis = null);
public sealed record StudioCardLiveState(string State, string? Revision = null, DateTime? SinceUtc = null);
public sealed record StudioReleasesView(string Project, string RunningRevision, DateTime ComputedAtUtc,
    IReadOnlyList<StudioReleaseCard> Pending, IReadOnlyList<StudioReleaseEntry> Releases,
    IReadOnlyDictionary<string, StudioCardLiveState> CardStates, string? Detail = null);

/// <summary>Delivery membership is ancestry, never a task key in a commit message or a timestamp comparison.</summary>
public static class StudioReleaseProjection
{
    public static StudioReleasesView Build(string project, string running, string target, DateTime now,
        IReadOnlyList<StudioRevision> revisions, IReadOnlyList<StudioReleaseCard> cards,
        Func<string, string, bool?> ancestor)
    {
        var membership = new Dictionary<(string Key, string Revision), bool?>();
        var incomplete = false;
        bool? Contains(StudioReleaseCard card, string revision)
        {
            var key = (card.Key, revision);
            if (membership.TryGetValue(key, out var cached)) return cached;
            var mapped = card.IntegrationSha is null ? false : ancestor(card.IntegrationSha, revision);
            var sources = card.Shas.Select(sha => ancestor(sha, revision)).ToArray();
            bool? contained = mapped == true || sources.Length > 0 && sources.All(value => value == true)
                ? true : mapped is null ? null : sources.Any(value => value == false) ? false : null;
            if (contained is null) incomplete = true;
            membership[key] = contained;
            return contained;
        }
        // Stable ordering preserves the source sequence when timestamps are equal.
        var ordered = revisions.OrderBy(revision => revision.AtUtc).ToArray();
        var current = Array.FindLastIndex(ordered,
            revision => string.Equals(revision.Revision, running, StringComparison.OrdinalIgnoreCase));
        var uniqueCards = cards.GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = ordered.Select((revision, index) => new StudioReleaseEntry(
            revision.Revision, revision.AtUtc, revision.Observed,
            index == current,
            index == 0 ? [] : uniqueCards.Where(card => Contains(card, revision.Revision) == true
                && Contains(card, ordered[index - 1].Revision) == false).ToArray(),
            revision.OccurrenceId ?? $"legacy:{index}:{revision.AtUtc.Ticks}:{revision.Revision}", revision.TimeBasis)).ToArray();
        var states = new Dictionary<string, StudioCardLiveState>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<StudioReleaseCard>();
        foreach (var card in uniqueCards)
        {
            var live = Contains(card, running);
            if (live == true)
            {
                // Walk only the current uninterrupted interval. A rollback can remove
                // a delivery even when an older occurrence had already contained it.
                var start = current;
                while (start > 0 && Contains(card, ordered[start - 1].Revision) == true) start--;
                var first = start < 0 ? null : ordered[start];
                states[card.Key] = new("live", first?.Revision ?? running,
                    first?.TimeBasis == "build-baseline" ? null : first?.AtUtc);
            }
            else if (live == false && Contains(card, target) == true)
            {
                states[card.Key] = new("integrated-not-live");
                pending.Add(card);
            }
            else states[card.Key] = new("unknown");
        }
        return new(project, running, now,
            pending.OrderBy(card => card.IntegratedAtUtc ?? DateTime.MaxValue).ThenBy(card => card.Key).ToArray(),
            entries, states, incomplete ? "Some deliveries could not be resolved from the available Git history." : null);
    }
}
