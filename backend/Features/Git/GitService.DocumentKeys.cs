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

    /// <summary>Amends a freshly created integration merge before its gate and journal record.</summary>
    public DocumentKeyCandidateResult IncludeDocumentKeysInIntegrationMerge(string repoRoot,
        string baseSha, string mergedSha, ProjectRecord project, ProjectRegistry registry)
        => IncludeDocumentKeys(repoRoot, baseSha, mergedSha, project, registry, amend: true);

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
