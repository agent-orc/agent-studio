using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Projects;

public sealed record HostedVolumeKey(string Project, string Environment, string Volume);
public sealed record HostedVolumeBackup(string Id, HostedVolumeKey Volume, DateTimeOffset CreatedAt, string Sha256, long Bytes);

/// <summary>Host integration seam: an implementation must access the declared volume on its owning host.</summary>
public interface IHostedVolumeBackupEngine
{
    Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken);
    Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken);
    Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds, CancellationToken cancellationToken);
}

public sealed class UnboundHostedVolumeBackupEngine : IHostedVolumeBackupEngine
{
    public Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The volume host backup adapter is not configured.");

    public Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The volume host backup adapter is not configured.");

    public Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The volume host backup adapter is not configured.");
}

/// <summary>Filesystem adapter for a co-located host. The run-zone agent can implement the same interface remotely.</summary>
public sealed class LocalHostedVolumeBackupEngine : IHostedVolumeBackupEngine
{
    private readonly string _volumeRoot;
    private readonly string _backupRoot;
    private readonly ILogger<LocalHostedVolumeBackupEngine> _logger;
    private readonly Action<string> _deletePrevious;
    private readonly Func<string, Stream>? _openSource;
    private readonly Action<string>? _beforeOpenSource;
    private readonly TimeProvider _clock;
    internal static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(10);

    public LocalHostedVolumeBackupEngine(string volumeRoot, string backupRoot,
        ILogger<LocalHostedVolumeBackupEngine>? logger = null, TimeProvider? clock = null)
        : this(volumeRoot, backupRoot, logger ?? NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            path => Directory.Delete(path, recursive: true), clock ?? TimeProvider.System) { }

    internal LocalHostedVolumeBackupEngine(string volumeRoot, string backupRoot,
        ILogger<LocalHostedVolumeBackupEngine> logger, Action<string> deletePrevious, TimeProvider? clock = null,
        Func<string, Stream>? openSource = null, Action<string>? beforeOpenSource = null)
    {
        _volumeRoot = volumeRoot;
        _backupRoot = backupRoot;
        _logger = logger;
        _deletePrevious = deletePrevious;
        _clock = clock ?? TimeProvider.System;
        _openSource = openSource;
        _beforeOpenSource = beforeOpenSource;
        RecoverPendingRestores();
    }

