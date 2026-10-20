using System.IO.Compression;
using System.Security.Cryptography;
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
    private readonly Func<string, Stream> _openSource;
    private readonly TimeProvider _clock;
    internal static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(10);

    public LocalHostedVolumeBackupEngine(string volumeRoot, string backupRoot,
        ILogger<LocalHostedVolumeBackupEngine>? logger = null, TimeProvider? clock = null)
        : this(volumeRoot, backupRoot, logger ?? NullLogger<LocalHostedVolumeBackupEngine>.Instance,
            path => Directory.Delete(path, recursive: true), clock ?? TimeProvider.System) { }

    internal LocalHostedVolumeBackupEngine(string volumeRoot, string backupRoot,
        ILogger<LocalHostedVolumeBackupEngine> logger, Action<string> deletePrevious, TimeProvider? clock = null,
        Func<string, Stream>? openSource = null)
    {
        _volumeRoot = volumeRoot;
        _backupRoot = backupRoot;
        _logger = logger;
        _deletePrevious = deletePrevious;
        _clock = clock ?? TimeProvider.System;
        _openSource = openSource ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
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
        EnsureNoLinks(source, token);
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
                    await using var input = _openSource(path);
                    await using var compressed = entry.Open();
                    await input.CopyToAsync(compressed, token);
                }
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
                File.Delete(MarkerPath(destination));
                _deletePrevious(previous);
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
        var temporary = marker + ".tmp";
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
        foreach (var marker in Directory.EnumerateFiles(_volumeRoot, "*.restore-pending", SearchOption.AllDirectories))
        {
            var destination = marker[..^".restore-pending".Length];
            var restoreId = File.ReadAllText(marker);
            if (!Guid.TryParseExact(restoreId, "N", out _)) throw new InvalidDataException("Invalid pending volume restore marker.");
            var previous = destination + ".previous-" + restoreId;
            var staging = destination + ".restore-" + restoreId;
            if (!Directory.Exists(destination))
            {
                if (!Directory.Exists(previous)) throw new InvalidDataException("Pending volume restore has no live or previous directory.");
                Directory.Move(previous, destination);
            }
            File.Delete(marker);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(previous))
            {
                try { _deletePrevious(previous); }
                catch (Exception ex) { _logger.LogWarning(ex, "Recovered volume, but could not remove previous directory {Previous}", previous); }
            }
        }
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
