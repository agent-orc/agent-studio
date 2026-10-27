using System.Security.Cryptography;

namespace AgentStudio.Tasks;

/// <summary>
/// One persisted board card: the serialized <see cref="BoardCardSummary"/> and
/// where it sits on the board. <see cref="LaneRank"/> is its position inside
/// its lane under the project's sort; <see cref="ScanRank"/> its position in
/// the list route, which keeps index order.
/// </summary>
public sealed record BoardSummaryCard(
    string ProjectName,
    string TaskKey,
    string WatchPath,
    string State,
    bool Fixture,
    int LaneRank,
    int ScanRank,
    byte[] Summary);

/// <summary>
/// The persisted board of one project: its cards in index order, the task
/// index generation they were built from, when they were persisted and the
/// Git freshness stamp the board carried.
/// </summary>
public sealed record BoardSummaryProject(
    string ProjectName,
    long IndexGeneration,
    DateTimeOffset PersistedAt,
    DateTimeOffset? GitStateAt,
    bool GitStale,
    string ContentHash,
    IReadOnlyList<BoardSummaryCard> Cards);

/// <summary>
/// Pure decisions of the persisted board (S5): how a project's enriched cards
/// become persisted rows, which projects a write must replace, and how a
/// restored read selects and composes its body. No clock, store or request.
/// </summary>
public static class BoardSummaryPolicy
{
    /// <summary>
    /// Builds one project's rows. <paramref name="cards"/> are enriched and in
    /// index order, with their index positions in <paramref name="scanRanks"/>;
    /// <paramref name="sortLane"/> is the board's lane sort over the same cards.
    /// </summary>
    public static BoardSummaryProject Build(
        string projectName,
        long indexGeneration,
        DateTimeOffset persistedAt,
        DateTimeOffset? gitStateAt,
        bool gitStale,
        IReadOnlyList<TaskInfo> cards,
        IReadOnlyList<int> scanRanks,
        Func<string, IReadOnlyList<TaskInfo>> sortLane,
        Func<TaskInfo, byte[]> serialize)
    {
        var laneRanks = new Dictionary<TaskInfo, int>(ReferenceEqualityComparer.Instance);
        foreach (var state in cards.Select(card => card.State).Distinct(StringComparer.Ordinal))
        {
            var sorted = sortLane(state);
            for (var rank = 0; rank < sorted.Count; rank++) laneRanks.TryAdd(sorted[rank], rank);
        }
        var rows = cards.Select((card, index) => new BoardSummaryCard(
            projectName, card.TaskKey, card.WatchPath, card.State, card.Fixture,
            laneRanks.GetValueOrDefault(card, int.MaxValue), scanRanks[index], serialize(card))).ToList();
        return new BoardSummaryProject(projectName, indexGeneration, persistedAt, gitStateAt, gitStale,
            ContentHash(rows, gitStateAt, gitStale), rows);
    }

    /// <summary>
    /// Digest of what a restored read of the project would serve: the cards
    /// in index order with lane, rank, fixture flag and bytes, and the Git
    /// stamp. The absolute index position is left out, so a card moving in
    /// another project does not rewrite this one.
    /// </summary>
    public static string ContentHash(IEnumerable<BoardSummaryCard> cards, DateTimeOffset? gitStateAt, bool gitStale)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string value)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
        Text(gitStateAt?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        Text(gitStale ? "stale" : "fresh");
        foreach (var card in cards)
        {
            Text(card.TaskKey);
            Text(card.WatchPath);
            Text(card.State);
            Text(card.Fixture ? "fixture" : "card");
            Text(card.LaneRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            hash.AppendData(card.Summary);
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// The write after a publication: the projects whose rows differ from the
    /// persisted digest, and the persisted projects no longer on the board.
    /// </summary>
    public static (IReadOnlyList<BoardSummaryProject> Changed, IReadOnlyList<string> Removed) Plan(
        IReadOnlyDictionary<string, string> persistedHashes, IReadOnlyList<BoardSummaryProject> built)
    {
        var changed = built
            .Where(project => !persistedHashes.TryGetValue(project.ProjectName, out var hash)
                || !string.Equals(hash, project.ContentHash, StringComparison.Ordinal))
            .ToList();
        var current = built.Select(project => project.ProjectName).ToHashSet(StringComparer.Ordinal);
        var removed = persistedHashes.Keys.Where(name => !current.Contains(name)).ToList();
        return (changed, removed);
    }

    /// <summary>
    /// The cards a restored read may serve: visible projects only, optionally
    /// one watch path, and fixtures only when the read asks for them, like the
    /// live read filters its scan.
    /// </summary>
    public static IReadOnlyList<BoardSummaryCard> Select(
        IReadOnlyList<BoardSummaryProject> projects,
        Func<string, bool> projectVisible,
        Func<string, bool>? watchPath,
        bool includeFixtures)
        => projects
            .Where(project => projectVisible(project.ProjectName))
            .SelectMany(project => project.Cards)
            .Where(card => (includeFixtures || !card.Fixture) && (watchPath is null || watchPath(card.WatchPath)))
            .ToList();

    /// <summary>The oldest Git stamp among the projects represented by <paramref name="cards"/>.</summary>
    public static DateTimeOffset? GitStateAt(IReadOnlyList<BoardSummaryProject> projects, IReadOnlyList<BoardSummaryCard> cards)
    {
        var represented = cards.Select(card => card.ProjectName).ToHashSet(StringComparer.Ordinal);
        return projects.Where(project => represented.Contains(project.ProjectName))
            .Select(project => project.GitStateAt).Where(at => at is not null).Min();
    }

    /// <summary>The list body: index order.</summary>
    public static byte[] ComposeList(IReadOnlyList<BoardSummaryCard> cards)
        => BoardCardSummary.SerializeSummaryList(cards
            .OrderBy(card => card.ScanRank)
            .ThenBy(card => card.ProjectName, StringComparer.Ordinal)
            .Select(card => card.Summary));

    /// <summary>
    /// The grouped body: the live lanes, each ordered like
    /// <see cref="LaneSortApplier"/> (projects alphabetically, then the
    /// project's lane rank). A restored body is always <c>stale</c>: its Git
    /// stamp has not been confirmed since the process started.
    /// </summary>
    public static byte[] ComposeGrouped(
        IReadOnlyList<BoardSummaryCard> cards, bool legacyReviewLane, DateTimeOffset? gitStateAt)
    {
        IReadOnlyList<byte[]> SortLane(string lane) => cards
            .Where(card => card.State == lane)
            .OrderBy(card => card.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(card => card.LaneRank)
            .Select(card => card.Summary)
            .ToList();
        return BoardCardSummary.SerializeSummaryGrouped(
            BoardCardSummary.GroupedLanes(SortLane, legacyReviewLane), gitStateAt, stale: true);
    }
}
