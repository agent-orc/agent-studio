using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Projects;

/// <summary>Reads a Linux volume through directory handles so path swaps cannot redirect backup reads.</summary>
internal static class HostedVolumeSafeReader
{
    private const int AtFdcwd = -100;
    private const int ReadOnly = 0;
    private const int NonBlock = 0x800;
    private const int DirectoryFlag = 0x10000;
    private const int NoFollow = 0x20000;
    private const int CloseOnExec = 0x80000;

    public static async Task AddEntriesAsync(string root, ZipArchive zip, CancellationToken cancellationToken,
        Func<string, Stream, Stream> openSource, Action<string>? beforeOpen)
    {
        if (OperatingSystem.IsLinux())
        {
            using var directory = OpenRoot(root);
            await AddLinuxEntriesAsync(directory, root, "", zip, cancellationToken, openSource, beforeOpen);
            return;
        }

        // The co-located host is Linux. Keep the existing portable path for local development tests.
        EnsureNoLinks(root, cancellationToken);
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (System.IO.Directory.Exists(path)) zip.CreateEntry(relative + "/");
            else
            {
                beforeOpen?.Invoke(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A declared volume contains a symbolic link.");
                await using var safe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await CopyEntryAsync(zip, relative, openSource(path, safe), cancellationToken);
            }
        }
    }

    private static async Task AddLinuxEntriesAsync(SafeFileHandle directory, string path, string prefix,
        ZipArchive zip, CancellationToken cancellationToken, Func<string, Stream, Stream> openSource,
        Action<string>? beforeOpen)
    {
        foreach (var entryPath in System.IO.Directory.EnumerateFileSystemEntries($"/proc/self/fd/{directory.DangerousGetHandle()}"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entryPath);
            var fullPath = Path.Combine(path, name);
            var relative = prefix + name;
            beforeOpen?.Invoke(fullPath);
            var childDirectory = TryOpenAt(directory, name, ReadOnly | DirectoryFlag | NoFollow | CloseOnExec);
            if (childDirectory is not null)
            {
                using (childDirectory)
                {
                    zip.CreateEntry(relative + "/");
                    await AddLinuxEntriesAsync(childDirectory, fullPath, relative + "/", zip,
                        cancellationToken, openSource, beforeOpen);
                }
                continue;
            }

            // O_NOFOLLOW is applied at the actual open, after enumeration and any path swap.
            using var handle = OpenAt(directory, name, ReadOnly | NonBlock | NoFollow | CloseOnExec);
            await using var safe = new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
            await CopyEntryAsync(zip, relative, openSource(fullPath, safe), cancellationToken);
        }
    }

    private static async Task CopyEntryAsync(ZipArchive zip, string relative, Stream input,
        CancellationToken cancellationToken)
    {
        await using (input)
        await using (var compressed = zip.CreateEntry(relative, CompressionLevel.Optimal).Open())
            await input.CopyToAsync(compressed, cancellationToken);
    }

    private static SafeFileHandle OpenRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var current = OpenAt(AtFdcwd, "/", ReadOnly | DirectoryFlag | NoFollow | CloseOnExec);
        try
        {
            foreach (var segment in full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenAt(current, segment, ReadOnly | DirectoryFlag | NoFollow | CloseOnExec);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? TryOpenAt(SafeFileHandle parent, string name, int flags)
    {
        var fd = openat((int)parent.DangerousGetHandle(), name, flags);
        return fd < 0 ? null : new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags)
        => OpenAt((int)parent.DangerousGetHandle(), name, flags);

    private static SafeFileHandle OpenAt(int parent, string name, int flags)
    {
        var fd = openat(parent, name, flags);
        if (fd < 0)
            throw new InvalidDataException($"A declared volume path could not be opened without following links (errno {Marshal.GetLastPInvokeError()}).");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static void EnsureNoLinks(string root, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A declared volume is a symbolic link.");
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A declared volume contains a symbolic link.");
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
    private static extern int openat(int dirfd, string pathname, int flags);
}
