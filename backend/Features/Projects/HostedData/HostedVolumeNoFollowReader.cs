using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Projects;

/// <summary>Reads Linux volume entries through pinned directory descriptors, never through a checked path.</summary>
internal static class HostedVolumeNoFollowReader
{
    private const int AtCurrentDirectory = -100;
    private const int DirectoryFlag = 0x10000;
    private const int NoFollowFlag = 0x20000;
    private const int CloseOnExecFlag = 0x80000;
    private const int NonBlockingFlag = 0x800;

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, string name, int flags);

    internal static async Task WriteEntriesAsync(string volumeRoot, HostedVolumeKey key, ZipArchive zip,
        Action<string>? beforeSourceOpen, Func<string, Stream>? sourceOverride, CancellationToken token)
    {
        using var root = OpenDirectory(AtCurrentDirectory, volumeRoot);
        using var project = OpenDirectory(root, key.Project);
        using var environment = OpenDirectory(project, key.Environment);
        using var volume = OpenDirectory(environment, key.Volume);
        await WriteDirectoryAsync(volume, Path.Combine(volumeRoot, key.Project, key.Environment, key.Volume),
            "", zip, beforeSourceOpen, sourceOverride, token);
    }

    private static async Task WriteDirectoryAsync(SafeFileHandle directory, string logicalPath, string prefix,
        ZipArchive zip, Action<string>? beforeSourceOpen, Func<string, Stream>? sourceOverride, CancellationToken token)
    {
        // /proc/self/fd pins enumeration to this open directory even if its path is replaced.
        var descriptorPath = "/proc/self/fd/" + directory.DangerousGetHandle().ToInt64();
        foreach (var path in Directory.EnumerateFileSystemEntries(descriptorPath))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name is "." or ".." || name.Contains('\\'))
                throw new InvalidDataException("A declared volume contains an unsafe path.");
            var relative = prefix + name;
            var logicalEntry = Path.Combine(logicalPath, name);
            var childDirectory = TryOpenDirectory(directory, name);
            if (childDirectory is not null)
            {
                using (childDirectory)
                {
                    zip.CreateEntry(relative + "/");
                    await WriteDirectoryAsync(childDirectory, logicalEntry, relative + "/", zip,
                        beforeSourceOpen, sourceOverride, token);
                }
                continue;
            }

            beforeSourceOpen?.Invoke(logicalEntry);
            using var file = OpenFile(directory, name);
            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
            await using var input = sourceOverride is null
                ? new FileStream(file, FileAccess.Read, 81920, isAsync: false)
                : sourceOverride(logicalEntry);
            if (!input.CanSeek && sourceOverride is null)
                throw new InvalidDataException("A declared volume contains a non-regular file.");
            await using var compressed = entry.Open();
            await input.CopyToAsync(compressed, token);
        }
    }

    private static SafeFileHandle OpenDirectory(int parent, string name)
    {
        var descriptor = OpenAt(parent, name, DirectoryFlag | NoFollowFlag | CloseOnExecFlag);
        if (descriptor < 0) throw OpenError(name);
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name)
        => OpenDirectory((int)parent.DangerousGetHandle(), name);

    private static SafeFileHandle? TryOpenDirectory(SafeFileHandle parent, string name)
    {
        var descriptor = OpenAt((int)parent.DangerousGetHandle(), name,
            DirectoryFlag | NoFollowFlag | CloseOnExecFlag);
        if (descriptor >= 0) return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (Marshal.GetLastPInvokeError() == 20) return null; // ENOTDIR, a regular file candidate.
        throw OpenError(name);
    }

    private static SafeFileHandle OpenFile(SafeFileHandle parent, string name)
    {
        var descriptor = OpenAt((int)parent.DangerousGetHandle(), name,
            NoFollowFlag | CloseOnExecFlag | NonBlockingFlag);
        if (descriptor < 0) throw OpenError(name);
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static Exception OpenError(string name)
    {
        var error = Marshal.GetLastPInvokeError();
        return error is 40 or 20 // ELOOP or ENOTDIR at a required directory.
            ? new InvalidDataException("A declared volume contains a symbolic link or invalid directory.")
            : new IOException($"Cannot open declared volume entry '{name}' (errno {error}).");
    }
}
