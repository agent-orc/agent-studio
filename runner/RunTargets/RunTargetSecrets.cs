using System.Text;

namespace AgentRunner.RunTargets;

/// <summary>Reads one secret value by reference, on the run host, at the moment it is needed.</summary>
public interface IRunTargetSecretSource
{
    /// <returns>The value, or null when the reference is unknown for this project on this host.</returns>
    Task<string?> ReadAsync(string project, string secretRef, CancellationToken ct);
}

/// <summary>
/// The AGT-3176 host secret store with one root per project:
/// <c>&lt;root&gt;/&lt;project&gt;/&lt;secretRef&gt;</c>, where the project directory
/// is <see cref="RunTargetNames.SecretScope"/>. A reference resolves only
/// inside its own project's root, so one project cannot read another
/// project's secret by naming it. The secret provider of IP-6 replaces this
/// source without touching callers.
/// </summary>
public sealed class FileRunTargetSecretSource(string root) : IRunTargetSecretSource
{
    public async Task<string?> ReadAsync(string project, string secretRef, CancellationToken ct)
    {
        if (!RunTargetNames.IsSecretRef(secretRef) || RunTargetNames.SecretScope(project) is not { } scope) return null;
        var path = Path.Combine(root, scope, secretRef);
        if (!File.Exists(path)) return null;
        return (await File.ReadAllTextAsync(path, ct)).TrimEnd('\r', '\n');
    }
}

/// <summary>
/// Resolved secret values of one convergence step, keyed by reference. They
/// live in this object's memory until <see cref="RunTargetSecretFiles"/>
/// writes them to tmpfs; every diagnostic goes through <see cref="Mask"/>
/// before it is logged or reported.
/// </summary>
public sealed class RunTargetResolvedSecrets
{
    private const string Redacted = "[redacted]";
    private readonly string[] _values;

    private RunTargetResolvedSecrets(IReadOnlyDictionary<string, string> values, IReadOnlyList<string> missing)
    {
        Values = values;
        Missing = missing;
        // Longest first, so a value that contains another is masked whole.
        _values = values.Values.Where(value => value.Length >= 4).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length).ToArray();
    }

    public IReadOnlyDictionary<string, string> Values { get; }
    public IReadOnlyList<string> Missing { get; }

    public static async Task<RunTargetResolvedSecrets> ResolveAsync(
        IRunTargetSecretSource source, string project, IReadOnlyList<string> secretRefs, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var secretRef in secretRefs)
        {
            var value = await source.ReadAsync(project, secretRef, ct);
            if (value is null) missing.Add(secretRef);
            else values[secretRef] = value;
        }
        return new(values, missing);
    }

    public string Mask(string text)
    {
        foreach (var value in _values)
            text = text.Replace(value, Redacted, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>
/// Secret files of the running revisions, on a memory-backed file system
/// (<c>RUN_TARGET_SECRET_RUNTIME_DIR</c>). Compose mounts each file read-only
/// into the container, so Docker's container configuration stores the path
/// and never the value. Files exist only while their revision may run: they
/// are removed when the revision is stopped, discarded, or removed, and
/// written again before it starts. The host refuses to write them anywhere
/// that is not tmpfs or ramfs.
/// </summary>
public sealed class RunTargetSecretFiles(string root, Func<string, bool> isMemoryBacked)
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // The container user is not the agent's user, so the file is readable by
    // all; the 0700 directory keeps other host users out.
    private const UnixFileMode MountedFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public string Root { get; } = Path.GetFullPath(root);

    public string Directory(string composeProject) => Path.Combine(Root, composeProject);

    public bool IsMemoryBacked() => isMemoryBacked(Root);

    public void Write(string composeProject, RunTargetResolvedSecrets resolved)
    {
        CreatePrivateDirectory(Root);
        var directory = Directory(composeProject);
        CreatePrivateDirectory(directory);
        foreach (var (secretRef, value) in resolved.Values)
        {
            var path = Path.Combine(directory, secretRef);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = MountedFile;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(value));
            }
            File.Move(temporary, path, true);
        }
    }

    public void Remove(string composeProject)
    {
        var directory = Directory(composeProject);
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
    }

    /// <summary>Production check: the longest mount point in /proc/self/mounts that contains the path.</summary>
    public static bool ProcMountsIsMemoryBacked(string path)
    {
        const string mounts = "/proc/self/mounts";
        return File.Exists(mounts) && IsMemoryBacked(path, File.ReadAllText(mounts));
    }

    public static bool IsMemoryBacked(string path, string mounts)
    {
        var target = Path.GetFullPath(path).TrimEnd('/') + "/";
        string? best = null;
        string? type = null;
        foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 3) continue;
            var mountPoint = fields[1].Replace("\\040", " ", StringComparison.Ordinal).TrimEnd('/') + "/";
            // A later mount on the same point hides the earlier one.
            if (!target.StartsWith(mountPoint, StringComparison.Ordinal) || best is not null && mountPoint.Length < best.Length)
                continue;
            best = mountPoint;
            type = fields[2];
        }
        return type is "tmpfs" or "ramfs";
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(directory);
            return;
        }
        System.IO.Directory.CreateDirectory(directory, PrivateDirectory);
        File.SetUnixFileMode(directory, PrivateDirectory);
    }
}
