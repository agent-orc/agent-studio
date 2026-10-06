using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>
/// The host salvage directory: worktree tarballs written by the retired host
/// snapshot script as <c>[&lt;project&gt;-]&lt;card&gt;[-&lt;suffix&gt;]-&lt;stamp&gt;.tgz</c>.
/// Only top-level regular files with that name shape are retention candidates;
/// every other entry is measured for the report but never deleted.
/// </summary>
internal static partial class SalvageTarballStore
{
    // Stamp is HHMM (snapshot script) or yyyyMMdd-HHmmss (manual salvage). The
    // optional project prefix is tried first, so PROJ-002-AGT-2177-1430 resolves
    // to project PROJ-002 and card AGT-2177.
    [GeneratedRegex(
        @"^(?:(?<project>[A-Za-z][A-Za-z0-9]*-\d+)-)?(?<card>[A-Za-z][A-Za-z0-9]*-\d+)(?:-[A-Za-z][A-Za-z0-9-]*?)?-(?:\d{4}|\d{8}-\d{6})\.tgz$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TarballName();

    public static bool TryParseName(string fileName, out string? projectId, out string cardKey)
    {
        var match = TarballName().Match(fileName);
        if (!match.Success)
        {
            projectId = null;
            cardKey = string.Empty;
            return false;
        }
        projectId = match.Groups["project"].Success
            ? match.Groups["project"].Value.ToUpperInvariant()
            : null;
        cardKey = SalvageRetentionPolicy.NormalizeCardKey(match.Groups["card"].Value);
        return true;
    }

    /// <summary>Measure the whole directory and list the retention candidates.</summary>
    public static SalvageTarballInventory Inventory(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            return new SalvageTarballInventory(fullRoot, false, 0, 0, 0, null, null, []);

        long size = 0;
        var count = 0;
        var unrecognized = 0;
        string? oldest = null;
        DateTime? oldestAt = null;
        var tarballs = new List<SalvageEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(fullRoot))
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            count++;
            var modified = info.LastWriteTimeUtc;
            if (oldestAt is null || modified < oldestAt)
            {
                oldestAt = modified;
                oldest = info.Name;
            }

            var isRegularFile = info is FileInfo && (info.Attributes & FileAttributes.ReparsePoint) == 0;
            var entrySize = info switch
            {
                FileInfo file when isRegularFile => file.Length,
                DirectoryInfo directory when (directory.Attributes & FileAttributes.ReparsePoint) == 0
                    => DirectorySize(directory),
                _ => 0,
            };
            size += entrySize;

            if (isRegularFile && TryParseName(info.Name, out var projectId, out var cardKey))
            {
                tarballs.Add(new SalvageEntry(
                    SalvageEntryKind.Tarball, info.Name, projectId, cardKey, modified, entrySize));
            }
            else
            {
                unrecognized++;
            }
        }

        return new SalvageTarballInventory(fullRoot, true, size, count, unrecognized, oldest, oldestAt, tarballs);
    }

    /// <summary>
    /// Delete one tarball. The path must resolve to a regular file directly
    /// inside the salvage root; anything else is refused.
    /// </summary>
    public static bool TryDelete(string root, SalvageEntry entry, out string? error)
    {
        error = null;
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, entry.Id));
        if (entry.Kind != SalvageEntryKind.Tarball
            || !string.Equals(Path.GetDirectoryName(path), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            error = "entry is not a tarball directly inside the salvage root";
            return false;
        }
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return true;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                error = "entry is a link";
                return false;
            }
            info.Delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static long DirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Sum(file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

internal sealed record SalvageTarballInventory(
    string Root,
    bool Exists,
    long SizeBytes,
    int EntryCount,
    int UnrecognizedCount,
    string? OldestEntry,
    DateTime? OldestEntryAt,
    IReadOnlyList<SalvageEntry> Tarballs);
