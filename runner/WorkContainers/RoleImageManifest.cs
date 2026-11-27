using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.WorkContainers;

/// <summary>One role's pinned image and the digest it rolls back to.</summary>
public sealed record RoleImagePin(string Current, string? Previous, string? Release);

/// <summary>
/// Digest-pinned role images for coding, review and gate containers. The
/// release pipeline writes the current digest; promotion keeps the prior one
/// so the host can roll back without a registry lookup. Tags are rejected:
/// a mutable tag would let two attempts of the same release run different code.
/// </summary>
public sealed record RoleImageManifest(int SchemaVersion, Dictionary<string, RoleImagePin> Roles)
{
    public static readonly string[] KnownRoles = ["coding", "review", "gate"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // registry/name@sha256:<64 hex>, or a bare local image ID for an offline host.
    private static readonly Regex PinnedReference = new(
        "^(?:[a-z0-9][a-z0-9._/:-]*@)?sha256:[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsPinned(string? reference)
        => reference is not null && PinnedReference.IsMatch(reference);

    public static RoleImageManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<RoleImageManifest>(json, Json)
                       ?? throw new InvalidDataException("Role image manifest is empty.");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported role image manifest schema {manifest.SchemaVersion}.");
        foreach (var (role, pin) in manifest.Roles ?? throw new InvalidDataException("Role image manifest has no roles."))
        {
            if (!KnownRoles.Contains(role, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown work container role '{role}'.");
            if (!IsPinned(pin.Current))
                throw new InvalidDataException($"Role '{role}' image is not digest pinned.");
            if (pin.Previous is not null && !IsPinned(pin.Previous))
                throw new InvalidDataException($"Role '{role}' rollback image is not digest pinned.");
        }
        return manifest;
    }

    public static RoleImageManifest? TryLoad(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

    // This file is installed by the root release/deployment authority, separately
    // from RUNNER_WORK_CONTAINER_IMAGES, which the service user may update.
    public const string LauncherReleasePath = "/etc/agent-host/work-container-release.json";

    /// <summary>Exact, root-controlled bare repository sources. No directory grants or prefix matching.</summary>
    public string[] Sources { get; init; } = [];

    public static async Task<RoleImageManifest> LoadLauncherReleaseAsync(string path, CancellationToken ct)
    {
        if (!await RootControlledPathAsync(path, directory: false, ct))
            throw new InvalidDataException("Launcher release state and every ancestor must be root-owned, without links or group/other write access.");
        return Parse(await File.ReadAllTextAsync(path, ct));
    }

    internal static bool CanonicalSource(string path)
        => path.StartsWith('/') && path.EndsWith(".git", StringComparison.Ordinal)
           && !path.Contains('\\') && !path.Contains(',')
           && !path.Any(char.IsControl)
           && !path.Split('/').Skip(1).Any(part => part is "" or "." or "..");

    internal static async Task<bool> RootControlledPathAsync(string path, bool directory, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(path)
            || Path.GetFullPath(path) != path || path.Split('/').Any(part => part is "." or "..")) return false;
        var paths = new List<string> { path };
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            paths.Add(parent);
        // stat does not dereference links. Check all ancestors as well as the leaf.
        var result = await ProcessRunner.RunAsync("/usr/bin/stat", ["--format=%u:%a:%F", "--", .. paths], environment: new Dictionary<string, string?> { ["LC_ALL"] = "C" }, ct: ct);
        return result.ExitCode == 0 && TrustedMetadata(result.StdOut, directory, paths.Count);
    }

    internal static bool TrustedMetadata(string metadata, bool directory, int count)
    {
        var lines = metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != count) return false;
        for (var index = 0; index < lines.Length; index++)
        {
            var fields = lines[index].TrimEnd('\r').Split(':');
            if (fields.Length != 3 || fields[0] != "0"
                || (index == 0 && !directory ? fields[2] is not ("regular file" or "regular empty file") : fields[2] != "directory")) return false;
            try
            {
                if ((Convert.ToInt32(fields[1], 8) & 0x12) != 0) return false;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return false;
            }
        }
        return true;
    }

    internal static async Task<bool> LauncherSourceReadyAsync(string path, CancellationToken ct)
    {
        if (!CanonicalSource(path) || !await RootControlledPathAsync(path, directory: true, ct)) return false;
        // A nested mount could expose a socket/credential tree despite the outer
        // directory's ownership. Reject it before asking the runtime to bind.
        foreach (var line in await File.ReadAllLinesAsync("/proc/self/mountinfo", ct))
        {
            var fields = line.Split(' ');
            if (fields.Length < 5) return false;
            var mount = fields[4].Replace("\\040", " ", StringComparison.Ordinal)
                .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
            if (mount.StartsWith(path + "/", StringComparison.Ordinal)) return false;
        }
        // Approved sources are immutable to the socket group. Walk without
        // following links, and admit only root-owned directories/regular files.
        var pending = new Queue<string>();
        pending.Enqueue(path);
        var entries = 0;
        while (pending.TryDequeue(out var parent))
        {
            foreach (var batch in Directory.EnumerateFileSystemEntries(parent).Chunk(256))
            {
                entries += batch.Length;
                if (entries > 100_000) return false;
                var metadata = await ProcessRunner.RunAsync("/usr/bin/stat", ["--format=%u:%a:%F", "--", .. batch],
                    environment: new Dictionary<string, string?> { ["LC_ALL"] = "C" }, ct: ct);
                var lines = metadata.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (metadata.ExitCode != 0 || lines.Length != batch.Length) return false;
                for (var index = 0; index < batch.Length; index++)
                {
                    var directory = lines[index].EndsWith(":directory", StringComparison.Ordinal);
                    if (!TrustedMetadata(lines[index], directory, 1)) return false;
                    if (directory) pending.Enqueue(batch[index]);
                }
            }
        }
        return File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects"));
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public string? Resolve(string role) => Roles.TryGetValue(role, out var pin) ? pin.Current : null;

    /// <summary>Install a newly released digest; the replaced digest becomes the rollback target.</summary>
    public RoleImageManifest Promote(string role, string image, string? release)
    {
        if (!KnownRoles.Contains(role, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown work container role '{role}'.");
        if (!IsPinned(image)) throw new ArgumentException("A promoted role image must be digest pinned.");
        var roles = new Dictionary<string, RoleImagePin>(Roles, StringComparer.Ordinal);
        roles[role] = roles.TryGetValue(role, out var old)
            ? old.Current == image ? old with { Release = release ?? old.Release } : new RoleImagePin(image, old.Current, release)
            : new RoleImagePin(image, null, release);
        return this with { Roles = roles };
    }

    /// <summary>Swap current and previous. A second rollback returns to the newer digest.</summary>
    public RoleImageManifest Rollback(string role)
    {
        if (!Roles.TryGetValue(role, out var pin) || pin.Previous is null)
            throw new InvalidOperationException($"Role '{role}' has no rollback image.");
        var roles = new Dictionary<string, RoleImagePin>(Roles, StringComparer.Ordinal)
        {
            [role] = new RoleImagePin(pin.Previous, pin.Current, null),
        };
        return this with { Roles = roles };
    }

    public static RoleImageManifest Empty() => new(1, new Dictionary<string, RoleImagePin>(StringComparer.Ordinal));
}
