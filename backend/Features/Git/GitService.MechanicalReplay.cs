using System.Diagnostics;

namespace AgentStudio.Git;

public partial class GitService
{
    /// <summary>
    /// Replays a linear delivery using Git's in-memory merge engine. Git 2.43
    /// supports this path, so no checkout or index is created on either host.
    /// A Git too old for the required merge-tree options uses the worktree path.
    /// </summary>
    private MechanicalRebaseAttempt? TryMechanicalTreeReplay(
        string repoRoot, string integrationTip, IReadOnlyList<string> originalCommits)
    {
        var tip = integrationTip;
        var replacements = new List<RebasedCommitReplacement>();
        for (var index = 0; index < originalCommits.Count; index++)
        {
            var original = originalCommits[index];
            var (parent, parentError, parentCode) = RunGitArgs(repoRoot, "rev-parse", original + "^");
            if (parentCode != 0)
                return MechanicalRebaseAttempt.Failed($"Mechanical replay could not read a commit parent: {parentError.Trim()}");

            var (merged, mergeError, mergeCode) = RunGitArgs(repoRoot,
                "merge-tree", "--write-tree", "--name-only", "--no-messages", "-z",
                "--merge-base=" + parent.Trim(), tip, original);
            if (mergeCode == 129)
                return null;
            if (mergeCode == 1)
            {
                var files = merged.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1).ToArray();
                return files.Length > 0
                    ? MechanicalRebaseAttempt.Conflict(files,
                        "Mechanical rebase conflicted and was aborted.",
                        original, index + 1, originalCommits.Count)
                    : MechanicalRebaseAttempt.Failed($"Mechanical replay failed: {mergeError.Trim()}");
            }
            if (mergeCode != 0)
                return MechanicalRebaseAttempt.Failed($"Mechanical replay failed: {mergeError.Trim()}");

            var tree = merged.Split('\0', 2)[0].Trim();
            var (metadata, metadataError, metadataCode) = RunGitArgs(repoRoot,
                "show", "-s", "--format=%an%x00%ae%x00%aI%x00%B", original);
            var fields = metadata.Split('\0', 4);
            if (metadataCode != 0 || fields.Length != 4)
                return MechanicalRebaseAttempt.Failed($"Mechanical replay could not read commit metadata: {metadataError.Trim()}");
            var environment = new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = fields[0],
                ["GIT_AUTHOR_EMAIL"] = fields[1],
                ["GIT_AUTHOR_DATE"] = fields[2],
            };
            var (rebased, commitError, commitCode) = RunGitArgs(repoRoot,
                ["commit-tree", tree, "-p", tip, "-F", "-"], fields[3], environment);
            if (commitCode != 0)
                return MechanicalRebaseAttempt.Failed($"Mechanical replay could not write a commit: {commitError.Trim()}");
            tip = rebased.Trim();
            replacements.Add(new RebasedCommitReplacement(original, tip));
        }

        return MechanicalRebaseAttempt.Applied(tip, replacements, originalCommits.Count);
    }

    internal static bool RemoveMechanicalWorktreeWithRetry(Func<bool> remove, Action<TimeSpan> wait)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (remove()) return true;
            if (attempt < 2) wait(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
        }
        return false;
    }

    private static GitWorktreeResult AddMechanicalWorktree(string repoRoot, string worktreePath, string atRef)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "worktree", "add", "--detach", worktreePath, atRef })
            start.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(start, timeout: TimeSpan.FromMinutes(3));
        return result.Success
            ? new GitWorktreeResult(true, worktreePath, null)
            : new GitWorktreeResult(false, null, result.StandardError.Trim());
    }

}
