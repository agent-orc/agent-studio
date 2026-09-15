namespace AgentStudio.Git;

/// <summary>
/// Pure policy: is a binary commit candidate an evidence asset the task was
/// explicitly asked to produce?
///
/// <para>The commit candidate gate treats every binary candidate as a surprise
/// that needs explicit review. That is right for a stray build artifact and
/// wrong for the twelve screenshots a UI task was told to capture: WEB-21
/// (15.09.2026) captured its screenshots, the gate raised twelve
/// <c>binary-surprise</c> warnings, nothing was committed, and the complete
/// delivery stayed dirty in the worktree. A declared evidence asset inside the
/// size limit is expected output, so it commits without a manual step.</para>
///
/// <para>The rule is deliberately conjunctive: a candidate must sit under one
/// of the project's declared asset paths AND carry an evidence extension AND
/// stay inside <see cref="MaxAssetBytes"/>. Anything else keeps the old
/// warning, so widening the rule always takes a deliberate declaration rather
/// than a lucky filename.</para>
/// </summary>
public static class CommitCandidateAssetPolicy
{
    /// <summary>
    /// Largest evidence asset that commits without review. Identical to the
    /// gate's <c>oversized-surprise</c> threshold, so one size ceiling governs
    /// both rules: above it the candidate is oversized AND a binary surprise,
    /// and an operator decides.
    /// </summary>
    public const long MaxAssetBytes = 5 * 1024 * 1024;

    /// <summary>
    /// What a project that declared nothing is assumed to have declared: the
    /// <c>results/</c> folder AGENTS.md asks review screenshots to be persisted
    /// in. That is the one convention the platform owns across every managed
    /// repository. A project whose evidence lives elsewhere (a documentation
    /// asset tree, an e2e snapshot folder) declares it through
    /// <c>ProjectSettings.EvidenceAssetPaths</c>; guessing on its behalf would
    /// commit binaries from directories nobody nominated.
    /// </summary>
    public static readonly string[] DefaultAssetPaths = ["results"];

    /// <summary>Extensions that can carry review evidence. Kept to still
    /// images and PDF: a video or archive under an asset path is not something
    /// the platform should commit unattended.</summary>
    public static readonly string[] AssetExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".pdf"];

    /// <summary>
    /// Normalizes declared asset paths to the gate's slash-separated,
    /// root-relative form. Blank entries are dropped; a null or empty
    /// declaration falls back to <see cref="DefaultAssetPaths"/> so a project
    /// that never configured anything still commits its own evidence.
    /// </summary>
    public static IReadOnlyList<string> ResolveAssetPaths(IReadOnlyCollection<string>? declared)
        => ResolveDeclaredPaths(declared) ?? DefaultAssetPaths;

    /// <summary>
    /// Normalizes a declaration for storage. Returns null when nothing usable
    /// was declared, so the setting stays absent and keeps inheriting
    /// <see cref="DefaultAssetPaths"/> rather than freezing today's default
    /// into a project's settings file.
    /// </summary>
    public static IReadOnlyList<string>? ResolveDeclaredPaths(IReadOnlyCollection<string>? declared)
    {
        var normalized = (declared ?? [])
            .Select(NormalizePrefix)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length > 0 ? normalized : null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a declared evidence asset small
    /// enough to commit without an explicit operator review.
    /// </summary>
    /// <param name="path">Repository-relative candidate path, slash separated.</param>
    /// <param name="size">Working-tree size in bytes.</param>
    /// <param name="declaredAssetPaths">The project's declared asset paths;
    /// null or empty resolves to <see cref="DefaultAssetPaths"/>.</param>
    public static bool IsEvidenceAsset(
        string? path, long size, IReadOnlyCollection<string>? declaredAssetPaths)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (size <= 0 || size > MaxAssetBytes) return false;

        var normalized = NormalizePrefix(path);
        if (normalized.Length == 0) return false;
        if (!AssetExtensions.Contains(
                Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
            return false;

        foreach (var prefix in ResolveAssetPaths(declaredAssetPaths))
        {
            if (normalized.StartsWith(prefix + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NormalizePrefix(string? value)
        => (value ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
}
