namespace AgentStudio.Areas;

/// <summary>
/// The shared <c>area</c> / <c>tag</c> filter of the classification vocabulary
/// (AGT-2803). Both parameters accept a comma-separated list. Within one
/// parameter the ids are alternatives; across the two parameters they are a
/// conjunction, so <c>?area=delivery-chain&amp;tag=incident</c> means "in the
/// delivery chain area and marked as an incident".
///
/// Pure by construction: the filter never reads the registry, so an id that is
/// no longer registered simply matches nothing instead of failing a list read.
/// </summary>
public sealed record TagFilter(IReadOnlyList<string> Areas, IReadOnlyList<string> Tags)
{
    public static readonly TagFilter None = new([], []);

    public bool IsActive => Areas.Count > 0 || Tags.Count > 0;

    public static TagFilter FromQuery(IQueryCollection query) =>
        new(Csv(query, "area"), Csv(query, "tag"));

    public static TagFilter Parse(string? area, string? tag) =>
        new(Split(area), Split(tag));

    /// <summary>True when the item's tag ids satisfy the filter.</summary>
    public bool Matches(IEnumerable<string>? itemTags)
    {
        if (!IsActive) return true;
        var tags = (itemTags ?? []).ToList();
        return MatchesAny(Areas, tags) && MatchesAny(Tags, tags);
    }

    private static bool MatchesAny(IReadOnlyList<string> requested, List<string> tags) =>
        requested.Count == 0
        || requested.Any(id => tags.Any(tag => string.Equals(tag, id, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> Csv(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values)
            ? [.. values.SelectMany(value => Split(value))]
            : [];

    private static List<string> Split(string? value) =>
        [.. (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}
