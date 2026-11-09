namespace AgentStudio.Pipeline;

/// <summary>Membership choice for formed batches that have not entered a gate.</summary>
public static class BatchGateWaitingPolicy
{
    public sealed record Card(string TaskKey, IReadOnlyList<string>? Paths);

    public static IReadOnlyList<Card> LargestCompatible(IReadOnlyList<Card> ordered,
        int maximumSize, string overlap)
    {
        if (overlap != "separate") return ordered.Take(maximumSize).ToArray();
        var best = new List<Card>();
        var chosen = new List<Card>();
        Search(0);
        return best;

        void Search(int index)
        {
            if (chosen.Count > best.Count) best = chosen.ToList();
            if (best.Count == maximumSize || index == ordered.Count
                || chosen.Count + Math.Min(maximumSize - chosen.Count,
                    ordered.Count - index) <= best.Count) return;
            var card = ordered[index];
            if (chosen.All(other => ConflictingFiles(card, other).Count == 0))
            {
                chosen.Add(card);
                Search(index + 1);
                chosen.RemoveAt(chosen.Count - 1);
            }
            Search(index + 1);
        }
    }

    public static IReadOnlyList<string> ConflictingFiles(Card left, Card right)
    {
        // An unavailable diff must not silently qualify for separate overlap.
        if (left.Paths is null || right.Paths is null) return ["changed files unavailable"];
        return left.Paths.Intersect(right.Paths, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
