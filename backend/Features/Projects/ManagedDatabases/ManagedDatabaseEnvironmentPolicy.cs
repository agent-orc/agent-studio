using System.Text.Json;

namespace AgentStudio.Projects;

/// <summary>
/// Backup schedule, retention and volume alert of the managed databases of one
/// environment (AGT-3878). The optional <c>managedDatabases</c> object of an
/// edge-hosted environment declaration overrides the defaults for its kind:
/// production dumps daily and keeps 30 days (at least 7 dumps), every other
/// environment dumps daily and keeps 7 days (at least 3). Without
/// <see cref="VolumeAlertBytes"/> the alert threshold follows the database size.
/// </summary>
public sealed record ManagedDatabaseEnvironmentPolicy(int BackupIntervalHours, int BackupRetentionDays, int BackupsKept,
    long? VolumeAlertBytes = null)
{
    public static readonly IReadOnlyList<string> Keys = ["backupIntervalHours", "backupRetentionDays", "backupsKept", "volumeAlertMb"];

    public static ManagedDatabaseEnvironmentPolicy For(string environmentKind)
        => environmentKind == "production" ? new(24, 30, 7) : new(24, 7, 3);

    /// <summary>Size of a database's data directory at which Studio raises an alert.</summary>
    public long VolumeAlertFor(string size)
        => VolumeAlertBytes ?? size switch
        {
            "medium" => 20L * 1024 * 1024 * 1024,
            "large" => 80L * 1024 * 1024 * 1024,
            _ => 5L * 1024 * 1024 * 1024,
        };

    public RunTargetBackupPolicy Wire => new(BackupIntervalHours, BackupRetentionDays, BackupsKept);

    /// <summary>Null when the declaration has no <c>managedDatabases</c> object.</summary>
    public static ManagedDatabaseEnvironmentPolicy? Parse(JsonElement declaration, string kind, string file)
    {
        if (!declaration.TryGetProperty("managedDatabases", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Environment {file} managedDatabases must be an object.");
        if (value.EnumerateObject().Select(property => property.Name).FirstOrDefault(name => !Keys.Contains(name)) is { } unknown)
            throw new JsonException($"Environment {file} managedDatabases has an unknown field '{unknown}'.");
        int? Number(string name, int minimum, int maximum)
        {
            if (!value.TryGetProperty(name, out var number)) return null;
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum)
                throw new JsonException($"Environment {file} managedDatabases.{name} must be a whole number from {minimum} to {maximum}.");
            return parsed;
        }
        var defaults = For(kind);
        var alertMb = Number("volumeAlertMb", 64, 16 * 1024 * 1024);
        return new(Number("backupIntervalHours", 1, 168) ?? defaults.BackupIntervalHours,
            Number("backupRetentionDays", 1, 3650) ?? defaults.BackupRetentionDays,
            Number("backupsKept", 1, 100) ?? defaults.BackupsKept,
            alertMb is { } mb ? mb * 1024L * 1024 : null);
    }
}
