using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// What Studio can observe about a candidate integration worktree directory
/// before it touches anything. Every fact is cheap: one directory probe, one
/// <c>git worktree list</c> lookup, one administrative-link probe.
/// </summary>
/// <param name="IsDeveloperCheckout">
/// The candidate resolves to the registered project checkout itself. Integration
/// must never run there (AGT-2832), no matter how the path was derived.
/// </param>
/// <param name="DirectoryExists">The candidate path exists on disk.</param>
/// <param name="DirectoryIsEmpty">The existing directory holds no entries.</param>
/// <param name="RegisteredForRepository">
/// <c>git worktree list</c> of the project repository contains the candidate.
/// </param>
/// <param name="HasGitAdminLink">
/// The candidate carries the <c>.git</c> link file that makes it a live worktree
/// of the shared object store.
/// </param>
public sealed record IntegrationWorktreeState(
    bool IsDeveloperCheckout,
    bool DirectoryExists,
    bool DirectoryIsEmpty,
    bool RegisteredForRepository,
    bool HasGitAdminLink);

/// <summary>What preparing the integration worktree must do to reach a usable slot.</summary>
public enum IntegrationWorktreeAction
{
    /// <summary>Register a new worktree at the candidate path.</summary>
    Create,

    /// <summary>The slot is live and owned by Studio; refresh it in place.</summary>
    Reuse,

    /// <summary>Drop the stale registration or leftover directory, then create it again.</summary>
    Recreate,

    /// <summary>The candidate may not be used at all.</summary>
    Blocked,
}

/// <summary>The action plus the reason that is reported when it is visible to an operator.</summary>
public sealed record IntegrationWorktreeDecision(
    IntegrationWorktreeAction Action,
    string? Reason = null);

/// <summary>
/// Pure placement and lifecycle policy for the Studio-owned integration
/// worktree. Deliveries are merged into the integration branch there instead of
/// in the developer checkout, so uncommitted developer edits can no longer
/// refuse an integration and Studio never switches the branch of a checkout a
/// person is working in.
///
/// <para>The path is derived, never stored: the same repository always resolves
/// to the same slot, so an existing project needs no migration step and a
/// restarted process finds the worktree it created before.</para>
/// </summary>
public static class IntegrationWorktreePolicy
{
    /// <summary>Container directory placed next to the project checkout.</summary>
    public const string ContainerName = ".agent-studio-integration";

    /// <summary>
    /// Ordered candidate slots for <paramref name="repoRoot"/>. The sibling
    /// container keeps the worktree on the same volume as the repository and
    /// inside the folder an operator already associates with the project; the
    /// temporary root is the fallback for a read-only or otherwise unusable
    /// parent directory. Both are outside the checkout, so the worktree can
    /// never show up as dirt in the project's own status.
    ///
    /// <para>When the parent directory itself lies inside a git repository (a
    /// checkout nested in another one), the order is reversed: a sibling
    /// container would show up as untracked content in that outer repository,
    /// which is exactly the kind of foreign dirt this card removes.</para>
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(
        string repoRoot,
        string? temporaryRoot = null,
        bool containerParentIsInsideRepository = false)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return [];

        var full = Path.GetFullPath(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var slot = SlotName(full);
        var candidates = new List<string>(2);

        var parent = Path.GetDirectoryName(full);
        var sibling = string.IsNullOrWhiteSpace(parent)
            ? null
            : Path.Combine(parent, ContainerName, slot);
        var temporary = string.IsNullOrWhiteSpace(temporaryRoot) ? Path.GetTempPath() : temporaryRoot!;
        var fallback = Path.Combine(temporary, "agent-studio-integration", slot);

        if (sibling is not null && !containerParentIsInsideRepository)
            candidates.Add(sibling);
        candidates.Add(fallback);
        if (sibling is not null && containerParentIsInsideRepository)
            candidates.Add(sibling);

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Stable per-repository slot name: the readable repository folder name plus
    /// a short digest of its absolute path, so two projects with the same folder
    /// name never share one integration worktree.
    /// </summary>
    public static string SlotName(string repoRoot)
    {
        var full = Path.GetFullPath(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var name = SafeSegment(Path.GetFileName(full));
        return $"{name}-{ShortDigest(full)}";
    }

    /// <summary>
    /// The decision matrix. It is total: every combination of the observed facts
    /// maps to exactly one action, so preparation never has to guess at a
    /// half-registered slot.
    /// </summary>
    public static IntegrationWorktreeDecision Decide(IntegrationWorktreeState state)
    {
        if (state.IsDeveloperCheckout)
        {
            return new(
                IntegrationWorktreeAction.Blocked,
                "The developer checkout is never used as the integration worktree.");
        }

        if (state.RegisteredForRepository)
        {
            if (!state.DirectoryExists)
            {
                return new(
                    IntegrationWorktreeAction.Recreate,
                    "The integration worktree is registered but its directory is gone.");
            }
            return state.HasGitAdminLink
                ? new(IntegrationWorktreeAction.Reuse)
                : new(
                    IntegrationWorktreeAction.Recreate,
                    "The integration worktree lost its administrative link to the repository.");
        }

        if (!state.DirectoryExists || state.DirectoryIsEmpty)
            return new(IntegrationWorktreeAction.Create);

        return new(
            IntegrationWorktreeAction.Recreate,
            "A leftover directory occupies the integration worktree slot without being registered.");
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "repository";
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '-');
        var cleaned = builder.ToString().Trim('-', '.');
        return cleaned.Length == 0 ? "repository" : cleaned;
    }

    private static string ShortDigest(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
