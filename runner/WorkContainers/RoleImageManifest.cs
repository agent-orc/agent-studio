using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        using var handle = OpenRootControlled(path, directory: false);
        using var reader = new StreamReader(DescriptorPath(handle));
        return Parse(await reader.ReadToEndAsync(ct));
    }

    internal static bool CanonicalSource(string path)
        => path.StartsWith('/') && path.EndsWith(".git", StringComparison.Ordinal)
           && !path.Contains('\\') && !path.Contains(',')
           && !path.Any(char.IsControl)
           && !path.Split('/').Skip(1).Any(part => part is "" or "." or "..");

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
        try
        {
            using var source = OpenRootControlled(path, directory: true);
            if (!CanonicalSource(path)) return false;
            await WalkSourceAsync(source, destination: null, owner: 0, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    // Linux O_PATH pins an inode without opening a device or blocking on a FIFO.
    // Every lookup is relative to an already checked directory descriptor.
    private const int PathOnly = 0x200000;
    private const int NoFollow = 0x20000;
    private const int CloseOnExec = 0x80000;
    private const int DirectoryOnly = 0x10000;

    internal static string DescriptorPath(SafeFileHandle handle)
        => "/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static SafeFileHandle OpenPath(int parent, string name, bool directory = false)
    {
        var descriptor = OpenAt(parent, name, PathOnly | NoFollow | CloseOnExec | (directory ? DirectoryOnly : 0));
        if (descriptor < 0) throw new IOException("Source capability lookup failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    internal static SafeFileHandle OpenRootControlled(string path, bool directory, bool executable = false)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path
            || (path != "/" && path.Split('/').Skip(1).Any(part => part is "" or "." or "..")))
            throw new InvalidDataException("A canonical Linux capability path is required.");
        var handle = OpenPath(-100, "/", directory: true);
        try
        {
            CheckMetadata(handle, directory: true, owner: 0);
            var parts = new LinkedList<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
            var links = 0;
            while (parts.First is { } part)
            {
                parts.RemoveFirst();
                var next = OpenPath(handle.DangerousGetHandle().ToInt32(), part.Value);
                try
                {
                    // Runtime installations may use aliases, including /bin.
                    // Read the pinned link itself, then validate every directory
                    // traversed by its target. Source and release paths never
                    // permit links. No pathname is reopened after validation.
                    if (executable && StatX(next.DangerousGetHandle().ToInt32(), "", 0x1000, 0x17ff, out var type) == 0
                        && (type.Mode & 0xf000) == 0xa000)
                    {
                        if (++links > 40) throw new InvalidDataException("Runtime capability has too many symbolic links.");
                        var buffer = new byte[4096];
                        var length = ReadLinkAt(next.DangerousGetHandle().ToInt32(), "", buffer, (nuint)buffer.Length);
                        if (length <= 0 || length >= buffer.Length) throw new InvalidDataException("Runtime capability link is invalid.");
                        var target = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
                        if (target.StartsWith('/'))
                        {
                            handle.Dispose();
                            handle = OpenPath(-100, "/", directory: true);
                            CheckMetadata(handle, directory: true, owner: 0);
                        }
                        foreach (var component in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                            parts.AddFirst(component);
                        next.Dispose();
                        continue;
                    }
                    CheckMetadata(next, parts.Count > 0 || directory, owner: 0);
                }
                catch { next.Dispose(); throw; }
                handle.Dispose();
                handle = next;
            }
            if (executable && (CheckMetadata(handle, directory: false, owner: 0).Mode & 0x49) == 0)
                throw new InvalidDataException("Runtime capability must be executable.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static LinuxMetadata CheckMetadata(SafeFileHandle handle, bool directory, uint owner)
    {
        // statx with AT_EMPTY_PATH describes the pinned object, never its name.
        if (StatX(handle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x17ff, out var metadata) != 0
            || (metadata.Mask & 0x101b) != 0x101b || metadata.Owner != owner
            || (metadata.Mode & 0x12) != 0 || (metadata.Mode & 0xf000) != (directory ? 0x4000 : 0x8000))
            throw new InvalidDataException("Source capability must contain only trusted, non-writable directories and regular files on one mount.");
        return metadata;
    }

    /// <summary>Copy only the validated inodes into a private snapshot, never the caller's paths.</summary>
    internal static async Task WalkSourceAsync(SafeFileHandle source, string? destination, uint owner,
        CancellationToken ct, Action<string>? opened = null)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Source capabilities require Linux.");
        var rootMetadata = CheckMetadata(source, directory: true, owner);
        var entries = 0;
        async Task Walk(SafeFileHandle parent, string? target, int depth)
        {
            if (depth > 128) throw new InvalidDataException("Source capability exceeds the directory depth limit.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(DescriptorPath(parent)))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 100_000) throw new InvalidDataException("Source capability exceeds the entry limit.");
                var name = Path.GetFileName(entry);
                using var child = OpenPath(parent.DangerousGetHandle().ToInt32(), name);
                opened?.Invoke(name);
                // Determine the type from this descriptor, then validate it. No
                // path-based type check may run between opening and copying.
                if (StatX(child.DangerousGetHandle().ToInt32(), "", 0x1000, 0x17ff, out var type) != 0)
                    throw new IOException("Source capability inspection failed.");
                var directory = (type.Mode & 0xf000) == 0x4000;
                var metadata = CheckMetadata(child, directory, owner);
                if (metadata.MountId != rootMetadata.MountId)
                    throw new InvalidDataException("Nested source mounts are refused.");
                var childTarget = target is null ? null : Path.Combine(target, name);
                if (directory)
                {
                    if (childTarget is not null)
                    {
                        Directory.CreateDirectory(childTarget);
                    }
                    await Walk(child, childTarget, depth + 1);
                    if (childTarget is not null) PreserveSnapshotPermissions(childTarget, metadata);
                }
                else if (childTarget is not null)
                {
                    await using var input = File.OpenRead(DescriptorPath(child));
                    await using var output = new FileStream(childTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, ct);
                    PreserveSnapshotPermissions(childTarget, metadata);
                }
            }
        }
        await Walk(source, destination, 0);
        if (destination is not null) PreserveSnapshotPermissions(destination, rootMetadata);
        // These are checked through descriptors too. A regular directory with
        // a .git suffix is not enough to grant a repository capability.
        using var head = OpenPath(source.DangerousGetHandle().ToInt32(), "HEAD");
        CheckMetadata(head, directory: false, owner);
        using var objects = OpenPath(source.DangerousGetHandle().ToInt32(), "objects");
        CheckMetadata(objects, directory: true, owner);
    }

    private static void PreserveSnapshotPermissions(string path, LinuxMetadata metadata)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Source capabilities require Linux.");
        // Preserve DAC read/traverse restrictions, including the source group.
        // The private snapshot parent stays 0700 throughout copying. Privilege
        // bits are never carried into the container's read-only source tree.
        if (ChangeOwner(path, -1, metadata.Group) != 0)
            throw new IOException("Source snapshot group could not be preserved.");
        File.SetUnixFileMode(path, (UnixFileMode)(metadata.Mode & 0x1ff));
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxMetadata
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(144)] public ulong MountId;
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, int owner, uint group);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int parent, string path, int flags);

    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern nint ReadLinkAt(int parent, string path, byte[] buffer, nuint size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(int descriptor, string path, int flags, uint mask, out LinuxMetadata metadata);

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
