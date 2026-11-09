using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

/// <summary>One directory tree that a backup reads.</summary>
/// <param name="Name">Archive member root and restore mapping key (<c>workspace</c>, <c>home</c>, ...).</param>
/// <param name="Path">Absolute source directory.</param>
/// <param name="Projects">
/// True for the projects directory: project checkouts come back from their
/// remotes, so only their origin, branch and <c>refs/agent-studio/*</c> travel
/// in the archive (see <see cref="BackupCheckouts"/>).
/// </param>
/// <param name="Optional">A missing optional source is recorded and skipped instead of failing the run.</param>
/// <param name="Exclude">Relative paths or single directory names that are left out.</param>
public sealed record BackupSource(string Name, string Path, bool Projects, bool Optional, IReadOnlyList<string> Exclude);

/// <summary>Off-host copy target. <c>Kind</c> is <c>none</c>, <c>directory</c>, <c>sftp</c> or <c>s3</c>.</summary>
public sealed record BackupTargetOptions(
    string Kind,
    string? Directory = null,
    string? Host = null,
    int Port = 22,
    string? User = null,
    string? RemotePath = null,
    string? IdentityFile = null,
    string? KnownHostsFile = null,
    string? Endpoint = null,
    string? Region = null,
    string? Bucket = null,
    string? Prefix = null,
    string? AccessKeyIdFile = null,
    string? SecretAccessKeyFile = null,
    bool PathStyle = true);

/// <summary>
/// Scheduled installation backup settings (section <c>Backup</c>, AGT-3574).
/// Backups run only when a backup key is configured; without one the status
/// reports <c>not-configured</c> and the scheduler stays idle.
/// </summary>
public sealed record BackupOptions(
    bool Enabled,
    string Directory,
    string? KeyFile,
    TimeSpan Interval,
    TimeSpan VerifyInterval,
    TimeSpan StaleAfter,
    TimeSpan StartupDelay,
    TimeSpan MaxWriteHold,
    TimeSpan MaxRequestHold,
    int RetentionCount,
    IReadOnlyList<BackupSource> Sources,
    BackupTargetOptions Target,
    string? SecretKeyFile)
{
    public const string Section = "Backup";

    /// <summary>Directory names left out of every source: package caches and build scratch that a restore does not need.</summary>
    public static readonly IReadOnlyList<string> DefaultExclude = ["node_modules", ".cache", ".npm", ".nuget"];

    public bool Configured => Enabled && !string.IsNullOrWhiteSpace(KeyFile);

    public static BackupOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var workspace = Path.GetFullPath(SecretStorePaths.WorkspaceRoot(configuration));
        var directory = section["Directory"] is { Length: > 0 } configured
            ? configured
            : configuration["Management:BackupDirectory"] is { Length: > 0 } legacy
                ? legacy
                : Path.Combine(Path.GetDirectoryName(workspace)!, Path.GetFileName(workspace) + "-backups");
        var keyFile = section["KeyFile"] is { Length: > 0 } key ? Path.GetFullPath(key) : null;
        var secretKey = configuration["Secrets:KeyFile"] is { Length: > 0 } kek ? Path.GetFullPath(kek) : null;
        return new BackupOptions(
            section.GetValue("Enabled", true),
            Path.GetFullPath(directory),
            keyFile,
            Hours(section, "IntervalHours", 6, 0.25, 24 * 7),
            Hours(section, "VerifyIntervalHours", 24, 1, 24 * 30),
            Hours(section, "StaleAfterHours", 12, 1, 24 * 30),
            TimeSpan.FromMinutes(Math.Clamp(section.GetValue("StartupDelayMinutes", 5.0), 0, 24 * 60)),
            TimeSpan.FromSeconds(Math.Clamp(section.GetValue("MaxWriteHoldSeconds", 120), 0, 3600)),
            TimeSpan.FromSeconds(Math.Clamp(section.GetValue("MaxRequestHoldSeconds", 60), 0, 600)),
            Math.Clamp(section.GetValue("RetentionCount", configuration.GetValue("Management:BackupRetention", 12)), 1, 500),
            ReadSources(section.GetSection("Sources"), workspace),
            ReadTarget(section.GetSection("Target")),
            secretKey);
    }

    private static TimeSpan Hours(IConfigurationSection section, string key, double fallback, double min, double max)
        => TimeSpan.FromHours(Math.Clamp(section.GetValue(key, fallback), min, max));

    private static IReadOnlyList<BackupSource> ReadSources(IConfigurationSection section, string workspace)
    {
        var configured = section.GetChildren()
            .Where(child => child["Path"] is { Length: > 0 })
            .Select(child => new BackupSource(
                child.Key,
                Path.GetFullPath(child["Path"]!),
                child.GetValue("Projects", false),
                child.GetValue("Optional", false),
                child.GetSection("Exclude").GetChildren().Select(item => item.Value)
                    .OfType<string>().Where(item => item.Length > 0).ToArray()))
            .ToList();
        if (configured.Count > 0)
        {
            if (!configured.Any(source => string.Equals(source.Name, "workspace", StringComparison.Ordinal)))
                configured.Insert(0, new BackupSource("workspace", workspace, false, false, []));
            return configured;
        }

        // Without explicit sources: the workspace and the Studio folders of the
        // machine-local state root (batch gate store and leases, model routing,
        // prompt overrides, project settings). The root itself is shared with
        // other applications on a workstation and is never read as a whole.
        var sources = new List<BackupSource> { new("workspace", workspace, false, false, []) };
        string? local = null;
        try { local = LocalStateRoot.Resolve(); }
        catch (LocalStateRootUnavailableException) { local = null; }
        if (local is not null)
            foreach (var folder in new[] { "agent-taskboard", "agentstudio", "agent-studio", "AgentStudio" })
                sources.Add(new BackupSource("local-state-" + folder.ToLowerInvariant(),
                    Path.Combine(local, folder), false, true, ["cache"]));
        return sources;
    }

    private static BackupTargetOptions ReadTarget(IConfigurationSection section)
    {
        var kind = (section["Kind"] ?? "none").Trim().ToLowerInvariant();
        string? Value(string key) => section[key] is { Length: > 0 } value ? value.Trim() : null;
        string? FullPath(string key) => Value(key) is { } value ? Path.GetFullPath(value) : null;
        return new BackupTargetOptions(
            kind.Length == 0 ? "none" : kind,
            FullPath("Directory"),
            Value("Host"),
            section.GetValue("Port", 22),
            Value("User"),
            Value("Path"),
            FullPath("IdentityFile"),
            FullPath("KnownHostsFile"),
            Value("Endpoint"),
            Value("Region") ?? "us-east-1",
            Value("Bucket"),
            Value("Prefix"),
            FullPath("AccessKeyIdFile"),
            FullPath("SecretAccessKeyFile"),
            section.GetValue("PathStyle", true));
    }
}
