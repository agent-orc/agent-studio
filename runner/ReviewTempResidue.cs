namespace AgentRunner;

/// <summary>
/// Removes what one review command left in its own temp directory (AGT-2858).
///
/// The attempt workspace already points <c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c> and
/// <c>MSBUILDDEBUGPATH</c> at <c>review-work/&lt;attempt&gt;/tmp</c>, so a verify
/// command's residue - test fixture directories, MSBuild node files, the
/// diagnostic sockets of killed test hosts - never reaches the host's shared
/// <c>/tmp</c>. What it did not do is bound the residue *inside* the attempt: a
/// frozen plan runs preparation, build, several test commands and the semantic
/// aspects one after another, and every one of them added to the same directory
/// until the whole attempt workspace was finally deleted.
///
/// So each command's temp directory is emptied once that command's process tree
/// is gone. The directory itself stays: it is the value of the environment
/// variables the next command inherits.
/// </summary>
internal static class ReviewTempResidue
{
    /// <summary>
    /// Empties <paramref name="tempPath"/>, keeping the directory. Best-effort
    /// by construction: an entry a still-unwinding process holds is counted as
    /// retained rather than failing the command that already produced a verdict.
    /// </summary>
    internal static ReviewTempPurge Purge(string? tempPath, string attemptRoot)
    {
        if (!IsInside(tempPath, attemptRoot)) return ReviewTempPurge.None;
        if (!Directory.Exists(tempPath)) return ReviewTempPurge.None;

        var removed = 0;
        var retained = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(tempPath!).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ReviewTempPurge.None;
        }

        foreach (var entry in entries)
        {
            if (TryRemove(entry)) removed++;
            else retained++;
        }
        return new ReviewTempPurge(removed, retained);
    }

    /// <summary>
    /// Fence for <see cref="Purge"/>: a temp path is only ever emptied when it
    /// lies strictly below the attempt root. A command that carries a foreign or
    /// host-shared <c>TMPDIR</c> - or none at all - is left alone, because this
    /// executor does not own what is in it.
    ///
    /// Pure, and the only branching decision here, so it is tested as a matrix
    /// rather than through a workspace.
    /// </summary>
    internal static bool IsInside(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;

        string fullCandidate;
        string fullRoot;
        try
        {
            fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (fullRoot.Length == 0) return false;
        // The attempt root itself is not a temp directory, and a sibling whose
        // name merely starts with the root ("...-before-agt2787") is not inside it.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullCandidate.Length > fullRoot.Length
               && fullCandidate.StartsWith(fullRoot, comparison)
               && fullCandidate[fullRoot.Length] == Path.DirectorySeparatorChar;
    }

    private static bool TryRemove(string entry)
    {
        try
        {
            // Sockets and FIFOs (clr-debug-pipe, dotnet-diagnostic) report as
            // files, so the directory check is what separates the two deletes.
            if (Directory.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
            {
                ResilientDirectory.Delete(entry);
                return !Directory.Exists(entry);
            }

            File.Delete(entry);
            return !File.Exists(entry) && !Directory.Exists(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Outcome of one <see cref="ReviewTempResidue.Purge"/> call.</summary>
internal readonly record struct ReviewTempPurge(int Removed, int Retained)
{
    internal static readonly ReviewTempPurge None = new(0, 0);

    /// <summary>True when the purge touched anything worth logging.</summary>
    internal bool Observed => Removed > 0 || Retained > 0;
}
