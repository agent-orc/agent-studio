using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRunner.RunTargets;

/// <summary>
/// Host-side naming and validation. Everything that reaches a compose file, a
/// Caddyfile, or a docker argument passes through here, whatever Studio sent.
/// </summary>
public static partial class RunTargetNames
{
    public const string EdgeContainer = "rt-edge";
    public const string EdgeNetwork = "rt-edge";

    [GeneratedRegex(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z")]
    private static partial Regex Digest();

    [GeneratedRegex(@"\A(?=.{1,253}\z)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z")]
    private static partial Regex Domain();

    [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]{0,63}\z")]
    private static partial Regex VariableName();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex SecretRef();

    [GeneratedRegex(@"\A/[A-Za-z0-9._~/-]{0,255}\z")]
    private static partial Regex HealthPath();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,30}\z")]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"\A(1[4-7])(?:\.(?:0|[1-9][0-9]{0,2}))?\z")]
    private static partial Regex PostgresVersion();

    [GeneratedRegex(@"\Asecret://[A-Za-z0-9 ._/-]{1,400}/MANAGED_DB_[A-Z0-9_]{1,40}_PASSWORD\z")]
    private static partial Regex DatabasePasswordRef();

    /// <summary>The PostgreSQL role every managed database's app connects as.</summary>
    public const string DatabaseUser = "app";
    public const int DatabasePort = 5432;
    public const string DatabaseDataPath = "/var/lib/postgresql/data";

    public static bool IsDatabaseName(string? value) => value is not null && DatabaseName().IsMatch(value);

    [GeneratedRegex(@"\A[0-9]{8}T[0-9]{6}Z-(?:scheduled|manual|pre-restore)\z")]
    private static partial Regex BackupId();

    /// <summary>
    /// Where the environment's backup directory appears inside its database
    /// containers (AGT-3878). It is a host directory outside every database
    /// volume, so deleting or corrupting a volume leaves the dumps alone.
    /// </summary>
    public const string BackupMount = "/backups";

    /// <summary>A dump id the host generated: its UTC time and kind, for example <c>20261116T020000Z-scheduled</c>.</summary>
    public static bool IsBackupId(string? value) => value is not null && BackupId().IsMatch(value);

    public static string NewBackupId(DateTime at, string kind)
        => at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + kind;

    /// <summary>Host directory that holds the dumps of every database of one environment; one subdirectory per database.</summary>
    public static string BackupDirectory(string backupRoot, string appKey) => Path.Combine(backupRoot, appKey);

    /// <summary>Path of a dump inside the database containers of the environment.</summary>
    public static string BackupFile(string database, string backupId) => $"{BackupMount}/{database}/{backupId}.dump";

    /// <summary>The throwaway container that restores a dump to prove it is readable; it has no network.</summary>
    public static string VerifyContainer(string databaseKey) => "rt-verify-" + Hash(databaseKey, 16);

    /// <summary>A database name that is free in the instance while a restore loads data next to the live database.</summary>
    public static string RestoreStaging(string database, string operationId) => $"{database}_restore_{Hash(operationId, 8)}";

    public static string RestoreRetired(string database, string operationId) => $"{database}_replaced_{Hash(operationId, 8)}";

    /// <summary>
    /// The network an environment's databases share with its app containers.
    /// The edge never joins it, so public traffic has no route to a database,
    /// and no other environment's app is attached to it.
    /// </summary>
    public static string DataNetwork(string appKey) => appKey + "-data";

    /// <summary>Compose project, container and host name of one database; within Docker's 63-character DNS label.</summary>
    public static string DatabaseKey(string appKey, string database) => $"{appKey}-db-{Hash(database, 8)}";

    /// <summary>The named volume of a database. Only an owner's deletion removes it.</summary>
    public static string DatabaseVolume(string databaseKey) => databaseKey + "-data";

    /// <summary>Secret file that holds the app's connection string to one database.</summary>
    public static string DatabaseUrlFile(string database) => "managed-db-" + database.Replace('_', '-') + "-url";

    /// <summary>The official PostgreSQL image for a version both Studio and the host validated.</summary>
    public static string PostgresImage(string version) => $"postgres:{version}-alpine";

    public static string MajorOf(string version) => version.Split('.')[0];

    /// <summary>The connection string the app reads from its secret file. The password is base64url, so it needs no escaping.</summary>
    public static string ConnectionString(string databaseKey, string database, string password)
        => $"postgresql://{DatabaseUser}:{Uri.EscapeDataString(password)}@{databaseKey}:{DatabasePort}/{database}";

    public static bool IsDigest(string? value) => value is not null && Digest().IsMatch(value);
    public static bool IsDomain(string value) => Domain().IsMatch(value);
    public static bool IsSecretRef(string value) => SecretRef().IsMatch(value);

    public static string SecretEnvironmentName(string secretRef)
        => secretRef.Replace('-', '_').Replace('.', '_').ToUpper(CultureInfo.InvariantCulture);

    /// <summary>Variable that tells the app where its secret file is mounted, for example <c>DATABASE_URL_FILE</c>.</summary>
    public static string SecretFileVariable(string secretRef) => SecretEnvironmentName(secretRef) + "_FILE";

    /// <summary>
    /// Directory name of a project's own secret root, the same escaping Studio
    /// uses for its per-project files. Null for a name that has no safe directory.
    /// </summary>
    public static string? SecretScope(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || project.IndexOf('\0') >= 0) return null;
        var scope = Uri.EscapeDataString(project.ToLowerInvariant());
        return scope is "." or ".." ? null : scope;
    }

