using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.State;

/// <summary>
/// Startup step for machine-local state (AGT-3401). It resolves
/// <see cref="LocalStateRoot"/> before any service reads or writes below it,
/// so an unusable location stops the host instead of silently writing state
/// into the working directory. It then moves state that earlier versions wrote
/// relative to the working directory (for example <c>/app/agentstudio</c> in
/// the container layer) to the absolute root, once.
/// </summary>
public static class LocalStateStartup
{
    /// <summary>Top-level folders the hosts create below the local data directory.</summary>
    public static readonly IReadOnlyList<string> FolderNames = ["agentstudio", "agent-taskboard", "AgentStudio"];

    /// <summary>
    /// Resolves and creates the local state root, then migrates legacy relative
    /// state from <paramref name="legacyBases"/>.
    /// </summary>
    /// <exception cref="LocalStateRootUnavailableException">The root cannot be resolved or created.</exception>
    public static string Prepare(IEnumerable<string?> legacyBases, ILogger logger)
    {
        var root = LocalStateRoot.Resolve();
        logger.LogInformation("local-state-root {Root}", root);
        MigrateLegacy(root, legacyBases, logger);
        return root;
    }

    /// <summary>
    /// Moves each entry of a legacy <c>&lt;base&gt;/&lt;folder&gt;</c> directory to
    /// <c>&lt;root&gt;/&lt;folder&gt;</c> when the target does not hold it yet. An
    /// entry whose target already exists stays where it is, so a second start
    /// never overwrites newer state.
    /// </summary>
    public static IReadOnlyList<LegacyLocalStateMove> MigrateLegacy(
        string root, IEnumerable<string?> legacyBases, ILogger logger)
    {
        var moves = new List<LegacyLocalStateMove>();
        var fullRoot = Path.GetFullPath(root);
        var bases = legacyBases
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate!)))
            .Distinct(PathComparer)
            .ToList();
        foreach (var legacyBase in bases)
        {
            if (!Directory.Exists(legacyBase)) continue;
            // Enumerate the on-disk names so a case-insensitive file system does
            // not report agentstudio and AgentStudio as two folders.
            foreach (var legacyFolder in Directory.EnumerateDirectories(legacyBase))
            {
                var name = Path.GetFileName(legacyFolder);
                if (!FolderNames.Contains(name, StringComparer.Ordinal)) continue;
                var targetFolder = Path.Combine(fullRoot, name);
                var folderDecision = LegacyLocalStatePolicy.DecideFolder(
                    sameLocation: PathComparer.Equals(legacyFolder, targetFolder)
                        || IsWithin(fullRoot, legacyFolder) || IsWithin(legacyFolder, fullRoot),
                    isCheckout: File.Exists(Path.Combine(legacyFolder, ".git"))
                        || Directory.Exists(Path.Combine(legacyFolder, ".git")));
                if (folderDecision == LegacyLocalStateFolderDecision.NotLegacy) continue;
                if (folderDecision == LegacyLocalStateFolderDecision.SkipCheckout)
                {
                    logger.LogWarning("local-state-migration-skipped {Source}: a Git checkout, not host state", legacyFolder);
                    continue;
                }
                foreach (var source in Directory.EnumerateFileSystemEntries(legacyFolder).ToList())
                {
                    var target = Path.Combine(targetFolder, Path.GetFileName(source));
                    var targetExists = File.Exists(target) || Directory.Exists(target);
                    if (LegacyLocalStatePolicy.DecideEntry(targetExists) == LegacyLocalStateEntryDecision.KeepTarget)
                    {
                        logger.LogWarning(
                            "local-state-migration-kept-target {Source} stays because {Target} already exists",
                            source, target);
                        continue;
                    }
                    try
                    {
                        Move(source, target);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException(
                            $"Moving legacy local state from '{source}' to '{target}' failed: {exception.Message} "
                            + "Move it by hand, then start again.", exception);
                    }
                    moves.Add(new LegacyLocalStateMove(source, target));
                    logger.LogInformation("local-state-migrated {Source} to {Target}", source, target);
                }
                if (!Directory.EnumerateFileSystemEntries(legacyFolder).Any())
                    Directory.Delete(legacyFolder);
            }
        }
        return moves;
    }

    private static void Move(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(source))
        {
            // File.Move copies and deletes across file systems on its own.
            File.Move(source, target);
            return;
        }
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // The old location is usually the container layer and the new one a
            // mounted volume, where rename(2) fails. Copy into a staging name on
            // the target file system, then publish it with one rename so a crash
            // never leaves a partial target that would block the next attempt.
            var staging = target + ".migrating-" + Guid.NewGuid().ToString("N");
            CopyDirectory(source, staging);
            Directory.Move(staging, target);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static bool IsWithin(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>One legacy entry moved below the local state root.</summary>
public sealed record LegacyLocalStateMove(string Source, string Target);

public enum LegacyLocalStateFolderDecision
{
    Migrate,
    /// <summary>The folder already is, or overlaps, the local state root.</summary>
    NotLegacy,
    /// <summary>A repository checkout that happens to share a state folder name.</summary>
    SkipCheckout,
}

public enum LegacyLocalStateEntryDecision
{
    Move,
    /// <summary>The root already holds this entry; newer state wins.</summary>
    KeepTarget,
}

/// <summary>Pure decisions of the legacy local state migration.</summary>
public static class LegacyLocalStatePolicy
{
    public static LegacyLocalStateFolderDecision DecideFolder(bool sameLocation, bool isCheckout)
        => sameLocation ? LegacyLocalStateFolderDecision.NotLegacy
            : isCheckout ? LegacyLocalStateFolderDecision.SkipCheckout
            : LegacyLocalStateFolderDecision.Migrate;

    public static LegacyLocalStateEntryDecision DecideEntry(bool targetExists)
        => targetExists ? LegacyLocalStateEntryDecision.KeepTarget : LegacyLocalStateEntryDecision.Move;
}
