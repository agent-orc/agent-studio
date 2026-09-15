using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>What one supervised publication attempt should do.</summary>
public enum WikiPublicationAction
{
    /// <summary>The project has no wiki source ref, so it stays checkout-backed.</summary>
    Disabled,
    /// <summary>The accepted revision is already published and still readable.</summary>
    NoOp,
    /// <summary>A newer accepted revision must replace the published one.</summary>
    Promote,
    /// <summary>The attempt cannot advance; the previous revision stays online.</summary>
    Fail,
}

/// <summary>
/// Typed deployment failure. Every unsuccessful publication attempt reports one
/// of these instead of free-text prose, so operator tooling can branch on the
/// reason and the recovery runbook can name it.
/// </summary>
public enum WikiPublicationFailure
{
    None,
    /// <summary>The project has no resolvable repository checkout on this host.</summary>
    RepositoryUnavailable,
    /// <summary>The configured ref is not an accepted integration branch or release revision.</summary>
    UnacceptedRef,
    /// <summary>The remote could not be contacted, so the candidate cannot be trusted as current.</summary>
    FetchFailed,
    /// <summary>The configured ref does not resolve to a commit in this repository.</summary>
    RevisionNotFound,
    /// <summary>The revision resolved but its docs/ tree could not be materialized.</summary>
    SnapshotFailed,
    /// <summary>A rollback was requested but no previous revision is still readable.</summary>
    RollbackUnavailable,
}

/// <summary>
/// The plain facts one publication attempt collected, with no filesystem,
/// process, or clock left in them. <see cref="WikiPublicationPolicy.Decide"/>
/// turns this into the action; the service performs it.
/// </summary>
public sealed record WikiPublicationFacts(
    string? SourceRef,
    bool RepositoryAvailable,
    bool RefAccepted,
    string? FetchError,
    string? CandidateSha,
    string? PublishedSha,
    bool PublishedSnapshotUsable);

/// <summary>The chosen action plus the old and new revision it applies to.</summary>
public sealed record WikiPublicationDecision(
    WikiPublicationAction Action,
    WikiPublicationFailure Failure,
    string Reason,
    string? FromSha,
    string? ToSha);

/// <summary>
/// Pure decision layer for hosted wiki publication. It answers two questions
/// without touching git, disk, or the clock: is this ref allowed to be
/// published at all, and does the collected evidence justify promoting a new
/// revision over the one currently online.
///
/// <para>The published revision is deliberately restricted to an accepted
/// integration branch or a release revision. An arbitrary active task branch
/// is never publishable, because the hosted wiki is the accepted documentation
/// state and not a preview of work in progress.</para>
/// </summary>
public static class WikiPublicationPolicy
{
    /// <summary>
    /// Ref patterns accepted as a published source by default. An entry is
    /// either an exact ref name or a <c>prefix*</c> pattern. Release tags of
    /// the form <c>v1</c> / <c>v1.2.3</c> are accepted in addition to these,
    /// as is an explicit 40-character commit SHA.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAcceptedRefs =
        ["main", "master", "develop", "release/*"];

    private static readonly Regex ReleaseTagPattern =
        new(@"^v\d+(\.\d+)*$", RegexOptions.Compiled);

    private static readonly Regex FullShaPattern =
        new(@"^[0-9a-f]{40}$", RegexOptions.Compiled);

    /// <summary>True when the ref is a pinned commit SHA rather than a moving branch.</summary>
    public static bool IsExplicitRevision(string? gitRef) =>
        !string.IsNullOrWhiteSpace(gitRef) && FullShaPattern.IsMatch(gitRef.Trim());

