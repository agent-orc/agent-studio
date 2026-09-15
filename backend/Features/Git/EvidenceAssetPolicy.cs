namespace AgentStudio.Git;

/// <summary>
/// Machine-readable outcomes of <see cref="EvidenceAssetPolicy.Decide"/>. The
/// admitted code is carried as an informational commit-gate finding so the card
/// can say WHY a binary rode along without a manual review step.
/// </summary>
public static class EvidenceAssetCodes
{
    /// <summary>Declared asset path, known asset type, within the size limit.</summary>
    public const string Admitted = "evidence-asset";

    /// <summary>The project declared no asset paths, or the candidate is outside all of them.</summary>
    public const string NotDeclared = "asset-path-not-declared";

    /// <summary>Under a declared asset path, but not a recognized evidence asset type.</summary>
    public const string UnsupportedType = "unsupported-asset-type";

    /// <summary>A declared asset that exceeds <see cref="EvidenceAssetPolicy.MaxAssetBytes"/>.</summary>
    public const string Oversized = "oversized-asset";
}

/// <summary>One admission decision plus the sentence the gate records with it.</summary>
public sealed record EvidenceAssetDecision(bool Admitted, string Code, string Message);

/// <summary>
/// AGT-2828: pure admission rule for evidence assets.
///
/// <para>WEB-21 captured 12 screenshots the task had explicitly asked for. Every
/// one of them raised <c>binary-surprise</c>, which is an unresolved warning, so
/// the commit candidate gate refused the whole delivery and the run ended with
/// nothing committed. A screenshot a task was told to produce is not a surprise;
/// it is the deliverable. This policy names the narrow case where that is true:
/// a recognized asset type, under a path the PROJECT declared for assets, within
/// a size limit.</para>
///
/// <para>It fails closed on purpose. A project that declares no asset paths
/// admits nothing, so no repository silently starts accepting binaries; and the
/// declared prefixes are the only escape hatch, so the rule can never widen to
/// "any binary anywhere". Everything outside the three conditions keeps the old
/// manual-review behaviour.</para>
/// </summary>
public static class EvidenceAssetPolicy
{
    /// <summary>
    /// Largest evidence asset admitted without a manual review step. Kept at the
    /// gate's own <c>oversized-surprise</c> threshold so the two rules agree: an
    /// admitted asset is never large enough to also be reported as oversized.
    /// </summary>
    public const long MaxAssetBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Recognized binary evidence types. Deliberately short: screenshots and the
    /// rendered reports a review reads. Text formats (`.svg`, `.md`, `.html`)
    /// are absent because they never trip the binary rule in the first place.
    /// </summary>
    public static readonly IReadOnlyList<string> AssetExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".pdf"];

    /// <summary>
    /// Normalizes declared asset paths to repository-relative, forward-slashed,
    /// trailing-slash-free prefixes. Blank entries and any entry that walks out
    /// of the repository (<c>..</c>) are dropped rather than repaired: a
    /// mistyped declaration must not widen the rule.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDeclaredPaths(IEnumerable<string>? declared)
    {
        if (declared is null) return [];
        var normalized = new List<string>();
        foreach (var raw in declared)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.Replace('\\', '/').Trim().Trim('/');
            if (value.Length == 0) continue;
            if (value.Split('/').Any(segment => segment == "..")) continue;
            if (!normalized.Contains(value, StringComparer.Ordinal)) normalized.Add(value);
        }
        return normalized;
    }

    /// <summary>
    /// Decides whether one candidate is a declared evidence asset. Pure: every
    /// input is a value, so the matrix test covers the rule directly instead of
    /// through a repository fixture.
    /// </summary>
    public static EvidenceAssetDecision Decide(
        string? relativePath,
        long sizeBytes,
        IReadOnlyCollection<string>? declaredPaths)
    {
        var path = (relativePath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        var declared = NormalizeDeclaredPaths(declaredPaths);
        if (path.Length == 0 || declared.Count == 0 || !IsUnderDeclaredPath(path, declared))
        {
            return new EvidenceAssetDecision(false, EvidenceAssetCodes.NotDeclared,
                "Candidate is not under a declared evidence-asset path.");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!AssetExtensions.Contains(extension, StringComparer.Ordinal))
        {
            return new EvidenceAssetDecision(false, EvidenceAssetCodes.UnsupportedType,
                "Candidate is under a declared evidence-asset path but is not a recognized asset type.");
        }

        if (sizeBytes > MaxAssetBytes)
        {
            return new EvidenceAssetDecision(false, EvidenceAssetCodes.Oversized,
                $"Declared evidence asset is larger than the {MaxAssetBytes} byte limit and requires explicit review.");
        }

        return new EvidenceAssetDecision(true, EvidenceAssetCodes.Admitted,
            "Declared evidence asset within the size limit; committable without a manual review step.");
    }

    private static bool IsUnderDeclaredPath(string path, IReadOnlyList<string> declared)
    {
        foreach (var prefix in declared)
        {
            if (path.Length <= prefix.Length) continue;
            if (path[prefix.Length] != '/') continue;
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
