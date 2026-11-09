using System.Diagnostics;
using System.Formats.Tar;
using AgentStudio.Secrets;

namespace AgentStudio.Management;

public sealed record BackupCollectRequest(
    string Id,
    DateTimeOffset CreatedAt,
    BackupOptions Options,
    BackupKey Key,
    string? SecretKeyId,
    IReadOnlyCollection<Func<byte[], bool>> KeyFingerprints,
    IReadOnlyList<string> StorageLocations,
    string StudioVersion);

/// <summary>
/// Writes one encrypted archive from the configured sources. Ordinary HTTP
/// mutations are held while the stores are read (<see cref="BackupWritePause"/>),
/// at most <see cref="BackupOptions.MaxWriteHold"/>; every file goes through
/// <see cref="BackupSnapshotReader"/>. Compression and encryption stream, so
/// the archive's plaintext never reaches the disk; only the per-file spool
/// copy does, in the owner-only staging folder, and is deleted right after.
/// </summary>
public sealed class BackupCollector(BackupCatalog catalog, BackupWritePause pause)
{
    /// <summary>Called after each file capture; tests use it to write while the backup reads.</summary>
    public Action<string>? AfterCapture { get; init; }

    public async Task<BackupRecord> CreateAsync(BackupCollectRequest request, CancellationToken cancellationToken)
    {
        var options = request.Options;
        catalog.EnsureCreated();
        catalog.ClearInterrupted();
        OwnerOnlyAccess.CreateDirectory(catalog.StagingDirectory);
        var fileName = request.Id + BackupCatalog.ArchiveSuffix;
        var final = catalog.ArchivePath(request.Id);
        var partial = final + ".partial";
        var stats = new Stats();
        var contents = new List<BackupContentEntry>();
        long sizeBytes;
        string sha256;
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            OwnerOnlyAccess.ApplyCreateMode(fileOptions);
            await using (var file = new FileStream(partial, fileOptions))
            {
                var hashing = new BackupArchive.HashingWriteStream(file);
                var encrypted = BackupCipher.Encrypt(hashing, request.Key);
                var writer = BackupArchive.OpenWriter(encrypted);
                var hold = await pause.HoldAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                var held = Stopwatch.StartNew();
                stats.Drained = hold.Drained;
                try
                {
                    void ReleaseWhenOverdue()
                    {
                        if (stats.HoldReleased || held.Elapsed < options.MaxWriteHold) return;
                        hold.Dispose();
                        stats.HoldReleased = true;
                        stats.ReleasedEarly = true;
                        stats.HeldMilliseconds = held.ElapsedMilliseconds;
                    }

                    var (manifest, bundles) = BuildManifest(request);
                    BackupArchive.WriteJson(writer, BackupArchive.ManifestMember, manifest);
                    foreach (var (member, path) in bundles)
                    {
                        var capture = BackupSnapshotReader.Capture(path, path + ".spool");
                        BackupArchive.WriteFile(writer, member, capture.SpoolPath, request.CreatedAt, null);
                        contents.Add(new(member, capture.Length, capture.Sha256, capture.Kind.ToString()));
                        File.Delete(path);
                        File.Delete(capture.SpoolPath);
                    }
                    foreach (var source in options.Sources)
                    {
                        if (!Directory.Exists(source.Path)) continue;
                        var checkouts = source.Projects ? CheckoutRoots(source.Path) : [];
                        var roots = new List<string> { source.Path };
                        if (source.Projects)
                            roots.AddRange(request.StorageLocations.Select(Path.GetFullPath)
                                .Where(storage => checkouts.Any(root => Inside(storage, root.Root) && !root.Worktree)
                                                  && Directory.Exists(storage)));
                        foreach (var root in roots)
                            Walk(writer, request, source, root, checkouts, contents, stats, ReleaseWhenOverdue, cancellationToken);
                    }
                    if (!stats.HoldReleased) stats.HeldMilliseconds = held.ElapsedMilliseconds;
                }
                finally
                {
                    hold.Dispose();
                }

                BackupArchive.WriteJson(writer, BackupArchive.ContentsMember, new BackupContents(contents, stats.ToConsistency()));
                writer.Dispose();
                sizeBytes = hashing.BytesWritten;
                sha256 = hashing.Hash();
                file.Flush(flushToDisk: true);
            }
            File.Move(partial, final);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            catalog.ClearInterrupted();
            throw;
        }
        finally
        {
            if (Directory.Exists(catalog.StagingDirectory)) Directory.Delete(catalog.StagingDirectory, recursive: true);
        }

