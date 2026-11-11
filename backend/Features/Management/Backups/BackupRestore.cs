using AgentStudio.Security;

namespace AgentStudio.Management;

/// <param name="Destinations">Archive source name to target directory; a source without one is skipped.</param>
/// <param name="CloneCheckouts">Clone project checkouts from their recorded origin and fetch the Studio refs from their bundles.</param>
/// <param name="EverySourceUnder">When set, a source without a destination goes to <c>&lt;this&gt;/&lt;source name&gt;</c> (restore drill).</param>
public sealed record BackupRestorePlan(
    IReadOnlyDictionary<string, string> Destinations, bool CloneCheckouts, string? EverySourceUnder = null);

public sealed record BackupRestoreResult(
    BackupManifest Manifest,
    BackupContents Contents,
    int FilesRestored,
    int DirectoriesRestored,
    int CheckoutsCloned,
    IReadOnlyList<string> SkippedSources);

/// <summary>
/// Puts a backup back onto an empty host. Every destination must be empty,
/// missing, or the fresh workspace repository from <c>setup.sh</c>. The
/// manifest comes first, so checkouts are cloned before any file is written;
/// a failed clone stops the restore with nothing restored. Every file is
/// checked against the hash list at the end of the archive; on any
/// failure the destinations are emptied again and the setup workspace is put
/// back, so a restore never leaves a half-restored installation behind and a
/// failed restore can run again on the same host.
/// </summary>
public static class BackupRestore
{
    public static BackupRestoreResult Restore(string archivePath, BackupKey key, BackupRestorePlan plan, Action<string>? log = null)
    {
        var destinations = plan.Destinations.ToDictionary(
            pair => pair.Key, pair => Path.GetFullPath(pair.Value), StringComparer.Ordinal);
        var candidates = destinations.ToList();
        if (plan.EverySourceUnder is { } stagingRoot) candidates.Add(new("staging", Path.GetFullPath(stagingRoot)));
        var setupWorkspaces = new List<string>();
        foreach (var (name, directory) in candidates)
        {
            if (!HoldsFiles(directory)) continue;
            if (IsSetupWorkspace(directory))
            {
                setupWorkspaces.Add(directory);
                continue;
            }
            throw new BackupException("restore-target-not-empty",
                $"The restore target for {name} ({directory}) is not empty; restore only into an empty installation.");
        }

        var skipped = new List<string>();
        var checkouts = new Dictionary<string, BackupCheckout>(StringComparer.Ordinal);
        string? projectsSource = null;
        var files = 0;
        var directories = 0;
        var cloned = 0;
        var bundleScratch = Path.Combine(Path.GetTempPath(), "studio-restore-bundles-" + Guid.NewGuid().ToString("N"));
        var setupHold = Path.Combine(Path.GetTempPath(), "studio-restore-setup-" + Guid.NewGuid().ToString("N"));
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var archive = File.OpenRead(archivePath);
            var (manifest, contents) = BackupArchive.Read(archive, key,
                onManifest: manifest =>
                {
                    // The key opened the archive: the archived workspace and its repository replace
                    // setup.sh's. A copy stays aside until the restore succeeds.
                    foreach (var directory in setupWorkspaces)
                    {
                        var copy = Path.Combine(setupHold, Guid.NewGuid().ToString("N"));
                        CopyTree(directory, copy);
                        held[directory] = copy;
                        Empty(directory);
                    }
                    foreach (var source in manifest.Sources.Where(source => source.Present))
                        if (!destinations.ContainsKey(source.Name) && plan.EverySourceUnder is { } under)
                            destinations[source.Name] = Path.GetFullPath(Path.Combine(under, source.Name));
                        else if (!destinations.ContainsKey(source.Name))
                        {
                            skipped.Add(source.Name);
                            log?.Invoke($"skipping source {source.Name}: no restore target");
                        }
                    var projects = manifest.Sources.FirstOrDefault(source => source.Projects);
                    if (projects is null || !destinations.TryGetValue(projects.Name, out var projectsRoot)) return;
                    projectsSource = projects.Name;
                    foreach (var checkout in manifest.Checkouts)
                    {
                        checkouts[checkout.Bundle ?? "\0" + checkout.Path] = checkout;
                        if (!plan.CloneCheckouts) continue;
                        var target = Resolve(projectsRoot, checkout.Path);
                        if (checkout.Origin is not null)
                        {
                            log?.Invoke($"cloning {checkout.Origin} into {checkout.Path}");
                            BackupCheckouts.Clone(checkout.Origin, target, checkout.Branch);
                        }
                        else
                        {
                            BackupCheckouts.Init(target);
                        }
                        cloned++;
                    }
                },
                onFile: (member, data, mode) =>
                {
                    if (member.StartsWith(BackupArchive.BundlePrefix, StringComparison.Ordinal))
                    {
                        if (!plan.CloneCheckouts || projectsSource is null
                            || !checkouts.TryGetValue(member, out var checkout)) return;
                        Directory.CreateDirectory(bundleScratch);
                        var bundle = Path.Combine(bundleScratch, Path.GetFileName(member));
                        using (var output = File.Create(bundle)) data.CopyTo(output);
                        var repository = Resolve(destinations[projectsSource], checkout.Path);
                        BackupCheckouts.FetchBundle(repository, bundle, allRefs: checkout.Origin is null, checkout.Branch);
                        return;
                    }
                    var (source, relative) = Split(member);
                    if (!destinations.TryGetValue(source, out var root)) return;
                    var path = Resolve(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".restore-tmp";
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                        data.CopyTo(output);
                    // Restores the recorded mode: SSH keys stay 0600, hooks stay executable.
                    if (!OperatingSystem.IsWindows() && mode != 0) File.SetUnixFileMode(temporary, mode);
                    // A file of a cloned checkout (task storage inside it) gives way to the archived copy.
                    File.Move(temporary, path, overwrite: true);
                    files++;
                },
                onDirectory: member =>
                {
                    var (source, relative) = Split(member + "/");
                    if (!destinations.TryGetValue(source, out var root)) return;
                    Directory.CreateDirectory(relative.Length == 0 ? root : Resolve(root, relative));
                    directories++;
                });
            // Only an installation restore has an explicit workspace target. A
            // scheduled verification uses EverySourceUnder and must leave the
            // archived bytes intact while checking their hashes.
            if (destinations.TryGetValue("workspace", out var workspace) && plan.EverySourceUnder is null)
                RestoreSecurityNotice.MarkRestored(workspace, manifest.Id, manifest.CreatedAt);
            return new BackupRestoreResult(manifest, contents, files, directories, cloned, skipped);
        }
        catch
        {
            // A setup workspace that was not set aside yet is untouched: nothing was written.
            foreach (var directory in destinations.Values)
                if (!setupWorkspaces.Contains(directory) || held.ContainsKey(directory)) Empty(directory);
            foreach (var (directory, copy) in held) CopyTree(copy, directory);
            throw;
        }
        finally
        {
            if (Directory.Exists(bundleScratch)) Directory.Delete(bundleScratch, recursive: true);
            if (Directory.Exists(setupHold)) Directory.Delete(setupHold, recursive: true);
        }
    }