    public string VolumePath(HostedVolumeKey key)
        => Path.Combine(_volumeRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume));

    public async Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(BackupTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var source = VolumePath(key);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Declared volume is absent on the host.");
        if (!OperatingSystem.IsLinux()) EnsureNoLinks(source, token);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_backupRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, id + ".zip");
        var temporary = target + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                if (OperatingSystem.IsLinux())
                    await WriteContainedEntriesAsync(zip, key, source, token);
                else
                    await WriteCheckedEntriesAsync(zip, source, token);
            }
            token.ThrowIfCancellationRequested();
            var digest = await Digest(temporary, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target);
            return new(id, key, now, digest, new FileInfo(target).Length);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Hosted volume backup exceeded its time limit.", ex);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task WriteContainedEntriesAsync(ZipArchive zip, HostedVolumeKey key, string source,
        CancellationToken token)
    {
        using var root = OpenDirectory(_volumeRoot);
        using var project = OpenDirectory(root, Segment(key.Project));
        using var environment = OpenDirectory(project, Segment(key.Environment));
        using var volume = OpenDirectory(environment, Segment(key.Volume));
        await WriteDirectoryAsync(zip, volume, source, "", token);
    }

    private async Task WriteDirectoryAsync(ZipArchive zip, SafeFileHandle directory, string source,
        string prefix, CancellationToken token)
    {
        var directoryPath = $"/proc/self/fd/{directory.DangerousGetHandle()}";
        foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var relative = prefix + name;
            _beforeOpenSource?.Invoke(relative);
            if (TryOpenDirectory(directory, name, out var child))
            {
                using (child)
                {
                    zip.CreateEntry(relative + "/");
                    await WriteDirectoryAsync(zip, child, source, relative + "/", token);
                }
                continue;
            }
            using var file = OpenFile(directory, name);
            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
            await using var input = _openSource?.Invoke(Path.Combine(source, relative))
                ?? new FileStream(file, FileAccess.Read, 81920, isAsync: false);
            await using var compressed = entry.Open();
            await input.CopyToAsync(compressed, token);
        }
    }

    private async Task WriteCheckedEntriesAsync(ZipArchive zip, string source, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (Directory.Exists(path))
            {
                zip.CreateEntry(relative + "/");
                continue;
            }
            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
            await using var input = _openSource?.Invoke(path)
                ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var compressed = entry.Open();
            await input.CopyToAsync(compressed, token);
        }
    }

    private const int OpenReadOnly = 0;
    private const int OpenNonBlock = 0x800;
    private const int OpenDirectoryFlag = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int NotDirectory = 20;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNative(int directory, string name, int flags);

    private static SafeFileHandle OpenDirectory(string path)
    {
        var fd = OpenNative(path, OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
        if (fd < 0) throw new IOException($"Cannot open declared volume root: {Marshal.GetLastPInvokeError()}");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name)
    {
        if (!TryOpenDirectory(parent, name, out var directory))
            throw new InvalidDataException("A declared volume identity path is not a directory or is a symbolic link.");
        return directory;
    }

    private static bool TryOpenDirectory(SafeFileHandle parent, string name, out SafeFileHandle directory)
    {
        var fd = OpenAtNative(parent.DangerousGetHandle().ToInt32(), name,
            OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec);
        if (fd >= 0)
        {
            directory = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != NotDirectory) throw new IOException($"Cannot open declared volume directory entry: {error}");
        directory = null!;
        return false;
    }

    private static SafeFileHandle OpenFile(SafeFileHandle parent, string name)
    {
        var fd = OpenAtNative(parent.DangerousGetHandle().ToInt32(), name,
            OpenReadOnly | OpenNonBlock | OpenNoFollow | OpenCloseOnExec);
        if (fd < 0) throw new IOException($"Cannot open declared volume file without following links: {Marshal.GetLastPInvokeError()}");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    public Task PruneBackupsAsync(HostedVolumeKey key, IReadOnlySet<string> retainedIds, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_backupRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume));
        if (!Directory.Exists(directory)) return Task.CompletedTask;
        foreach (var archive in Directory.EnumerateFiles(directory, "*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!retainedIds.Contains(Path.GetFileNameWithoutExtension(archive))) File.Delete(archive);
        }
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
    {
        var key = backup.Volume;
        var archive = Path.Combine(_backupRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume), backup.Id + ".zip");
        if (!Guid.TryParseExact(backup.Id, "N", out _) || !File.Exists(archive))
            throw new FileNotFoundException("Backup archive is absent.");
        if (!string.Equals(await Digest(archive, cancellationToken), backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup checksum does not match.");
        var destination = VolumePath(key);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var pending = MarkerPath(destination);
        if (File.Exists(pending))
        {
            RecoverPendingRestore(pending);
            if (File.Exists(pending))
                throw new IOException("Previous volume restore cleanup is still pending.");
        }
        var restoreId = Guid.NewGuid().ToString("N");
        var staging = destination + ".restore-" + restoreId;
        var previous = destination + ".previous-" + restoreId;
        Directory.CreateDirectory(staging);
        var moved = false;
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = entry.FullName.Replace('\\', '/');
                    if (relative.StartsWith('/') || relative.Split('/').Any(part => part is "." or ".."))
                        throw new InvalidDataException("Backup has an unsafe path.");
                    var output = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("Backup escapes the declared volume.");
                    if (entry.Name.Length == 0) Directory.CreateDirectory(output);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        entry.ExtractToFile(output);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                EnsureNoLinks(destination);
                MoveLiveAside(destination, restoreId);
                moved = true;
            }
            Directory.Move(staging, destination);
        }
        catch
        {
            if (moved && !Directory.Exists(destination)) Directory.Move(previous, destination);
            if (Directory.Exists(destination)) File.Delete(MarkerPath(destination));
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        // The replacement is committed. Cleanup cannot turn a completed restore into a failed response.
        if (moved)
        {
            try
            {
                _deletePrevious(previous);
                File.Delete(MarkerPath(destination));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Restored volume, but could not remove previous directory {Previous}", previous);
            }
        }
    }

    internal void MoveLiveAside(string destination, string restoreId)
    {
        var marker = MarkerPath(destination);
        var temporary = marker + ".tmp-" + restoreId;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true))
            {
                writer.Write(restoreId);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, marker);
            Directory.Move(destination, destination + ".previous-" + restoreId);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void RecoverPendingRestores()
    {
        if (!Directory.Exists(_volumeRoot)) return;
        if ((File.GetAttributes(_volumeRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Volume root is a symbolic link.");
        foreach (var project in Directory.EnumerateDirectories(_volumeRoot))
        {
            if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Volume identity directory is a symbolic link.");
            foreach (var environment in Directory.EnumerateDirectories(project))
            {
                if ((File.GetAttributes(environment) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Volume identity directory is a symbolic link.");
                foreach (var marker in Directory.EnumerateFiles(environment, "*.restore-pending"))
                    RecoverPendingRestore(marker);
            }
        }
    }

    private void RecoverPendingRestore(string marker)
    {
        var destination = marker[..^".restore-pending".Length];
        Segment(Path.GetFileName(destination));
        var restoreId = File.ReadAllText(marker);
        if (!Guid.TryParseExact(restoreId, "N", out _)) throw new InvalidDataException("Invalid pending volume restore marker.");
        var previous = destination + ".previous-" + restoreId;
        var staging = destination + ".restore-" + restoreId;
        if (!Directory.Exists(destination))
        {
            if (!Directory.Exists(previous)) throw new InvalidDataException("Pending volume restore has no live or previous directory.");
            Directory.Move(previous, destination);
        }
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        if (Directory.Exists(previous))
        {
            try { _deletePrevious(previous); }
            catch (Exception ex) { _logger.LogWarning(ex, "Recovered volume, but could not remove previous directory {Previous}", previous); }
        }
        if (!Directory.Exists(previous)) File.Delete(marker);
    }

    private static string MarkerPath(string destination) => destination + ".restore-pending";

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' '))
            || value is "." or "..")
            throw new ArgumentException("Invalid volume identity.", nameof(value));
        return value;
    }

    private static async Task<string> Digest(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void EnsureNoLinks(string root, CancellationToken cancellationToken = default)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A declared volume is a symbolic link.");
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A declared volume contains a symbolic link.");
        }
    }
}
