using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// The identity of an immutable diff object (docs/studio-route-ownership,
/// decision D1): repository, named view, base and head SHA, diff options and
/// renderer version. Commits are content addressed, so an object never needs
/// a TTL. <see cref="ObjectId"/> is the stored key.
/// </summary>
public sealed record GitDiffObjectKey(string RepoId, string View, string BaseSha, string HeadSha)
{
    /// <summary>One attributed commit against its first parent (<see cref="RootBase"/> for a root commit).</summary>
    public const string CommitView = "commit";

    /// <summary>
    /// The card's aggregate: the ordered attributed commits, each against its
    /// first parent. <see cref="HeadSha"/> hashes the ordered member SHAs; the
    /// manifest names the member commit objects, whose patches it reuses.
    /// </summary>
    public const string AttributedCommitsView = "attributed-commits";

    /// <summary>One tree diff <c>base..head</c>, for run views.</summary>
    public const string RangeView = "range";

    public const string RootBase = "root";
    public const string Options = "renames=M;merges=first-parent;context=3";
    public const int RendererVersion = 1;

    public string ObjectId => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{RepoId}\0{View}\0{BaseSha}\0{HeadSha}\0{Options}\0{RendererVersion}")));

    public static GitDiffObjectKey Commit(string repoId, GitCommitFact commit)
        => new(repoId, CommitView, commit.Parents.Count > 0 ? commit.Parents[0] : RootBase, commit.Sha);

    public static GitDiffObjectKey Aggregate(string repoId, IReadOnlyList<string> orderedShas)
        => new(repoId, AttributedCommitsView, "", Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', orderedShas.Select(sha => sha.ToLowerInvariant()))))));

    public static GitDiffObjectKey Range(string repoId, string baseSha, string headSha)
        => new(repoId, RangeView, baseSha, headSha);
}

/// <summary>One manifest row: path, status, rename source, numstat, binary and truncation flags.</summary>
public sealed record GitDiffManifestFile(
    string Path, string Status, string? OldPath, int Added, int Removed, bool Binary, bool Truncated);

/// <summary>
/// A stored diff object without its patches. <see cref="State"/> is
/// <c>ready</c>, <c>too-large</c> (manifest only) or <c>unavailable</c> with a
/// typed <see cref="Reason"/>. An aggregate lists its member commit objects.
/// </summary>
public sealed record GitDiffObjectManifest(
    string ObjectId,
    string RepoId,
    string View,
    string BaseSha,
    string HeadSha,
    string State,
    string? Reason,
    IReadOnlyList<GitDiffManifestFile> Files,
    IReadOnlyList<string> Members,
    bool FilesTruncated,
    DateTimeOffset CreatedAt)
{
    public const string Ready = "ready";
    public const string TooLarge = "too-large";
    public const string Unavailable = "unavailable";

    /// <summary>Most files one manifest lists (D1); more are marked <see cref="FilesTruncated"/>.</summary>
    public const int MaxFiles = 5_000;
}

/// <summary>A manifest plus the patches the producer wrote with it.</summary>
internal sealed record GitDiffObject(GitDiffObjectManifest Manifest, IReadOnlyList<GitPatchFile> Patches);

/// <summary>
/// Builds diff objects from facts the producer already holds. Pure: commit
/// facts give the manifest, a split patch gives the bounded per-file patches.
/// </summary>
internal static class GitDiffObjectBuilder
{
    internal static GitDiffObject FromCommit(string repoId, GitCommitFact commit, string? patch, DateTimeOffset now)
    {
        var key = GitDiffObjectKey.Commit(repoId, commit);
        return Build(key, commit.Files, patch, [], now);
    }

    internal static GitDiffObject FromRange(string repoId, string baseSha, string headSha,
        IReadOnlyList<GitCommitFileFact> files, string? patch, DateTimeOffset now)
        => Build(GitDiffObjectKey.Range(repoId, baseSha, headSha), files, patch, [], now);

    /// <summary>
    /// The aggregate object: the aggregated file list of
    /// <see cref="GitCardFactJoins.AggregateFiles"/> and its member commit objects.
    /// </summary>
    internal static GitDiffObject Aggregate(string repoId, IReadOnlyList<GitCommitFact> commits, DateTimeOffset now)
    {
        var key = GitDiffObjectKey.Aggregate(repoId, commits.Select(commit => commit.Sha).ToList());
        var files = GitCardFactJoins.AggregateFiles(commits)
            .Select(file => new GitDiffManifestFile(file.Path, file.Status, null, file.Added, file.Removed, false, false))
            .ToList();
        var truncated = files.Count > GitDiffObjectManifest.MaxFiles;
        return new GitDiffObject(new GitDiffObjectManifest(key.ObjectId, key.RepoId, key.View, key.BaseSha, key.HeadSha,
            GitDiffObjectManifest.Ready, null, truncated ? files.Take(GitDiffObjectManifest.MaxFiles).ToList() : files,
            commits.Select(commit => GitDiffObjectKey.Commit(repoId, commit).ObjectId).ToList(), truncated, now), []);
    }

    /// <summary>A manifest-only object whose patch could not be read within the limits.</summary>
    internal static GitDiffObject TooLarge(GitDiffObjectKey key, IReadOnlyList<GitCommitFileFact> files, DateTimeOffset now)
        => Build(key, files, null, [], now, GitDiffObjectManifest.TooLarge, "patch-exceeds-read-limit");

    internal static GitDiffObject Unavailable(GitDiffObjectKey key, string reason, DateTimeOffset now)
        => new(new GitDiffObjectManifest(key.ObjectId, key.RepoId, key.View, key.BaseSha, key.HeadSha,
            GitDiffObjectManifest.Unavailable, reason, [], [], false, now), []);

    private static GitDiffObject Build(GitDiffObjectKey key, IReadOnlyList<GitCommitFileFact> files, string? patch,
        IReadOnlyList<string> members, DateTimeOffset now, string state = GitDiffObjectManifest.Ready, string? reason = null)
    {
        var patches = patch is null ? [] : GitPatchSplitter.Split(patch);
        var truncatedPaths = patches.Where(file => file.Truncated).Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        var manifest = files.Select(file => new GitDiffManifestFile(file.Path, file.Status, file.OldPath, file.Added,
                file.Removed, file.Binary, truncatedPaths.Contains(file.Path)))
            .ToList();
        var listed = manifest.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        // A path the patch names but the stat did not (a mode-only change) still renders.
        manifest.AddRange(patches.Where(file => !listed.Contains(file.Path))
            .Select(file => new GitDiffManifestFile(file.Path, "M", null, 0, 0, false, file.Truncated)));
        var tooMany = manifest.Count > GitDiffObjectManifest.MaxFiles;
        if (tooMany)
        {
            manifest = manifest.Take(GitDiffObjectManifest.MaxFiles).ToList();
            var kept = manifest.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            patches = patches.Where(file => kept.Contains(file.Path)).ToList();
        }
        return new GitDiffObject(new GitDiffObjectManifest(key.ObjectId, key.RepoId, key.View, key.BaseSha,
            key.HeadSha, state, reason, manifest, members, tooMany, now), patches);
    }
}