    internal static (string Source, string Relative) Split(string member)
    {
        var slash = member.IndexOf('/');
        return slash < 0 ? (member, "") : (member[..slash], member[(slash + 1)..].TrimEnd('/'));
    }

    internal static string Resolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) && full != root)
            throw new BackupException("backup-corrupt", $"Archive path {relative} leaves its restore target.");
        return full;
    }

    /// <summary>
    /// A target counts as empty while it holds no file: <c>setup.sh</c> lays
    /// out empty directories (<c>home/.local/share</c>) before a restore.
    /// </summary>
    private static bool HoldsFiles(string directory)
        => Directory.Exists(directory)
           && Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
               .Any(entry => !Directory.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0);

    /// <summary>
    /// The workspace <c>setup.sh</c> lays out for a new host: a Git repository
    /// with at most the installer's single commit of <c>.gitignore</c>, a
    /// clean working tree, and nothing else. It holds no installation data, so
    /// a restore replaces it.
    /// </summary>
    internal static bool IsSetupWorkspace(string directory)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToArray();
        if (!entries.All(entry => entry is ".git" or ".gitignore")) return false;
        var git = Path.Combine(directory, ".git");
        if (!Directory.Exists(git) || (File.GetAttributes(git) & FileAttributes.ReparsePoint) != 0) return false;
        var commits = BackupCheckouts.Git(directory, ["rev-list", "--all", "--count"]);
        if (!commits.Success || !int.TryParse(commits.StandardOutput.Trim(), out var count) || count > 1) return false;
        var status = BackupCheckouts.Git(directory, ["status", "--porcelain", "--ignored"]);
        if (!status.Success || status.StandardOutput.Trim().Length > 0) return false;
        if (count == 0) return true;
        var tracked = BackupCheckouts.Git(directory, ["ls-tree", "-r", "--name-only", "HEAD"]);
        return tracked.Success
               && tracked.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(path => path == ".gitignore");
    }

    /// <summary>Copies a directory tree with file modes; a setup workspace holds no links.</summary>
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void Empty(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original failure is what the caller reports.
                SilentCatch.Note(ex, "Restore cleanup after a failed restore");
            }
        }
    }
}
