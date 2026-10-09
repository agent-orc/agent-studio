using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.CliHosting;

/// <summary>
/// Host-local platform family used when resolving the durable clean-context
/// root. The explicit value keeps Windows and XDG path policy directly
/// testable on either build host.
/// </summary>
public enum CleanContextHostPlatform
{
    Windows,
    Unix,
}

/// <summary>How one allow-listed file reached a task clean home.</summary>
public enum CleanContextSeedMethod
{
    Existing,
    HardLink,
    SymbolicLink,
    Copy,
}

/// <summary>One allow-listed credential or base-config file in a clean home.</summary>
public sealed record CleanContextSeededFile(
    string RelativePath,
    string SourcePath,
    string DestinationPath,
    CleanContextSeedMethod Method,
    bool SharedCredential);

/// <summary>Result of one bounded retention sweep.</summary>
public sealed record CleanContextCleanupResult(
    int Scanned,
    int Deleted,
    IReadOnlyList<string> FailedPaths);

/// <summary>
/// A host-owned handle for one task-stable CLI config home. Disposing releases
/// the in-process handle and refreshes its last-use marker; it deliberately does
/// not delete the directory. Retention owns deletion so a later continuation,
/// including one after a host-process restart, can reopen the CLI rollout.
/// </summary>
public sealed class TaskCleanContextLease : IDisposable
{
    private readonly string _rootPath;
    private readonly string _taskIdentityHash;
    private int _disposed;

