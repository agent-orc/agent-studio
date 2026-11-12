namespace AgentStudio.Pipeline;

/// <summary>
/// A checkout-shaped copy of one concept Dossier's contract files
/// (<c>workbench.json</c>, <c>index.html</c>) at an exact commit, kept in the
/// card at <see cref="ConceptWorkbenchStore.SnapshotRoot"/>. A Remote concept
/// delivery is merged in the Studio-owned integration worktree, so the
/// developer checkout does not carry the Dossier until the integration branch
/// is released to it. The concept post-steps review the reviewed and then the
/// integrated commit through this copy, and promotion reads it without a git
/// read on its request path.
/// </summary>
public static class ConceptDossierSnapshot
{
    /// <summary>
    /// Writes the Dossier's contract files at <paramref name="commitSha"/>
    /// below <paramref name="targetRoot"/> in repository layout. A file absent
    /// from the commit is removed from the copy, so
    /// <see cref="ConceptWorkbenchContract.ReviewDirectory"/> reports it as
    /// missing. Returns false with <paramref name="failure"/> when git cannot
    /// read the commit or the copy cannot be written.
    /// </summary>
    public static bool Write(
        GitService git,
        string repositoryRoot,
        string commitSha,
        string repoRelativeDirectory,
        string targetRoot,
        out string? failure)
    {
        var rel = repoRelativeDirectory.Replace('\\', '/').Trim('/');
        string[] paths =
        [
            rel + "/" + ConceptWorkbenchContract.DescriptorFileName,
            rel + "/" + ConceptWorkbenchContract.EntryFileName,
        ];
        var files = git.ReadFilesAtRevision(repositoryRoot, commitSha, paths);
        if (files.Failure is not null)
        {
            failure = $"The Dossier at {commitSha[..Math.Min(8, commitSha.Length)]} could not be read: {files.Failure}";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var path in paths)
            {
                var target = Path.Combine(targetRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (files.Files.TryGetValue(path, out var content) && content is not null)
                    File.WriteAllText(target, content);
                else if (File.Exists(target))
                    File.Delete(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "The Dossier copy could not be written: " + ex.Message;
            return false;
        }
        failure = null;
        return true;
    }
}