    /// <summary>
    /// Stable key of one project environment on this host. The slugs only make
    /// it readable; 80 hash bits keep it distinct. With the 12-character
    /// revision the compose project name stays within Docker's 63 characters.
    /// </summary>
    public static string AppKey(string project, string environmentId)
        => $"rt-{Slug(project, 14)}-{Slug(environmentId, 10)}-{Hash(project + "\n" + environmentId, 20)}";

    public static string AppNetwork(string appKey) => appKey + "-net";

    /// <summary>
    /// Content address of what runs in the container. Domains and edge mode are
    /// not part of it: they change routing only, never the app container.
    /// </summary>
    public static string Revision(RunTargetEnvironmentSpec spec)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            spec.Artifact, spec.ContainerPort, spec.HealthPath,
            spec.Variables.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new[] { item.Key, item.Value }),
            spec.SecretRefs.Order(StringComparer.Ordinal),
            spec.Resources,
            // Only how the app reaches a database changes the app container,
            // not the database's version or size.
            (spec.Databases ?? []).OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new[] { item.Name, item.Variable }),
        });
        return Hash(canonical, 12);
    }

    /// <summary>Returns the first reason the spec cannot be hosted, or null.</summary>
    public static string? Invalid(RunTargetEnvironmentSpec spec, bool requireArtifact = true)
    {
        if (requireArtifact && !IsDigest(spec.Artifact)) return "artifact must be pinned by sha256 digest";
        if (spec.ContainerPort is < 1 or > 65535) return "invalid container port";
        if (!HealthPath().IsMatch(spec.HealthPath) || spec.HealthPath.Contains("..", StringComparison.Ordinal))
            return "invalid health path";
        if (spec.Edge is not ("acme" or "cloudflare")) return "unknown edge mode";
        if (spec.Domains.Any(item => !IsDomain(item))) return "invalid domain";
        if (spec.Resources.Cpus is < 0.1 or > 64 || spec.Resources.MemoryMb is < 32 or > 262144
            || spec.Resources.PidsLimit is < 16 or > 32768) return "resources out of range";
        if (spec.Variables.Any(item => !VariableName().IsMatch(item.Key)
            || item.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)) return "invalid variable";
        if (spec.SecretRefs.Any(item => !IsSecretRef(item))) return "invalid secret reference";
        if (SecretScope(spec.Project) is null) return "project has no secret root";
        var secretNames = spec.SecretRefs.Select(SecretFileVariable).ToArray();
        if (secretNames.Any(name => !VariableName().IsMatch(name) || spec.Variables.ContainsKey(name)
            || spec.Variables.ContainsKey(name[..^"_FILE".Length]))
            || secretNames.Distinct(StringComparer.Ordinal).Count() != secretNames.Length)
            return "secret reference maps to a variable name that is invalid, shadowed or repeated";
        var databases = spec.Databases ?? [];
        if (databases.Any(item => !IsDatabaseName(item.Name) || item.Engine != "postgres"
                || !PostgresVersion().IsMatch(item.Version) || !DatabasePasswordRef().IsMatch(item.PasswordRef)
                || item.Resources.Cpus is < 0.1 or > 64 || item.Resources.MemoryMb is < 64 or > 262144
                || item.Resources.PidsLimit is < 64 or > 32768))
            return "invalid managed database";
        var databaseFiles = databases.Select(item => item.Variable + "_FILE").ToArray();
        if (databases.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != databases.Count
            || databases.Any(item => !VariableName().IsMatch(item.Variable))
            || databaseFiles.Any(name => !VariableName().IsMatch(name) || spec.Variables.ContainsKey(name)
                || spec.Variables.ContainsKey(name[..^"_FILE".Length]) || secretNames.Contains(name, StringComparer.Ordinal))
            || databaseFiles.Distinct(StringComparer.Ordinal).Count() != databaseFiles.Length)
            return "managed database variable is invalid, shadowed or repeated";
        return null;
    }

    private static string Slug(string value, int max)
    {
        var slug = NotSlug().Replace(value.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > max) slug = slug[..max].TrimEnd('-');
        return slug.Length == 0 ? "x" : slug;
    }

    private static string Hash(string value, int length)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length].ToLowerInvariant();
}
