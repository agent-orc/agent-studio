namespace AgentStudio.Git;

/// <summary>One virtual merge of a delivery onto a base, written as objects only.</summary>
/// <param name="Sha">The merge commit; null when the merge conflicts or Git cannot answer.</param>
/// <param name="TreeSha">The merged tree, which the real integration merge reproduces exactly.</param>
/// <param name="Conflict">True when Git reported a content conflict.</param>
public sealed record VirtualMergeResult(string? Sha, string? TreeSha, bool Conflict, string? Error);

public partial class GitService
{
    /// <summary>Ref that keeps the newest post-merge train tip reachable while its compile runs.</summary>
    public static string PostMergeTrainRef(string branch)
        => "refs/agent-studio/post-merge-train/" + string.Concat(
            branch.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-'));

    /// <summary>
    /// AGT-4020: merges <paramref name="deliverySha"/> onto <paramref name="baseSha"/>
    /// with Git's in-memory merge engine (the same ort merge the integration
    /// worktree runs) and writes a two-parent commit for it. No ref, index or
    /// checkout changes, so the train can lay out the merges of every waiting
    /// card in order and compile the combined tree before one of them lands.
    /// </summary>
    public VirtualMergeResult VirtualMerge(string repoRoot, string baseSha, string deliverySha, string message)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new(null, null, false, "repository root is unavailable");
        if (!IsObjectName(baseSha) || !IsObjectName(deliverySha))
            return new(null, null, false, "base or delivery is not an object name");
        var (merged, mergeError, mergeCode) = RunGitArgs(repoRoot,
            "merge-tree", "--write-tree", "--no-messages", baseSha, deliverySha);
        if (mergeCode == 1) return new(null, null, true, "the delivery conflicts with the train");
        if (mergeCode != 0) return new(null, null, false, mergeError.Trim());
        var tree = merged.Split('\n', 2)[0].Trim();
        if (!IsObjectName(tree)) return new(null, null, false, "merge-tree returned no tree");
        var identity = new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = "Agent Studio",
            ["GIT_AUTHOR_EMAIL"] = "agent-studio@users.noreply.github.com",
            ["GIT_COMMITTER_NAME"] = "Agent Studio",
            ["GIT_COMMITTER_EMAIL"] = "agent-studio@users.noreply.github.com",
        };
        var (commit, commitError, commitCode) = RunGitArgs(repoRoot,
            ["commit-tree", tree, "-p", baseSha, "-p", deliverySha, "-F", "-"], message, identity);
        var sha = commit.Trim();
        return commitCode == 0 && IsObjectName(sha)
            ? new(sha, tree, false, null)
            : new(null, tree, false, commitError.Trim());
    }

    /// <summary>Points <see cref="PostMergeTrainRef"/> at the train tip; best effort.</summary>
    public bool PinPostMergeTrain(string repoRoot, string branch, string sha)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot) || !IsObjectName(sha)) return false;
        var (_, _, code) = RunGitArgs(repoRoot, "update-ref", PostMergeTrainRef(branch), sha);
        return code == 0;
    }

    /// <summary>The second parent of a merge commit, the delivery it merged; null otherwise.</summary>
    public string? GetSecondParent(string repoRoot, string commit)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        if (!IsObjectName(commit)) return null;
        var (output, _, code) = RunGitArgs(repoRoot, "rev-parse", "--verify", "--quiet", commit + "^2");
        var sha = output.Trim();
        return code == 0 && IsObjectName(sha) ? sha : null;
    }

    /// <summary>True when the object exists in the repository's object store.</summary>
    public bool HasCommitObject(string repoRoot, string sha)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot) || !IsObjectName(sha)) return false;
        var (_, _, code) = RunGitArgs(repoRoot, "cat-file", "-e", sha + "^{commit}");
        return code == 0;
    }
}
