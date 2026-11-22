namespace AgentStudio.Deploy;

/// <summary>A commit of a range with its parents, first parent first.</summary>
public sealed record RangeCommit(string Sha, IReadOnlyList<string> Parents, string Message);

/// <summary>A card and the first-parent commit of the range that first brought it.</summary>
public sealed record CardLanding(string Key, string Title, string LandedSha);

/// <summary>
/// Maps a commit range to the cards it lands (AGT-3879). The range is walked
/// along the first-parent chain of its tip, oldest first; each first-parent
/// commit owns itself and every commit of the range it alone makes reachable,
/// such as the side of a batch gate merge. A card lands at the first
/// first-parent commit that owns one of its integration commits or whose owned
/// messages name its key. Pure.
/// </summary>
public static class InstanceRangeCards
{
    public static IReadOnlyList<CardLanding> Map(IReadOnlyList<RangeCommit> range, string tip,
        IReadOnlyList<IntegratedCardRecord> cards, IReadOnlySet<string>? excludedKeys = null)
    {
        var byId = new Dictionary<string, RangeCommit>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in range) byId.TryAdd(commit.Sha, commit);
        var chain = new List<RangeCommit>();
        for (var current = tip; current is not null && byId.TryGetValue(current, out var commit);
             current = commit.Parents.Count > 0 ? commit.Parents[0] : null)
        {
            if (chain.Count > byId.Count) break;
            chain.Add(commit);
        }
        chain.Reverse();

        var candidates = cards.Where(card => excludedKeys?.Contains(card.Key) != true)
            .GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Key: group.Key, Title: group.First().Title,
                Shas: group.SelectMany(card => card.Shas).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var landings = new List<CardLanding>();
        foreach (var landing in chain)
        {
            var owned = new List<RangeCommit>();
            var pending = new Stack<string>();
            pending.Push(landing.Sha);
            while (pending.Count > 0)
            {
                var sha = pending.Pop();
                if (!byId.TryGetValue(sha, out var commit) || !assigned.Add(sha)) continue;
                owned.Add(commit);
                foreach (var parent in commit.Parents) pending.Push(parent);
            }
            if (candidates.Count == 0) continue;
            var shas = owned.Select(item => item.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mentioned = ReleaseCardAttribution.FindKeys(owned.Select(item => item.Message));
            var landed = candidates.Where(card => card.Shas.Overlaps(shas) || mentioned.Contains(card.Key))
                .OrderBy(card => card.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var card in landed)
            {
                landings.Add(new CardLanding(card.Key, card.Title, landing.Sha));
                candidates.Remove(card);
            }
        }
        return landings;
    }
}