    /// <summary>
    /// Strips a leading remote segment so <c>origin/develop</c> and
    /// <c>develop</c> are judged by the same rule.
    /// </summary>
    public static string NormalizeRef(string gitRef, string remote)
    {
        var trimmed = (gitRef ?? "").Trim();
        var prefix = (remote ?? "origin").Trim() + "/";
        return trimmed.StartsWith(prefix, StringComparison.Ordinal)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    /// <summary>True when the ref is remote-qualified and therefore worth fetching.</summary>
    public static bool RequiresFetch(string? gitRef, string remote)
    {
        if (string.IsNullOrWhiteSpace(gitRef) || IsExplicitRevision(gitRef)) return false;
        var prefix = (remote ?? "origin").Trim() + "/";
        return gitRef.Trim().StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the configured ref may become the published wiki revision.
    /// </summary>
    public static bool IsPublishableRef(
        string? gitRef,
        string remote,
        IReadOnlyCollection<string>? acceptedPatterns = null)
    {
        if (string.IsNullOrWhiteSpace(gitRef)) return false;
        if (IsExplicitRevision(gitRef)) return true;

        var name = NormalizeRef(gitRef, remote);
        if (name.Length == 0) return false;
        if (ReleaseTagPattern.IsMatch(name)) return true;

        var patterns = acceptedPatterns is { Count: > 0 } ? acceptedPatterns : DefaultAcceptedRefs;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var candidate = pattern.Trim();
            if (candidate.EndsWith('*'))
            {
                var stem = candidate[..^1];
                if (stem.Length > 0 && name.StartsWith(stem, StringComparison.Ordinal)) return true;
            }
            else if (string.Equals(name, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Chooses the action for one attempt. Every non-promoting outcome keeps
    /// the currently published revision online by construction: nothing here
    /// can report success for a revision that was not fully validated first.
    /// </summary>
    public static WikiPublicationDecision Decide(WikiPublicationFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.SourceRef))
            return new(WikiPublicationAction.Disabled, WikiPublicationFailure.None,
                "No wiki source ref is configured, so the project stays checkout-backed.",
                facts.PublishedSha, facts.PublishedSha);

        if (!facts.RepositoryAvailable)
            return new(WikiPublicationAction.Fail, WikiPublicationFailure.RepositoryUnavailable,
                "The project has no resolvable repository checkout on this host.",
                facts.PublishedSha, null);

        if (!facts.RefAccepted)
            return new(WikiPublicationAction.Fail, WikiPublicationFailure.UnacceptedRef,
                $"Ref '{facts.SourceRef}' is not an accepted integration branch or release revision.",
                facts.PublishedSha, null);

        if (!string.IsNullOrWhiteSpace(facts.FetchError))
            return new(WikiPublicationAction.Fail, WikiPublicationFailure.FetchFailed,
                $"Fetching '{facts.SourceRef}' failed: {facts.FetchError!.Trim()}",
                facts.PublishedSha, null);

        if (string.IsNullOrWhiteSpace(facts.CandidateSha))
            return new(WikiPublicationAction.Fail, WikiPublicationFailure.RevisionNotFound,
                $"Ref '{facts.SourceRef}' does not resolve to a commit in this repository.",
                facts.PublishedSha, null);

        if (string.Equals(facts.CandidateSha, facts.PublishedSha, StringComparison.Ordinal)
            && facts.PublishedSnapshotUsable)
            return new(WikiPublicationAction.NoOp, WikiPublicationFailure.None,
                "The accepted revision is already published.",
                facts.PublishedSha, facts.PublishedSha);

        return new(WikiPublicationAction.Promote, WikiPublicationFailure.None,
            string.IsNullOrWhiteSpace(facts.PublishedSha)
                ? "Publishing the accepted revision for the first time."
                : "A newer accepted revision replaces the published one.",
            facts.PublishedSha, facts.CandidateSha);
    }

    /// <summary>
    /// Verdict on a materialized candidate before it is allowed to replace the
    /// published revision. A half-extracted or docs-less snapshot must never
    /// become the online tree.
    /// </summary>
    public static WikiPublicationFailure ValidateMaterialization(
        string? snapshotError,
        bool docsTreePresent) =>
        !string.IsNullOrWhiteSpace(snapshotError) || !docsTreePresent
            ? WikiPublicationFailure.SnapshotFailed
            : WikiPublicationFailure.None;
}
