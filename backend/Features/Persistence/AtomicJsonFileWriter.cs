namespace AgentStudio.Persistence;

/// <summary>
/// Injectable atomic-write boundary for small JSON state stores. Tests can
/// force a write failure without relying on platform-specific file permissions.
/// </summary>
public interface IAtomicJsonFileWriter
{
    void Write(string path, string content);

    /// <summary>
    /// Rewrites a file whose directory must already exist, and fails instead of
    /// re-creating it.
    ///
    /// <para><see cref="Write"/> creates the destination directory, which is
    /// right for a store that owns its own folder (the project registry, a
    /// settings file) but wrong for a file that lives inside a folder another
    /// writer may move or delete underneath it. A <c>task.json</c> rewrite that
    /// races a lane move re-created the vanished source folder and re-seeded it
    /// with the pre-move content, so the same task appeared in two lanes at once:
    /// the scanner then reported a duplicate id, and the next delete removed the
    /// resurrected ghost while the real folder stayed in the target lane (AGT-2867).
    /// Refusing to create the directory turns that into a clean failed write the
    /// caller already knows how to report.</para>
    ///
    /// <para>The default implementation keeps existing writers (including test
    /// doubles that only override <see cref="Write"/>) working unchanged.</para>
    /// </summary>
    void ReplaceExisting(string path, string content) => Write(path, content);
}

public sealed class AtomicJsonFileWriter : IAtomicJsonFileWriter
{
    /// <summary>
    /// How long a swap keeps retrying while concurrent readers hold the
    /// destination open. Readers of these stores are short plain opens
    /// (<c>File.ReadAllText</c>); one millisecond between attempts lets them
    /// finish. The budget is deliberately generous: losing the mutation is
    /// worse than a blocked writer, and only a reader that re-opens the file
    /// back-to-back on a saturated CPU gets anywhere near it (measured: up to
    /// ~8 s against a spinning reader with every core busy, sub-millisecond
    /// otherwise). After the budget the failure surfaces to the caller.
    /// </summary>
    private static readonly TimeSpan SwapRetryBudget = TimeSpan.FromSeconds(10);

    public void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        Swap(path, content);
    }

    /// <inheritdoc />
    public void ReplaceExisting(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // No Directory.CreateDirectory: the temp file is a sibling of the
        // destination, so a folder that a lane writer moved or deleted while we
        // were preparing the content makes the temp write fail with
        // DirectoryNotFoundException instead of resurrecting the folder.
        Swap(path, content);
    }

    private static void Swap(string path, string content)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            MoveWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Swaps the temp file into place so that CONCURRENT PLAIN READERS never
    /// observe a missing, locked, or truncated destination.
    ///
    /// <para><c>File.Move(overwrite: true)</c> is a single rename-with-replace
    /// (<c>rename(2)</c> on POSIX, <c>FileRenameInformation</c> with
    /// ReplaceIfExists on NTFS): the destination name always resolves to either
    /// the old or the new file. What it does NOT tolerate is any open handle on
    /// the destination - Windows then refuses the rename
    /// (<see cref="IOException"/> sharing violation or
    /// <see cref="UnauthorizedAccessException"/> access denied) and the writer
    /// must retry a moment later instead of dropping the mutation. Plain readers
    /// hold the file for microseconds, so the retry normally succeeds on the
    /// first or second attempt.</para>
    ///
    /// <para><c>File.Replace</c> (ReplaceFile) is the wrong tool here despite
    /// its name: Windows implements it as two renames (destination to backup,
    /// then replacement to destination), so between them the destination name
    /// does not exist and a concurrent open fails with FileNotFound - measured
    /// at roughly twenty missed opens per write under a spinning reader, versus
    /// none with the single rename - and while it holds the destination every
    /// reader is locked out (hundreds of sharing violations per write). It does
    /// win the race against a spinning reader somewhat more often, which is not
    /// worth either cost: the realistic readers of these stores never spin.</para>
    /// </summary>
    private static void MoveWithRetry(string tempPath, string path)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && started.Elapsed < SwapRetryBudget)
            {
                Thread.Sleep(1);
            }
        }
    }
}

/// <summary>Typed failure surfaced by strict project metadata writes.</summary>
public sealed class ProjectPersistenceException : IOException
{
    public ProjectPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