    internal TaskCleanContextLease(
        string cliType,
        string environmentVariable,
        string rootPath,
        string homePath,
        string taskIdentityHash,
        bool reused,
        IReadOnlyList<CleanContextSeededFile> seededFiles)
    {
        CliType = cliType;
        EnvironmentVariable = environmentVariable;
        _rootPath = rootPath;
        HomePath = homePath;
        _taskIdentityHash = taskIdentityHash;
        Reused = reused;
        SeededFiles = seededFiles;
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [environmentVariable] = homePath,
        };
    }

    public string CliType { get; }
    public string EnvironmentVariable { get; }
    public string HomePath { get; }
    public bool Reused { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public IReadOnlyList<CleanContextSeededFile> SeededFiles { get; }

    /// <summary>
    /// Delete a freshly prepared home after a failed start. Normal terminal
    /// paths must call <see cref="Dispose"/> and leave cleanup to retention.
    /// </summary>
    public bool Delete()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return TaskCleanContextStore.DeleteOwnedHome(_rootPath, HomePath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        TaskCleanContextStore.TouchExisting(
            HomePath,
            CliType,
            _taskIdentityHash,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// The one clean-home composition used by the local backend and the standalone
/// Agent Host. Homes live outside OS temporary directories, are keyed by a
/// cryptographic task identity, and preserve only the CLI's auth/base-config
/// allow-list plus task-owned session state.
/// </summary>
public static class TaskCleanContextStore
{
    public const string RootOverrideEnvironmentVariable = "AGENT_STUDIO_CLEAN_CONTEXT_ROOT";
    public const int DefaultRetentionDays = 7;
    public const string MarkerFileName = ".agent-studio-clean-context.json";

    private const int MarkerVersion = 1;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions MarkerJson = new(JsonSerializerDefaults.Web);

    private static readonly CleanContextRecipe ClaudeRecipe = new(
        "claude",
        "CLAUDE_CONFIG_DIR",
        ".claude",
        [".credentials.json"],
        ["settings.json"]);

    private static readonly CleanContextRecipe CodexRecipe = new(
        "codex",
        "CODEX_HOME",
        ".codex",
        ["auth.json"],
        ["config.toml"]);

    public static TimeSpan DefaultRetention => TimeSpan.FromDays(DefaultRetentionDays);

    /// <summary>Resolve the stable root for the current host.</summary>
    public static string ResolveRoot(string? userHome = null, string? rootOverride = null)
        => ResolveRoot(
            OperatingSystem.IsWindows() ? CleanContextHostPlatform.Windows : CleanContextHostPlatform.Unix,
            userHome ?? ResolveUserHome(),
            rootOverride ?? Environment.GetEnvironmentVariable(RootOverrideEnvironmentVariable),
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"));

    /// <summary>Pure path-policy overload used by Windows and Linux tests.</summary>
    public static string ResolveRoot(
        CleanContextHostPlatform platform,
        string userHome,
        string? rootOverride,
        string? xdgStateHome)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
            return GetFullPath(platform, rootOverride.Trim());
        if (string.IsNullOrWhiteSpace(userHome))
            throw new ArgumentException("A user home is required to resolve the clean-context root.", nameof(userHome));

        if (platform == CleanContextHostPlatform.Windows)
            return GetFullPath(platform, Combine(platform, userHome, ".atp", "clean-context"));

        var stateHome = string.IsNullOrWhiteSpace(xdgStateHome)
            ? Combine(platform, userHome, ".local", "state")
            : xdgStateHome.Trim();
        return GetFullPath(platform, Combine(platform, stateHome, "agent-studio", "clean-context"));
    }

    private static string Combine(CleanContextHostPlatform platform, params string[] parts)
    {
        var separator = platform == CleanContextHostPlatform.Windows ? '\\' : '/';
        return string.Join(separator, parts.Select((part, index) => index == 0
            ? TrimTrailingSeparatorsPreservingRoot(part, separator)
            : part.Trim('\\', '/')));
    }

    private static string TrimTrailingSeparatorsPreservingRoot(string part, char separator)
    {
        var trimmed = part.TrimEnd('\\', '/');
        return trimmed.Length == 0 && part.Length > 0 ? separator.ToString() : trimmed;
    }

    private static string GetFullPath(CleanContextHostPlatform platform, string path)
    {
        var hostMatches = platform == CleanContextHostPlatform.Windows
            ? OperatingSystem.IsWindows()
            : !OperatingSystem.IsWindows();
        if (hostMatches) return Path.GetFullPath(path);

        return platform == CleanContextHostPlatform.Windows
            ? NormalizeWindowsAbsolutePath(path)
            : NormalizeUnixAbsolutePath(path);
    }

    private static string NormalizeWindowsAbsolutePath(string path)
    {
        var candidate = path.Replace('/', '\\');
        if (candidate.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var parts = candidate.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new ArgumentException("A Windows UNC path must include a server and share.", nameof(path));
            return CollapsePath($"\\\\{parts[0]}\\{parts[1]}\\", parts.Skip(2), '\\');
        }

        if (candidate.Length < 3
            || !char.IsAsciiLetter(candidate[0])
            || candidate[1] != ':'
            || candidate[2] != '\\')
        {
            throw new ArgumentException("A Windows path must be absolute.", nameof(path));
        }

        return CollapsePath(candidate[..3], candidate[3..].Split('\\'), '\\');
    }

    private static string NormalizeUnixAbsolutePath(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A Unix path must be absolute.", nameof(path));
        return CollapsePath("/", path[1..].Split('/'), '/');
    }

    private static string CollapsePath(string root, IEnumerable<string> rawParts, char separator)
    {
        var parts = new List<string>();
        foreach (var part in rawParts)
        {
            if (string.IsNullOrEmpty(part) || part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }

        return root + string.Join(separator, parts);
    }

    /// <summary>Return the deterministic, non-identifying home path for a task.</summary>
    public static string ResolveTaskHome(string rootPath, string cliType, string taskIdentity)
    {
        var recipe = ResolveRecipe(cliType);
        var key = NormalizeTaskIdentity(taskIdentity);
        var homeKey = Hash($"{recipe.CliType}\n{key}");
        return Path.Combine(Path.GetFullPath(rootPath), recipe.CliType, homeKey);
    }

    /// <summary>
    /// Acquire or adopt the task's durable home. Existing task state is never
    /// reseeded; a valid marker is required before an on-disk directory can be
    /// reused, preventing accidental cross-task adoption.
    /// </summary>
    public static TaskCleanContextLease Acquire(
        string cliType,
        string taskIdentity,
        string? userHome = null,
        string? rootOverride = null,
        DateTimeOffset? nowUtc = null,
        TimeSpan? retention = null)
    {
        var recipe = ResolveRecipe(cliType);
        var normalizedTaskIdentity = NormalizeTaskIdentity(taskIdentity);
        var resolvedUserHome = userHome ?? ResolveUserHome();
        var root = ResolveRoot(resolvedUserHome, rootOverride);
        var home = ResolveTaskHome(root, recipe.CliType, normalizedTaskIdentity);
        var taskIdentityHash = Hash(normalizedTaskIdentity);
        var now = nowUtc ?? DateTimeOffset.UtcNow;

        lock (Gate)
        {
            Cleanup(root, now, retention ?? DefaultRetention, keepHome: home);
            Directory.CreateDirectory(Path.GetDirectoryName(home)!);

            if (Directory.Exists(home))
            {
                var existing = ReadMarker(home);
                if (MarkerMatches(existing, recipe.CliType, taskIdentityHash))
                {
                    WriteMarker(home, existing! with { LastUsedAtUtc = now });
                    return BuildLease(recipe, root, home, taskIdentityHash, reused: true, resolvedUserHome);
                }

                if (Directory.EnumerateFileSystemEntries(home).Any())
                    throw new InvalidDataException(
                        $"Clean-context home '{home}' exists without the expected task marker; refusing to adopt it.");
            }
            else
            {
                Directory.CreateDirectory(home);
            }

            try
            {
                var seeded = SeedFreshHome(recipe, resolvedUserHome, home);
                WriteMarker(home, new CleanContextMarker(
                    MarkerVersion,
                    recipe.CliType,
                    taskIdentityHash,
                    now,
                    now));
                return new TaskCleanContextLease(
                    recipe.CliType,
                    recipe.EnvironmentVariable,
                    root,
                    home,
                    taskIdentityHash,
                    reused: false,
                    seeded);
            }
            catch
            {
                DeleteOwnedHome(root, home);
                throw;
            }
        }
    }

    /// <summary>
    /// Resolve a marker-validated task home without creating it. This is the
    /// pre-spawn Codex resume seam and remains effective after backend restart.
    /// </summary>
    public static bool TryGetExistingHome(
        string cliType,
        string taskIdentity,
        out string? homePath,
        string? userHome = null,
        string? rootOverride = null)
    {
        var recipe = ResolveRecipe(cliType);
        var normalizedTaskIdentity = NormalizeTaskIdentity(taskIdentity);
        var root = ResolveRoot(userHome ?? ResolveUserHome(), rootOverride);
        var home = ResolveTaskHome(root, recipe.CliType, normalizedTaskIdentity);
        var marker = ReadMarker(home);
        if (Directory.Exists(home) && MarkerMatches(marker, recipe.CliType, Hash(normalizedTaskIdentity)))
        {
            homePath = home;
            return true;
        }

        homePath = null;
        return false;
    }

    /// <summary>
    /// Rebind every marker-owned consumer after the CLI atomically replaces its
    /// native auth file. The caller must keep all provider workers drained for
    /// the entire call; hard links alone do not follow a renamed inode.
    /// </summary>
    public static int RebindProviderCredential(
        string cliType,
        string userHome,
        string rootPath,
        Func<bool> isDrained)
    {
        var recipe = ResolveRecipe(cliType);
        if (recipe.CliType is not ("codex" or "claude"))
            throw new ArgumentException("Only native provider stores can be rebound.", nameof(cliType));
        if (isDrained is null || !isDrained())
            throw new InvalidOperationException("Provider workers must drain before auth rebinding.");
        var source = Path.Combine(userHome, recipe.SourceDirectoryName, recipe.LinkedSeedFiles[0]);
        if (!File.Exists(source))
            throw new FileNotFoundException("Native provider auth store is absent.");
        var root = Path.GetFullPath(rootPath);
        var providerRoot = Path.Combine(root, recipe.CliType);
        if (!Directory.Exists(providerRoot)) return 0;
        var initialDigest = SHA256.HashData(File.ReadAllBytes(source));
        var rebound = 0;
        lock (Gate)
        {
            foreach (var home in Directory.EnumerateDirectories(providerRoot))
            {
                var marker = ReadMarker(home);
                if (marker is null || marker.Version != MarkerVersion || marker.CliType != recipe.CliType)
                    continue;
                if (!isDrained())
                    throw new InvalidOperationException("Provider workers resumed during auth rebinding.");
                var destination = Path.Combine(home, recipe.LinkedSeedFiles[0]);
                var temporary = destination + ".rebind-" + Guid.NewGuid().ToString("N");
                try
                {
                    LinkOrCopy(source, temporary);
                    File.Move(temporary, destination, overwrite: true);
                    rebound++;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            if (!isDrained() || !SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(initialDigest))
                throw new InvalidOperationException("Native auth store changed during rebinding; keep workers drained and retry.");
            foreach (var home in Directory.EnumerateDirectories(providerRoot))
            {
                var marker = ReadMarker(home);
                if (marker is null || marker.Version != MarkerVersion || marker.CliType != recipe.CliType)
                    continue;
                var destination = Path.Combine(home, recipe.LinkedSeedFiles[0]);
                if (!File.Exists(destination) ||
                    !SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(initialDigest))
                    throw new InvalidOperationException("A clean-context credential binding did not converge.");
            }
        }
        return rebound;
    }

    /// <summary>
    /// Delete expired task homes and stale incomplete directories. The sweep is
    /// bounded to two levels below the resolved root and never follows a path
    /// outside that root.
    /// </summary>
    public static CleanContextCleanupResult Cleanup(
        string rootPath,
        DateTimeOffset? nowUtc = null,
        TimeSpan? retention = null)
        => Cleanup(
            Path.GetFullPath(rootPath),
            nowUtc ?? DateTimeOffset.UtcNow,
            retention ?? DefaultRetention,
            keepHome: null);

    internal static CleanContextCleanupResult Cleanup(
        string rootPath,
        DateTimeOffset nowUtc,
        TimeSpan retention,
        string? keepHome)
    {
        if (retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        if (!Directory.Exists(rootPath)) return new CleanContextCleanupResult(0, 0, []);

        var scanned = 0;
        var deleted = 0;
        var failed = new List<string>();
        var cutoff = nowUtc - retention;

        foreach (var cliDirectory in Directory.EnumerateDirectories(rootPath))
        {
            foreach (var home in Directory.EnumerateDirectories(cliDirectory))
            {
                scanned++;
                if (keepHome != null && PathsEqual(home, keepHome)) continue;

                try
                {
                    var marker = ReadMarker(home);
                    var lastUsed = marker?.LastUsedAtUtc
                        ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(home), TimeSpan.Zero);
                    if (lastUsed >= cutoff) continue;
                    if (DeleteOwnedHome(rootPath, home)) deleted++;
                }
                catch
                {
                    failed.Add(home);
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(cliDirectory).Any())
                    Directory.Delete(cliDirectory);
            }
            catch
            {
                failed.Add(cliDirectory);
            }
        }

        return new CleanContextCleanupResult(scanned, deleted, failed.Distinct(PathComparer()).ToList());
    }

    internal static bool DeleteOwnedHome(string rootPath, string homePath)
    {
        var root = Path.GetFullPath(rootPath);
        var home = Path.GetFullPath(homePath);
        var relative = Path.GetRelativePath(root, home);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(relative)
            || segments.Length != 2
            || segments.Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"Refusing to delete clean-context path outside the owned task-home shape: {home}");
        }

        if (!Directory.Exists(home)) return false;
        Directory.Delete(home, recursive: true);
        return true;
    }

    internal static void TouchExisting(
        string home,
        string cliType,
        string taskIdentityHash,
        DateTimeOffset nowUtc)
    {
        lock (Gate)
        {
            var marker = ReadMarker(home);
            if (!MarkerMatches(marker, cliType, taskIdentityHash)) return;
            WriteMarker(home, marker! with { LastUsedAtUtc = nowUtc });
        }
    }

    private static TaskCleanContextLease BuildLease(
        CleanContextRecipe recipe,
        string root,
        string home,
        string taskIdentityHash,
        bool reused,
        string userHome)
    {
        var sourceRoot = Path.Combine(userHome, recipe.SourceDirectoryName);
        var seeded = recipe.LinkedSeedFiles
            .Select(path => ExistingSeed(path, shared: true))
            .Concat(recipe.CopiedSeedFiles.Select(path => ExistingSeed(path, shared: false)))
            .Where(seed => File.Exists(seed.DestinationPath))
            .ToList();
        return new TaskCleanContextLease(
            recipe.CliType,
            recipe.EnvironmentVariable,
            root,
            home,
            taskIdentityHash,
            reused,
            seeded);

        CleanContextSeededFile ExistingSeed(string relativePath, bool shared)
            => new(
                relativePath,
                Path.Combine(sourceRoot, relativePath),
                Path.Combine(home, relativePath),
                CleanContextSeedMethod.Existing,
                shared);
    }

    private static IReadOnlyList<CleanContextSeededFile> SeedFreshHome(
        CleanContextRecipe recipe,
        string userHome,
        string home)
    {
        var sourceRoot = Path.Combine(userHome, recipe.SourceDirectoryName);
        var seeded = new List<CleanContextSeededFile>();
        Seed(recipe.LinkedSeedFiles, shared: true);
        Seed(recipe.CopiedSeedFiles, shared: false);
        return seeded;

        void Seed(IReadOnlyList<string> relativePaths, bool shared)
        {
            foreach (var relativePath in relativePaths)
            {
                var source = Path.Combine(sourceRoot, relativePath);
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(home, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var method = shared
                    ? LinkOrCopy(source, destination)
                    : Copy(source, destination);
                seeded.Add(new CleanContextSeededFile(
                    relativePath,
                    source,
                    destination,
                    method,
                    shared));
            }
        }
    }

    private static CleanContextSeedMethod Copy(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        return CleanContextSeedMethod.Copy;
    }

    private static CleanContextSeedMethod LinkOrCopy(string source, string destination)
    {
        try
        {
            CreateHardLink(source, destination);
            return CleanContextSeedMethod.HardLink;
        }
        catch
        {
            // A cross-volume home or filesystem without hardlinks falls through
            // to a symbolic link before the final copy degradation.
        }

        try
        {
            File.CreateSymbolicLink(destination, source);
            return CleanContextSeedMethod.SymbolicLink;
        }
        catch
        {
            File.Copy(source, destination, overwrite: true);
            return CleanContextSeedMethod.Copy;
        }
    }

    private static void CreateHardLink(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(destination, source, IntPtr.Zero))
                throw new IOException(
                    $"Could not create hard link '{destination}' to '{source}'.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        if (CreateHardLinkUnix(source, destination) != 0)
            throw new IOException(
                $"Could not create hard link '{destination}' to '{source}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static CleanContextRecipe ResolveRecipe(string cliType)
        => cliType?.Trim().ToLowerInvariant() switch
        {
            "claude" => ClaudeRecipe,
            "codex" => CodexRecipe,
            _ => throw new ArgumentException(
                $"CLI '{cliType}' does not support a relocated clean-context home.",
                nameof(cliType)),
        };

    private static string NormalizeTaskIdentity(string taskIdentity)
        => string.IsNullOrWhiteSpace(taskIdentity)
            ? throw new ArgumentException("A stable task identity is required.", nameof(taskIdentity))
            : taskIdentity.Trim();

    private static string ResolveUserHome()
    {
        var environmentName = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        var home = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(home)) return Path.GetFullPath(home);
        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) return Path.GetFullPath(home);
        throw new InvalidOperationException("Could not resolve the current user's home directory.");
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static CleanContextMarker? ReadMarker(string home)
    {
        var path = Path.Combine(home, MarkerFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CleanContextMarker>(File.ReadAllText(path), MarkerJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool MarkerMatches(CleanContextMarker? marker, string cliType, string taskIdentityHash)
        => marker is
        {
            Version: MarkerVersion,
        }
        && string.Equals(marker.CliType, cliType, StringComparison.Ordinal)
        && string.Equals(marker.TaskIdentityHash, taskIdentityHash, StringComparison.Ordinal);

    private static void WriteMarker(string home, CleanContextMarker marker)
    {
        var path = Path.Combine(home, MarkerFileName);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(marker, MarkerJson));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // A marker temp file is inside the owned home and will be removed
                // by the next retention sweep. Marker replacement already failed.
            }
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingFileName, string fileName);

    private sealed record CleanContextRecipe(
        string CliType,
        string EnvironmentVariable,
        string SourceDirectoryName,
        IReadOnlyList<string> LinkedSeedFiles,
        IReadOnlyList<string> CopiedSeedFiles);

    private sealed record CleanContextMarker(
        int Version,
        string CliType,
        string TaskIdentityHash,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastUsedAtUtc);
}
