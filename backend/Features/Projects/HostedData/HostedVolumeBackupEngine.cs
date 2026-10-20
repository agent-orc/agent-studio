using System.IO.Compression;
using System.Security.Cryptography;

namespace AgentStudio.Projects;

public sealed record HostedVolumeKey(string Project, string Environment, string Volume);
public sealed record HostedVolumeBackup(string Id, HostedVolumeKey Volume, DateTimeOffset CreatedAt, string Sha256, long Bytes);

/// <summary>Host integration seam: an implementation must access the declared volume on its owning host.</summary>
public interface IHostedVolumeBackupEngine
{
    Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken);
    Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken);
}

public sealed class UnboundHostedVolumeBackupEngine : IHostedVolumeBackupEngine
{
    public Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The volume host backup adapter is not configured.");

    public Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The volume host backup adapter is not configured.");
}

/// <summary>Filesystem adapter for a co-located host. The run-zone agent can implement the same interface remotely.</summary>
public sealed class LocalHostedVolumeBackupEngine(string volumeRoot, string backupRoot) : IHostedVolumeBackupEngine
{
    public string VolumePath(HostedVolumeKey key)
        => Path.Combine(volumeRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume));

    public async Task<HostedVolumeBackup> BackupAsync(HostedVolumeKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var source = VolumePath(key);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Declared volume is absent on the host.");
        EnsureNoLinks(source);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(backupRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, id + ".zip");
        var temporary = target + ".tmp";
        try
        {
            ZipFile.CreateFromDirectory(source, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
            cancellationToken.ThrowIfCancellationRequested();
            var digest = await Digest(temporary, cancellationToken);
            File.Move(temporary, target);
            return new(id, key, now, digest, new FileInfo(target).Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task RestoreAsync(HostedVolumeBackup backup, CancellationToken cancellationToken)
    {
        var key = backup.Volume;
        var archive = Path.Combine(backupRoot, Segment(key.Project), Segment(key.Environment), Segment(key.Volume), backup.Id + ".zip");
        if (!Guid.TryParseExact(backup.Id, "N", out _) || !File.Exists(archive))
            throw new FileNotFoundException("Backup archive is absent.");
        if (!string.Equals(await Digest(archive, cancellationToken), backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup checksum does not match.");
        var destination = VolumePath(key);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = destination + ".restore-" + Guid.NewGuid().ToString("N");
        var previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
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
            if (Directory.Exists(destination))
            {
                EnsureNoLinks(destination);
                Directory.Move(destination, previous);
                moved = true;
            }
            Directory.Move(staging, destination);
            if (moved) Directory.Delete(previous, recursive: true);
        }
        catch
        {
            if (moved && !Directory.Exists(destination)) Directory.Move(previous, destination);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

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

    private static void EnsureNoLinks(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A declared volume is a symbolic link.");
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A declared volume contains a symbolic link.");
    }
}
