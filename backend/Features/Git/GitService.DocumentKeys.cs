using AgentStudio.Docs;

namespace AgentStudio.Git;

public sealed record DocumentKeyCandidateResult(bool Success, string? Sha, string? Error);

public sealed partial class GitService
{
    /// <summary>Creates a child of the assembled candidate containing its new descriptor keys.</summary>
    public DocumentKeyCandidateResult IncludeDocumentKeysInCandidate(string repoRoot,
        string baseSha, string candidateSha, ProjectRecord project, ProjectRegistry registry)
    {
        var (changed, diffError, diffCode) = RunGitArgs(repoRoot,
            "diff", "--name-only", "-z", baseSha, candidateSha, "--", "docs");
        if (diffCode != 0) return new(false, null, diffError.Trim());
        if (!changed.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(path => path.EndsWith("/workbench.json", StringComparison.Ordinal)))
            return new(true, candidateSha, null);
        var container = Path.Combine(Path.GetTempPath(),
            "agent-studio-document-keys-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(container, "worktree");
        Directory.CreateDirectory(container);
        var added = false;
        try
        {
            var add = AddMechanicalWorktree(repoRoot, worktree, candidateSha);
            if (!add.Success) return new(false, null, add.Error);
            added = true;
            return IncludeDocumentKeys(worktree, baseSha, candidateSha, project, registry,
                amend: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new(false, null, ex.Message);
        }
        finally
        {
            var cleanup = CleanupMechanicalRebaseWorktree(repoRoot, container, worktree, added);
            if (cleanup is not null)
                _logger.LogWarning("Document key candidate cleanup deferred at {Path}: {Error}",
                    worktree, cleanup);
        }
    }

    /// <summary>
    /// Amends a freshly created integration merge before its gate and journal
    /// record. The amend moves only the detached HEAD of the integration
    /// worktree, so the integration lane is compare-and-swapped from the merge
    /// onto the keyed commit: the gate-approved SHA must be the lane tip, or the
    /// push refuses it as <c>sha-not-on-branch</c> (AGT-3695).
    /// </summary>
    public DocumentKeyCandidateResult IncludeDocumentKeysInIntegrationMerge(string repoRoot,
        string integrationBranch, string baseSha, string mergedSha, ProjectRecord project,
        ProjectRegistry registry)
    {
        var keyed = IncludeDocumentKeys(repoRoot, baseSha, mergedSha, project, registry, amend: true);
        if (!keyed.Success || string.Equals(keyed.Sha, mergedSha, StringComparison.OrdinalIgnoreCase))
            return keyed;
        return PublishIntegrationHead(repoRoot, integrationBranch, mergedSha) is { } laneError
            ? new(false, null, laneError)
            : keyed;
    }

    /// <summary>
    /// Repairs a lane left on the pre-amend merge by AGT-3610: when
    /// <paramref name="approvedSha"/> is a document-key amend of the lane tip
    /// (the same ordered parents, differing only in <c>docs/**/workbench.json</c>),
    /// the lane is compare-and-swapped onto it. Any other shape is left alone,
    /// so an approval the lane really lost still fails closed.
    /// </summary>
    public bool AdoptDocumentKeyAmend(string repoRoot, string integrationBranch, string approvedSha)
    {
        if (!IsLikelyBranchName(integrationBranch) || !IsLikelyShaOrRef(approvedSha)) return false;
        var laneTip = GetBranchTip(repoRoot, IntegrationLaneRef(integrationBranch));
        var (approvedRaw, _, approvedCode) = RunGitArgs(repoRoot,
            "rev-parse", "--verify", $"{approvedSha}^{{commit}}");
        if (laneTip is null || approvedCode != 0) return false;
        var approved = approvedRaw.Trim();
        if (string.Equals(laneTip, approved, StringComparison.OrdinalIgnoreCase)
            || IsAncestor(repoRoot, approved, laneTip)) return false;

        var parents = ParentsOf(repoRoot, laneTip);
        if (parents is not { Length: > 0 } || ParentsOf(repoRoot, approved) is not { } approvedParents
            || !parents.SequenceEqual(approvedParents, StringComparer.OrdinalIgnoreCase)) return false;
        var (changed, _, diffCode) = RunGitArgs(repoRoot, "diff", "--name-only", "-z", laneTip, approved);
        var paths = changed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (diffCode != 0 || paths.Length == 0
            || !paths.All(path => path.StartsWith("docs/", StringComparison.Ordinal)
                && path.EndsWith("/workbench.json", StringComparison.Ordinal))) return false;

        if (UpdateLaneRef(repoRoot, integrationBranch, approved, laneTip) is { } error)
        {
            _logger.LogWarning("Document key amend {Sha} could not be adopted on {Branch}: {Error}",
                AbbreviateSha(approved), integrationBranch, error);
            return false;
        }
        _logger.LogInformation(
            "Integration lane of {Branch} moved from {LaneTip} to its document key amend {Sha}",
            integrationBranch, AbbreviateSha(laneTip), AbbreviateSha(approved));
        return true;
    }

    private string[]? ParentsOf(string repoRoot, string sha)
    {
        var (raw, _, code) = RunGitArgs(repoRoot, "rev-list", "--parents", "-n", "1", sha);
        return code == 0 ? raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..] : null;
    }

    private DocumentKeyCandidateResult IncludeDocumentKeys(string worktree, string baseSha,
        string candidateSha, ProjectRecord project, ProjectRegistry registry, bool amend)
    {
        var head = ReadHeadShaAt(worktree);
        if (!string.Equals(head, candidateSha, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "Document key assignment found a different integration HEAD.");
        var (changed, diffError, diffCode) = RunGitArgs(worktree,
            "diff", "--name-only", "-z", baseSha, candidateSha, "--", "docs");
        if (diffCode != 0) return new(false, null, diffError.Trim());
        var paths = changed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!paths.Any(path => path.EndsWith("/workbench.json", StringComparison.Ordinal)))
            return new(true, candidateSha, null);
        var assigned = WorkbenchIntegrationKeyAssignment.Assign(worktree, paths, project, registry);
        if (assigned.Count == 0) return new(true, candidateSha, null);
        var (_, addError, addCode) = RunGitArgs(worktree,
            ["add", "--", .. assigned]);
        if (addCode != 0) return new(false, null, addError.Trim());
        var args = amend
            ? new[] { "-c", "user.name=Agent Studio", "-c", "user.email=agent-studio@users.noreply.github.com",
                "commit", "--amend", "--no-edit" }
            : new[] { "-c", "user.name=Agent Studio", "-c", "user.email=agent-studio@users.noreply.github.com",
                "commit", "-m", "chore(workbench): assign document keys in gated delivery" };
        var (_, commitError, commitCode) = RunGitArgs(worktree, args);
        if (commitCode != 0) return new(false, null, commitError.Trim());
        return new(true, ReadHeadShaAt(worktree), null);
    }
}
