namespace AgentStudio.Review;

/// <summary>
/// One reviewed card for the wave census: its Dossier (<c>task.json.dossier.path</c>),
/// when its card check last passed, and its review material size. A missing
/// value stays null.
/// </summary>
public sealed record WaveCensusCard(string TaskKey, string? DossierPath, DateTime? CardCheckPassedAt,
    int? Files, int? Lines);

/// <summary>
/// A package PK5 A would form. <see cref="SizeUnknown"/> when a member's
/// material size is unknown, so the budget could not be checked for it.
/// <see cref="OverBudget"/> marks a lone member that alone exceeds the budget.
/// </summary>
public sealed record WavePackage(string DossierPath, IReadOnlyList<string> Members, DateTime FirstCheckAt,
    int? Files, int? Lines, bool SizeUnknown, bool OverBudget);

/// <summary>
/// Dossier wave census (AGT-3400 S2, deliverable 3). <see cref="DossierShare"/>
/// is the share of reviewed cards with a Dossier; the Dossier's S5 stop rule
/// applies below <see cref="ReviewWaveCensus.WaveShareThreshold"/>.
/// <see cref="MedianPackageSize"/> is k: the median size of the packages PK5 A
/// would form from Dossier cards. Cards without a Dossier stay per card (R-A)
/// and are not packages.
/// </summary>
public sealed record WaveCensus(
    int ReviewedCards, int DossierCards, double? DossierShare, bool? MeetsWaveShareThreshold,
    int DossierCardsWithoutCheckTime, int Packages, double? MedianPackageSize, double? MeanPackageSize,
    IReadOnlyDictionary<int, int> PackageSizes, int PackagesWithUnknownSize,
    IReadOnlyList<WavePackage> PackageList,
    int FormationMinutes = ReviewWaveCensus.FormationMinutes, int MaximumMembers = ReviewWaveCensus.MaximumMembers,
    int MaximumFiles = ReviewWaveCensus.MaximumFiles, int MaximumLines = ReviewWaveCensus.MaximumLines);

/// <summary>Pure PK5 A package formation over reviewed cards.</summary>
public static class ReviewWaveCensus
{
    public const int FormationMinutes = 60;
    public const int MaximumMembers = 4;
    /// <summary>The Remote Review material budget (<c>SourceDiffObjectOptions.ReviewMaximumFiles</c>).</summary>
    public const int MaximumFiles = AgentStudio.TaskServer.Contracts.SourceDiffObjectOptions.ReviewMaximumFiles;
    public const int MaximumLines = AgentStudio.TaskServer.Contracts.SourceDiffObjectOptions.ReviewMaximumLines;
    public const double WaveShareThreshold = 0.30;

    public static WaveCensus Build(IReadOnlyList<WaveCensusCard> cards)
    {
        var dossierCards = cards.Where(card => NormalizePath(card.DossierPath) is not null).ToList();
        var packages = dossierCards.Where(card => card.CardCheckPassedAt is not null)
            .GroupBy(card => NormalizePath(card.DossierPath)!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(wave => Form(wave.Key, wave.OrderBy(card => card.CardCheckPassedAt)
                .ThenBy(card => card.TaskKey, StringComparer.Ordinal).ToList()))
            .OrderBy(package => package.FirstCheckAt).ToList();
        var sizes = packages.Select(package => (double)package.Members.Count).ToList();
        double? share = cards.Count == 0 ? null : (double)dossierCards.Count / cards.Count;
        return new WaveCensus(cards.Count, dossierCards.Count, share,
            share is null ? null : share >= WaveShareThreshold,
            dossierCards.Count(card => card.CardCheckPassedAt is null),
            packages.Count, AgentStudio.Tokens.ReviewStatisticsFold.Quantile(sizes, 0.5),
            sizes.Count == 0 ? null : sizes.Average(),
            packages.GroupBy(package => package.Members.Count).OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => group.Count()),
            packages.Count(package => package.SizeUnknown), packages);
    }

    /// <summary>
    /// Greedy formation in card-check order: a package opens at its first
    /// member's check and takes each later member that passed within the
    /// formation window, while it stays within the member cap and the material
    /// budget. A card that does not fit opens the next package. A package never
    /// waits for coding: only cards that passed their check are members.
    /// </summary>
    internal static IEnumerable<WavePackage> Form(string dossier, IReadOnlyList<WaveCensusCard> ordered)
    {
        var members = new List<WaveCensusCard>();
        foreach (var card in ordered)
        {
            if (members.Count > 0 && !Fits(members, card))
            {
                yield return Package(dossier, members);
                members = [];
            }
            members.Add(card);
        }
        if (members.Count > 0) yield return Package(dossier, members);
    }

    private static bool Fits(IReadOnlyList<WaveCensusCard> members, WaveCensusCard card)
    {
        if (members.Count >= MaximumMembers) return false;
        if (card.CardCheckPassedAt!.Value - members[0].CardCheckPassedAt!.Value > TimeSpan.FromMinutes(FormationMinutes))
            return false;
        var candidate = members.Append(card).ToList();
        return Sum(candidate, member => member.Files) is not > MaximumFiles
               && Sum(candidate, member => member.Lines) is not > MaximumLines;
    }

    private static WavePackage Package(string dossier, IReadOnlyList<WaveCensusCard> members)
    {
        var files = Sum(members, member => member.Files);
        var lines = Sum(members, member => member.Lines);
        return new WavePackage(dossier, members.Select(member => member.TaskKey).ToList(),
            members[0].CardCheckPassedAt!.Value, files, lines,
            members.Any(member => member.Files is null || member.Lines is null),
            files > MaximumFiles || lines > MaximumLines);
    }

    // The known part of the size: an unknown member cannot be checked, so it
    // neither blocks nor fills the budget; the package is marked SizeUnknown.
    private static int? Sum(IEnumerable<WaveCensusCard> members, Func<WaveCensusCard, int?> size)
    {
        var known = members.Select(size).Where(value => value is not null).ToList();
        return known.Count == 0 ? null : known.Sum(value => value!.Value);
    }

    internal static string? NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim().Replace('\\', '/').TrimStart('/');
}