        var record = new BackupRecord(request.Id, fileName, request.CreatedAt.ToString("O"), sizeBytes, sha256,
            request.Key.Id, request.SecretKeyId, contents.Count, contents.Sum(entry => entry.Size), stats.ToConsistency());
        catalog.Save(record);
        catalog.WriteChecksum(record);
        return record;
    }

    private (BackupManifest Manifest, IReadOnlyList<(string Member, string Path)> Bundles) BuildManifest(BackupCollectRequest request)
    {
        var options = request.Options;
        var sources = new List<BackupManifestSource>();
        var checkouts = new List<BackupCheckout>();
        var bundles = new List<(string, string)>();
        foreach (var source in options.Sources)
        {
            var present = Directory.Exists(source.Path);
            if (!present && !source.Optional)
                throw new BackupException("backup-source-missing", $"Backup source {source.Name} ({source.Path}) does not exist.");
            sources.Add(new BackupManifestSource(source.Name, source.Path, source.Projects, present));
            if (!present || !source.Projects) continue;
            foreach (var (root, worktree) in BackupCheckouts.Discover(source.Path))
            {
                if (worktree) continue;
                var index = checkouts.Count + 1;
                var member = $"{BackupArchive.BundlePrefix}{index}.bundle";
                var path = Path.Combine(catalog.StagingDirectory, $"{index}.bundle");
                var checkout = BackupCheckouts.Record(source.Path, root, member, path);
                checkouts.Add(checkout);
                if (checkout.Bundle is not null) bundles.Add((member, path));
            }
        }
        var notIncluded = new List<string>
        {
            "The secret store key (Secrets:KeyFile): escrowed by the owner, only its fingerprint is recorded.",
            "The backup key (Backup:KeyFile): escrowed by the owner.",
            "Project checkout content: cloned from the recorded origin; Studio refs travel as bundles.",
            "Task worktrees: their branches come back through the checkouts.",
        };
        notIncluded.AddRange(options.Sources.SelectMany(source => DefaultAndSourceExcludes(source)
            .Select(exclude => $"{source.Name}: {exclude}")));
        return (new BackupManifest(BackupArchive.Format, request.Id, request.CreatedAt.ToString("O"), request.StudioVersion,
            Environment.MachineName, request.Key.Id, request.SecretKeyId, sources, checkouts, notIncluded), bundles);
    }

    private void Walk(TarWriter writer, BackupCollectRequest request, BackupSource source, string root,
        IReadOnlyList<(string Root, bool Worktree)> checkouts, List<BackupContentEntry> contents, Stats stats,
        Action releaseWhenOverdue, CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(ExcludedPaths(request.Options), PathComparer);
        var excludes = DefaultAndSourceExcludes(source).ToArray();
        var pending = new Stack<string>();
        pending.Push(root);
        var spool = Path.Combine(catalog.StagingDirectory, "file.spool");
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var relativeDirectory = BackupCheckouts.Relative(source.Path, directory);
            BackupArchive.WriteDirectory(writer, relativeDirectory == "." ? source.Name : $"{source.Name}/{relativeDirectory}");
            DirectoryInfo[] children;
            FileInfo[] files;
            try
            {
                var info = new DirectoryInfo(directory);
                children = info.GetDirectories();
                files = info.GetFiles();
            }
            catch (DirectoryNotFoundException) { stats.Vanished++; continue; }

            foreach (var file in files.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                releaseWhenOverdue();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { stats.Symlinks++; continue; }
                var full = file.FullName;
                var relative = BackupCheckouts.Relative(source.Path, full);
                if (excluded.Contains(full) || IsExcluded(relative, excludes)) continue;
                if (full.EndsWith(".kek", StringComparison.OrdinalIgnoreCase)
                    || BackupSnapshotReader.HoldsKeyMaterial(full, request.KeyFingerprints))
                {
                    stats.KeyFiles++;
                    continue;
                }
                if (BackupSnapshotReader.IsSqliteSidecar(full)) continue;
                var capture = BackupSnapshotReader.Capture(full, spool);
                AfterCapture?.Invoke(full);
                stats.Count(capture, $"{source.Name}/{relative}");
                if (capture.Kind == BackupCaptureKind.Vanished) continue;
                var member = $"{source.Name}/{relative}";
                BackupArchive.WriteFile(writer, member, spool, file.LastWriteTimeUtc, UnixMode(full));
                contents.Add(new(member, capture.Length, capture.Sha256, capture.Kind.ToString()));
                File.Delete(spool);
            }
            foreach (var child in children.OrderByDescending(item => item.Name, StringComparer.Ordinal))
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { stats.Symlinks++; continue; }
                var full = child.FullName;
                if (excluded.Contains(full) || IsExcluded(BackupCheckouts.Relative(source.Path, full), excludes)) continue;
                if (checkouts.Any(checkout => PathComparer.Equals(checkout.Root, full))) continue;
                pending.Push(full);
            }
        }
    }

    private static IReadOnlyList<(string Root, bool Worktree)> CheckoutRoots(string projects)
        => BackupCheckouts.Discover(projects);

    private static IEnumerable<string> DefaultAndSourceExcludes(BackupSource source)
        => BackupOptions.DefaultExclude.Concat(source.Exclude);

    private static IEnumerable<string> ExcludedPaths(BackupOptions options)
    {
        yield return options.Directory;
        if (options.KeyFile is not null) yield return options.KeyFile;
        if (options.SecretKeyFile is not null) yield return options.SecretKeyFile;
    }

    /// <summary>An entry with a slash is a relative path prefix; one without matches any path segment.</summary>
    internal static bool IsExcluded(string relative, IReadOnlyList<string> excludes)
    {
        var segments = relative.Split('/');
        foreach (var exclude in excludes)
        {
            var normalized = exclude.Trim().Trim('/');
            if (normalized.Length == 0) continue;
            if (normalized.Contains('/'))
            {
                if (relative == normalized || relative.StartsWith(normalized + "/", StringComparison.Ordinal)) return true;
            }
            else if (segments.Contains(normalized, StringComparer.Ordinal)) return true;
        }
        return false;
    }

    internal static bool Inside(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return PathComparer.Equals(full, parent)
               || full.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                   ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static UnixFileMode? UnixMode(string path)
        => OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class Stats
    {
        public bool Drained;
        public bool HoldReleased;
        public bool ReleasedEarly;
        public long HeldMilliseconds;
        public int Sqlite;
        public int Journals;
        public int Retried;
        public int Vanished;
        public int KeyFiles;
        public int Symlinks;
        public readonly List<string> Unsettled = [];

        public void Count(BackupCapture capture, string member)
        {
            if (capture.Attempts > 1) Retried++;
            switch (capture.Kind)
            {
                case BackupCaptureKind.SqliteSnapshot: Sqlite++; break;
                case BackupCaptureKind.JournalPrefix: Journals++; break;
                case BackupCaptureKind.Vanished: Vanished++; break;
                case BackupCaptureKind.Unsettled: Unsettled.Add(member); break;
            }
        }

        public BackupConsistency ToConsistency()
            => new(true, Drained, HeldMilliseconds, ReleasedEarly, Sqlite, Journals, Retried, Vanished, KeyFiles, Symlinks,
                Unsettled.ToArray());
    }
}
