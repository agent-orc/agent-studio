namespace AgentStudio.Git;

/// <summary>
/// Verdict of checking a claimed delivery commit against the target repository
/// (AGT-2220). The distinction that matters for every stamping path is
/// <em>disproved</em> vs. <em>not checkable</em>: a mismatch is positive
/// evidence that the claim is false and must never produce a terminal stamp,
/// while a missing remote only means we could not look - which is recorded
/// honestly instead of being silently upgraded to "verified".
/// </summary>
public enum DeliveryVerificationStatus
{
    /// <summary>The claimed ref resolves to exactly the claimed commit.</summary>
    Verified,

    /// <summary>
    /// The claimed commit is not the ref tip but is contained in the ref's
    /// history. The commit demonstrably exists in the target repository, so the
    /// delivery is real - it just is not the tip (e.g. the branch moved on).
    /// </summary>
    VerifiedContained,

    /// <summary>The claimed ref does not exist on the target repository.</summary>
    RefMissing,

    /// <summary>
    /// The ref exists but resolves to a different commit and the claimed commit
    /// is not contained in it. This is the "Delivered lies" / phantom case.
    /// </summary>
    ShaMismatch,

    /// <summary>The result ref has the claimed tip, but its history omits the fenced base.</summary>
    BaseNotAncestor,

    /// <summary>No ref was claimed and the commit is nowhere on the target repository.</summary>
    CommitMissing,

    /// <summary>
    /// No repository, no origin remote, or no claim at all - nothing could be
    /// checked. Never counts as proof.
    /// </summary>
    NotVerifiable,
}

/// <summary>
/// Result of verifying one claimed delivery commit against the target
/// repository. Carries the resolved remote SHA so the honest state can name
/// what the repository actually holds instead of only what was claimed.
/// </summary>
public sealed record DeliveryVerificationResult(
    DeliveryVerificationStatus Status,
    string? Message,
    string? ClaimedSha,
    string? GitRef,
    string? ResolvedRefSha)
{
    /// <summary>True only when the commit was positively found in the target repository.</summary>
    public bool IsVerified =>
        Status is DeliveryVerificationStatus.Verified or DeliveryVerificationStatus.VerifiedContained;

    /// <summary>
    /// True when the repository actively contradicts the claim. These never get
    /// a completion stamp - not even a degraded one.
    /// </summary>
    public bool IsDisproved =>
        Status is DeliveryVerificationStatus.ShaMismatch
            or DeliveryVerificationStatus.BaseNotAncestor
            or DeliveryVerificationStatus.RefMissing
            or DeliveryVerificationStatus.CommitMissing;

    /// <summary>
    /// One-line audit note ("Verifikationsvermerk") recorded next to every
    /// stamp so a card states how its delivery was proven, not just that it was.
    /// </summary>
    public string Note => Status switch
    {
        DeliveryVerificationStatus.Verified =>
            $"Repository-Verifikation: {Short(ClaimedSha)} ist die Spitze von '{GitRef}' im Zielrepo.",
        DeliveryVerificationStatus.VerifiedContained =>
            $"Repository-Verifikation: {Short(ClaimedSha)} liegt in der Historie von '{GitRef}' im Zielrepo "
            + $"(Spitze ist {Short(ResolvedRefSha)}).",
        DeliveryVerificationStatus.RefMissing =>
            $"Repository-Verifikation fehlgeschlagen: Ref '{GitRef}' existiert nicht im Zielrepo.",
        DeliveryVerificationStatus.ShaMismatch =>
            $"Repository-Verifikation fehlgeschlagen: '{GitRef}' zeigt auf {Short(ResolvedRefSha)}, "
            + $"behauptet war {Short(ClaimedSha)} - der behauptete Commit ist dort nicht enthalten.",
        DeliveryVerificationStatus.CommitMissing =>
            $"Repository-Verifikation fehlgeschlagen: Commit {Short(ClaimedSha)} existiert nicht im Zielrepo.",
        _ => $"Repository-Verifikation nicht moeglich: {Message}",
    };

    private static string Short(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "(kein SHA)" : sha!.Length <= 8 ? sha! : sha![..8];

    public static DeliveryVerificationResult NotVerifiable(string message, string? sha = null, string? gitRef = null)
        => new(DeliveryVerificationStatus.NotVerifiable, message, sha, gitRef, null);
}
