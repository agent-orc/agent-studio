using System.Text.RegularExpressions;
using AgentStudio.Diagnostics;
using AgentStudio.Registry;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>One location to describe: the paths it consists of and the setting that selects them.</summary>
public sealed record StructureDataLocationSpec(string Role, string Label, string SourceRef, IReadOnlyList<string> Paths,
    bool GitCheckouts);

/// <summary>
/// The locations on the Studio server that hold state (AGT-3954): workspace
/// repository, project checkouts, application state directory, secrets key and
/// edge certificates. Each is a fact with its size and whether a backup source
/// or a git remote covers it, so the Backup layer and assessments attach by id.
/// Sizes come from a bounded walk on the structure refresh worker, at most
/// every <see cref="MeasureInterval"/>; nothing here runs on a request.
/// </summary>
public sealed partial class StructureDataLocationService(IConfiguration configuration, ProjectRegistry projects,
    TimeProvider clock, ILogger<StructureDataLocationService> logger)
{
    public const string EdgeCertificatesKey = "Structure:EdgeCertificatesPath";
    public static readonly TimeSpan MeasureInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DescribeInterval = TimeSpan.FromMinutes(1);
    private const int MaxEntries = 300_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, (long Bytes, bool Complete, DateTime At)> _sizes = new(StringComparer.Ordinal);
    private IReadOnlyList<StructureDataLocation> _current = [];
    private DateTime _describedAt;
    private DateTime _measuredAt;
    private string? _machineId;

    /// <summary>The last described facts. Empty until the first refresh.</summary>
    public IReadOnlyList<StructureDataLocation> Current { get { lock (_gate) return _current; } }

    /// <summary>Describes when due and measures sizes when due. Called from the refresh worker only.</summary>
    public void Refresh(string machineId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        bool describe, measure;
        lock (_gate)
        {
            describe = _machineId != machineId || now - _describedAt >= DescribeInterval;
            measure = now - _measuredAt >= MeasureInterval;
        }
        if (!describe && !measure) return;
        try
        {
            var specs = Specs();
            var backup = BackupOptions.From(configuration);
            if (measure)
            {
                foreach (var spec in specs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (bytes, complete) = Size(spec.Paths, cancellationToken);
                    lock (_gate) _sizes[spec.Role] = (bytes, complete, clock.GetUtcNow().UtcDateTime);
                }
            }
            var described = specs.Select(spec => Describe(machineId, spec, backup)).ToArray();
            lock (_gate)
            {
                _current = described;
                _machineId = machineId;
                _describedAt = now;
                if (measure) _measuredAt = now;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or BackupException or LocalStateRootUnavailableException
                                              or InvalidOperationException or ArgumentException)
        { logger.LogWarning(exception, "structure-data-locations-refresh-failed"); }
    }

    internal IReadOnlyList<StructureDataLocationSpec> Specs()
    {
        var workspace = Path.GetFullPath(SecretStorePaths.WorkspaceRoot(configuration));
        var checkouts = projects.List().Where(project => !project.Archived)
            .Select(project => project.RepositoryPath ?? project.RootPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        string[] state = [];
        try
        {
            var local = LocalStateRoot.Resolve();
            state = new[] { "agent-taskboard", "agentstudio", "agent-studio", "AgentStudio" }
                .Select(folder => Path.GetFullPath(Path.Combine(local, folder))).Where(Directory.Exists)
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (LocalStateRootUnavailableException) { state = []; }
        string[] Optional(string? path) => string.IsNullOrWhiteSpace(path) ? [] : [Path.GetFullPath(path)];
        return
        [
            new("workspace-repository", "Workspace repository", "TaskRepository", [workspace], true),
            new("project-checkouts", "Project checkouts", "project registry", checkouts, true),
            new("application-state", "Application state directory", "local state root", state, false),
            new("secrets-key", "Secrets key", "Secrets:KeyFile", Optional(configuration["Secrets:KeyFile"]), false),
            new("edge-certificates", "Edge certificates", EdgeCertificatesKey, Optional(configuration[EdgeCertificatesKey]), false),
        ];
    }

    private StructureDataLocation Describe(string machineId, StructureDataLocationSpec spec, BackupOptions backup)
    {
        var existing = spec.Paths.Where(path => Directory.Exists(path) || File.Exists(path)).ToArray();
        var backedUp = backup.Configured && existing.Length > 0 && existing.All(path =>
            backup.Sources.Any(source => IsUnder(path, source.Path)));
        var remote = spec.GitCheckouts && existing.Length > 0 && existing.All(HasGitRemote);
        (long Bytes, bool Complete, DateTime At)? size;
        lock (_gate) size = _sizes.TryGetValue(spec.Role, out var known) ? known : null;
        return new($"data:{machineId}:{spec.Role}", spec.Role, spec.Label, spec.SourceRef, existing.Length > 0,
            existing.Length > 0 ? size?.Bytes : null, size?.Complete ?? false, size?.At, backedUp, remote,
            backedUp && remote ? "both" : backedUp ? "backup" : remote ? "git-remote" : "none");
    }

    internal static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(parent, comparison) || full.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>True when the checkout's git configuration names a remote with a URL. Reads one small file.</summary>
    internal static bool HasGitRemote(string checkout)
    {
        try
        {
            var git = Path.Combine(checkout, ".git");
            if (File.Exists(git))
            {
                // A worktree or submodule points at its git directory; the remotes live in the common one.
                var pointer = File.ReadLines(git).FirstOrDefault(line => line.StartsWith("gitdir:", StringComparison.Ordinal));
                if (pointer is null) return false;
                git = Path.GetFullPath(pointer["gitdir:".Length..].Trim(), checkout);
                var common = Path.Combine(git, "commondir");
                if (File.Exists(common)) git = Path.GetFullPath(File.ReadAllText(common).Trim(), git);
            }
            var config = Path.Combine(git, "config");
            if (!File.Exists(config) || new FileInfo(config).Length > 1024 * 1024) return false;
            var inRemote = false;
            foreach (var line in File.ReadLines(config))
            {
                if (Section().IsMatch(line)) inRemote = RemoteSection().IsMatch(line);
                else if (inRemote && RemoteUrl().IsMatch(line)) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    /// <summary>Bytes below the paths. Stops at <see cref="MaxEntries"/> entries and never follows links.</summary>
    internal static (long Bytes, bool Complete) Size(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        var visited = 0;
        var remaining = new Stack<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) bytes += new FileInfo(path).Length;
                else if (Directory.Exists(path)) remaining.Push(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { SilentCatch.Note(exception, "StructureDataLocations: path unreadable"); }
        }
        while (remaining.Count > 0)
        {
            if (visited >= MaxEntries || cancellationToken.IsCancellationRequested) return (bytes, false);
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(remaining.Pop()).GetFileSystemInfos(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (++visited >= MaxEntries) return (bytes, false);
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo directory) remaining.Push(directory.FullName);
                    else if (entry is FileInfo file) bytes += file.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { SilentCatch.Note(exception, "StructureDataLocations: entry unreadable"); }
            }
        }
        return (bytes, true);
    }

    [GeneratedRegex(@"^\s*\[")] private static partial Regex Section();
    [GeneratedRegex(@"^\s*\[remote\s+""[^""]+""\s*\]")] private static partial Regex RemoteSection();
    [GeneratedRegex(@"^\s*url\s*=\s*\S+")] private static partial Regex RemoteUrl();
}
