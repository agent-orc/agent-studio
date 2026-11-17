using System.Text.RegularExpressions;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// Studio's record of one managed database on a run host (AGT-3877). It
/// outlives the declaration: a database that is no longer declared is
/// <see cref="Declared"/> false and keeps its volume until an owner requests
/// the deletion and the host confirms it. <see cref="Version"/> is the
/// approved version of the last deploy that declared it;
/// <see cref="RunningVersion"/> is what the host reports.
/// </summary>
public sealed record ManagedDatabaseRecord(string EnvironmentId, string Name, string HostId)
{
    public string Engine { get; init; } = "postgres";
    public string Version { get; init; } = "";
    public string Size { get; init; } = ProjectDatabaseVocabulary.DefaultSize;
    public string Variable { get; init; } = ProjectDatabaseVocabulary.DefaultVariable;
    public bool Declared { get; init; }
    public string State { get; init; } = "provisioning";
    public string? RunningVersion { get; init; }
    public long? VolumeBytes { get; init; }
    public DateTime? ObservedAt { get; init; }

    /// <summary>Why the last deploy that declared it did not apply, for example a refused major version change.</summary>
    public string? Message { get; init; }
    public string? DeletionRequestedBy { get; init; }
    public DateTime? DeletionRequestedAt { get; init; }
    public DateTime? DeletedAt { get; init; }

    /// <summary>True once Studio removed the generated credential after the host deleted the data.</summary>
    public bool CredentialRetired { get; init; }

    /// <summary>The host's last readiness check (AGT-3878); null before the first one.</summary>
    public bool? Ready { get; init; }
    public int? Connections { get; init; }
    public int? MaxConnections { get; init; }

    /// <summary>When the host first reported the database running; the backup schedule is due from there.</summary>
    public DateTime? RunningSince { get; init; }

    /// <summary>The dumps the host keeps, oldest first, as it last reported them.</summary>
    public IReadOnlyList<RunTargetBackupRecord> Backups { get; init; } = [];
    public DateTime? LastBackupAttemptAt { get; init; }
    public string? LastBackupError { get; init; }

    /// <summary>Backup and volume alerts with the card each one raised; a closed one stays until its kind opens again.</summary>
    public IReadOnlyList<ManagedDatabaseAlert> Alerts { get; init; } = [];

    public DateTime? LastBackupAt => Backups.Count == 0 ? null : Backups.Max(item => item.TakenAt);
}

/// <summary>One open alert of a database (AGT-3878): <c>backup-overdue</c>, <c>backup-failed</c> or <c>volume-threshold</c>.</summary>
public sealed record ManagedDatabaseAlert(string Kind, DateTime Since, string Summary, string? CardId = null,
    bool OpenedReported = false, DateTime? ClosedAt = null);

/// <summary>One database as the Bereitstellung area shows it. Holds the credential reference, never a value.</summary>
public sealed record ManagedDatabaseView(
    string EnvironmentId, string Name, string Engine, string Version, string? RunningVersion, string Size,
    ProjectDatabaseResources Resources, string Variable, string CredentialReference, string State, bool Declared,
    long? VolumeBytes, DateTime? ObservedAt, string? Message,
    string? DeletionRequestedBy, DateTime? DeletionRequestedAt, DateTime? DeletedAt)
{
    public string EnvironmentKind { get; init; } = "";
    public bool? Ready { get; init; }
    public int? Connections { get; init; }
    public int? MaxConnections { get; init; }
    public ManagedDatabaseBackupPolicyView BackupPolicy { get; init; } = new(24, 7, 3);
    public long VolumeAlertBytes { get; init; }
    public DateTime? LastBackupAt { get; init; }

    /// <summary><c>ok</c>, <c>overdue</c>, <c>failed</c>, or <c>none</c> when no dump is expected.</summary>
    public string BackupStatus { get; init; } = "none";
    public string? LastBackupError { get; init; }
    public DateTime? LastBackupAttemptAt { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RunTargetBackupRecord> Backups { get; init; } = [];

    /// <summary>The newest operations that read or replace this database, newest first.</summary>
    public IReadOnlyList<ManagedDatabaseOperationView> Operations { get; init; } = [];
}

public sealed record ManagedDatabaseBackupPolicyView(int IntervalHours, int RetentionDays, int MinimumKept);

public sealed record ManagedDatabaseDeletionRequest(string? Confirm);

public enum ManagedDatabaseDeletionOutcome { Requested, NotFound, StillDeclared, AlreadyRequested, Deleted }

/// <summary>A credential Studio must generate before the host can provision the database.</summary>
public sealed record ManagedDatabaseCredentialNeed(string Project, string EnvironmentId, string EnvironmentKind,
    string Database);

/// <summary>The databases of one environment placed on a host, for the host's secret resolution.</summary>
public sealed record ManagedDatabaseCredentialLease(string Project, string EnvironmentId, string EnvironmentKind,
    string HostId, IReadOnlyList<string> Names);

/// <summary>
/// Pure decisions for managed databases: the desired-state spec, which
/// records a deploy declares, when an owner may delete data, which major
/// version changes are refused, and which parts of a host report Studio keeps.
/// </summary>
public static partial class ManagedDatabasePolicy
{
    [GeneratedRegex(@"\A[1-9][0-9]?(?:\.[0-9]{1,3}){0,2}\z")]
    private static partial Regex ReportedVersion();

    [GeneratedRegex(@"\A[0-9]{8}T[0-9]{6}Z-(?:scheduled|manual|pre-restore)\z")]
    public static partial Regex BackupId();

    /// <summary>The host keeps far fewer; more is a hostile or broken report.</summary>
    public const int MaxReportedBackups = 1000;

    /// <summary>The <c>secret://</c> reference of the database password, in the environment's own scope.</summary>
    public static string CredentialReference(string project, string environmentId, string database)
        => new SecretReference(SecretScope.ForEnvironment(project, environmentId),
            ProjectDatabaseVocabulary.CredentialName(database)).ToString();

    public static IReadOnlyList<RunTargetDatabaseSpec> Specs(string project, string environmentId,
        IReadOnlyList<ProjectDatabaseDefinition> databases, ManagedDatabaseEnvironmentPolicy policy)
        => databases.Select(item =>
        {
            var limits = ProjectDatabaseVocabulary.ResourcesFor(item.Size);
            return new RunTargetDatabaseSpec(item.Name, item.Engine, item.Version, item.Size,
                new RunTargetResources(limits.Cpus, limits.MemoryMb, limits.PidsLimit), item.Variable,
                CredentialReference(project, environmentId, item.Name), policy.Wire);
        }).ToArray();

    /// <summary>
    /// The message that refuses a deploy whose databases change the major
    /// version of data the host already holds, or null. The host's reported
    /// version wins over the approved one, since it describes the data.
    /// </summary>
    public static string? MajorChangeRefusal(IReadOnlyList<ProjectDatabaseDefinition> requested,
        IEnumerable<ManagedDatabaseRecord> records, string environmentId)
    {
        foreach (var database in requested)
        {
            var record = records.FirstOrDefault(item => item.EnvironmentId == environmentId && item.Name == database.Name
                && item.DeletedAt is null);
            var current = record?.RunningVersion ?? (record?.State is "running" or "retained" ? record.Version : null);
            if (current is null || ProjectDatabaseVocabulary.MajorOf(current) == database.Major) continue;
            return $"Database {database.Name} holds PostgreSQL {ProjectDatabaseVocabulary.MajorOf(current)} data; "
                + $"the deploy asks for {database.Version}. A major version change is refused because it needs a "
                + $"dump and restore, which a later card adds. Keep version {ProjectDatabaseVocabulary.MajorOf(current)}.x in project.yml.";
        }
        return null;
    }

    /// <summary>
    /// The records after Studio sent <paramref name="declared"/> for an
    /// environment on <paramref name="hostId"/>: declared databases are
    /// created or updated, a pending deletion of a database declared again is
    /// withdrawn, and the environment's other databases on that host are retained.
    /// </summary>
    public static List<ManagedDatabaseRecord> Track(IReadOnlyList<ManagedDatabaseRecord> records, string environmentId,
        string hostId, IReadOnlyList<ProjectDatabaseDefinition> declared)
    {
        var result = new List<ManagedDatabaseRecord>();
        foreach (var record in records)
        {
            if (record.EnvironmentId != environmentId || record.HostId != hostId || record.DeletedAt is not null)
            {
                result.Add(record);
                continue;
            }
            var database = declared.FirstOrDefault(item => item.Name == record.Name);
            result.Add(database is null ? record with { Declared = false }
                : record with { Declared = true, Engine = database.Engine, Version = database.Version, Size = database.Size,
                    Variable = database.Variable, DeletionRequestedAt = null, DeletionRequestedBy = null });
        }
        foreach (var database in declared.Where(item => !result.Any(record => record.EnvironmentId == environmentId
                     && record.HostId == hostId && record.Name == item.Name && record.DeletedAt is null)))
            result.Add(new(environmentId, database.Name, hostId)
            {
                Engine = database.Engine, Version = database.Version, Size = database.Size,
                Variable = database.Variable, Declared = true,
            });
        return result;
    }

    /// <summary>
    /// An owner may delete only data the environment no longer declares, not in
    /// the running deploy and not in an open one, so a deploy never loses the
    /// database it was approved with.
    /// </summary>
    public static (ManagedDatabaseDeletionOutcome Outcome, ManagedDatabaseRecord? Record) RequestDeletion(
        ManagedDatabaseRecord? record, bool declaredByAnyJob, string actor, DateTime now)
    {
        if (record is null) return (ManagedDatabaseDeletionOutcome.NotFound, null);
        if (record.DeletedAt is not null) return (ManagedDatabaseDeletionOutcome.Deleted, record);
        if (record.Declared || declaredByAnyJob) return (ManagedDatabaseDeletionOutcome.StillDeclared, record);
        if (record.DeletionRequestedAt is not null) return (ManagedDatabaseDeletionOutcome.AlreadyRequested, record);
        return (ManagedDatabaseDeletionOutcome.Requested,
            record with { DeletionRequestedAt = now, DeletionRequestedBy = actor });
    }

    /// <summary>
    /// The host's report is untrusted: closed state vocabulary, a version shape,
    /// bounded sizes and counts, backup ids of the host's own shape, no time in
    /// the future, and a short single-line error text.
    /// </summary>
    public static RunTargetDatabaseObservation? Sanitize(RunTargetDatabaseObservation observation, DateTime receivedAt)
    {
        if (!RunTargetVocabulary.DatabaseStates.Contains(observation.State)
            || !ProjectDatabaseVocabulary.Name().IsMatch(observation.Name ?? "")) return null;
        DateTime Past(DateTime value) => value > receivedAt ? receivedAt : value;
        int? Count(int? value) => value is { } count ? Math.Clamp(count, 0, 1_000_000) : null;
        return observation with
        {
            Version = observation.Version is { } version && ReportedVersion().IsMatch(version) ? version : null,
            VolumeBytes = observation.VolumeBytes is { } bytes ? Math.Clamp(bytes, 0, long.MaxValue / 2) : null,
            ObservedAt = Past(observation.ObservedAt),
            Connections = Count(observation.Connections),
            MaxConnections = Count(observation.MaxConnections),
            Backups = observation.Backups?.Take(MaxReportedBackups)
                .Where(item => item is not null && BackupId().IsMatch(item.Id ?? "")
                    && RunTargetVocabulary.BackupKinds.Contains(item.Kind ?? ""))
                .DistinctBy(item => item.Id)
                .Select(item => item with
                {
                    TakenAt = Past(item.TakenAt), Bytes = Math.Clamp(item.Bytes, 0, long.MaxValue / 2),
                    VerifiedAt = item.VerifiedAt is { } verified ? Past(verified) : null,
                    VerifiedTables = item.VerifiedTables is { } tables ? Math.Clamp(tables, 0, 1_000_000) : null,
                })
                .OrderBy(item => item.TakenAt).ToArray(),
            LastBackupAttemptAt = observation.LastBackupAttemptAt is { } attempt ? Past(attempt) : null,
            LastBackupError = HostText(observation.LastBackupError),
        };
    }

    /// <summary>Host text shown in Studio: one line, at most 300 characters, credential-shaped text masked.</summary>
    public static string? HostText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        line = AgentStudio.Security.CredentialRedactor.Redact(line);
        return line.Length <= 300 ? line : line[..300];
    }

    /// <summary>
    /// Applies one sanitized observation. A <c>deleted</c> report ends only a
    /// deletion an owner requested; any other report updates what runs.
    /// </summary>
    public static ManagedDatabaseRecord Observe(ManagedDatabaseRecord record, RunTargetDatabaseObservation observation,
        DateTime now)
    {
        if (observation.State == "deleted")
            return record.DeletionRequestedAt is null ? record : record with { State = "deleted", DeletedAt = now, VolumeBytes = null };
        var backups = observation.Backups ?? record.Backups;
        var unchanged = record.State == observation.State && (observation.Version ?? record.RunningVersion) == record.RunningVersion
            && (observation.VolumeBytes ?? record.VolumeBytes) == record.VolumeBytes
            && observation.Ready == record.Ready && (observation.Connections ?? record.Connections) == record.Connections
            && backups.SequenceEqual(record.Backups) && observation.LastBackupError == record.LastBackupError;
        if (unchanged && record.ObservedAt is { } seen && observation.ObservedAt - seen < RunTargetDesiredStatePolicy.ObservedRefresh)
            return record;
        return record with
        {
            State = observation.State,
            RunningVersion = observation.Version ?? record.RunningVersion,
            VolumeBytes = observation.VolumeBytes ?? record.VolumeBytes,
            ObservedAt = observation.ObservedAt,
            Message = observation.State == "running" ? null : record.Message,
            Ready = observation.Ready,
            Connections = observation.Connections ?? record.Connections,
            MaxConnections = observation.MaxConnections ?? record.MaxConnections,
            RunningSince = record.RunningSince ?? (observation.State == "running" ? observation.ObservedAt : null),
            Backups = backups,
            LastBackupAttemptAt = observation.LastBackupAttemptAt ?? record.LastBackupAttemptAt,
            LastBackupError = observation.LastBackupError,
        };
    }

    public static ManagedDatabaseView View(string project, ManagedDatabaseRecord record, string environmentKind,
        ManagedDatabaseEnvironmentPolicy policy, IReadOnlyList<ManagedDatabaseOperationRecord> operations, DateTime now)
    {
        return new ManagedDatabaseView(record.EnvironmentId, record.Name, record.Engine, record.Version, record.RunningVersion,
            record.Size, ProjectDatabaseVocabulary.ResourcesFor(record.Size), record.Variable,
            CredentialReference(project, record.EnvironmentId, record.Name),
            record.DeletionRequestedAt is not null && record.DeletedAt is null ? "deleting"
                : !record.Declared && record.DeletedAt is null ? "retained"
                : record.State,
            record.Declared, record.VolumeBytes, record.ObservedAt, record.Message,
            record.DeletionRequestedBy, record.DeletionRequestedAt, record.DeletedAt)
        {
            EnvironmentKind = environmentKind,
            Ready = record.Ready,
            Connections = record.Connections,
            MaxConnections = record.MaxConnections,
            BackupPolicy = new(policy.BackupIntervalHours, policy.BackupRetentionDays, policy.BackupsKept),
            VolumeAlertBytes = policy.VolumeAlertFor(record.Size),
            LastBackupAt = record.LastBackupAt,
            BackupStatus = ManagedDatabaseAlertPolicy.BackupStatus(record, policy, now),
            LastBackupError = record.LastBackupError,
            LastBackupAttemptAt = record.LastBackupAttemptAt,
            Backups = record.DeletedAt is null ? record.Backups.Reverse().ToArray() : [],
            Operations = ManagedDatabaseOperationPolicy.Views(operations, record.EnvironmentId, record.Name),
        };
    }

    /// <summary>A database the current declaration has but no deploy has approved yet.</summary>
    public static ManagedDatabaseView Pending(string project, EnvironmentDeclaration environment, ProjectDatabaseDefinition database)
    {
        var policy = environment.EffectiveDatabasePolicy;
        return new ManagedDatabaseView(environment.Id, database.Name, database.Engine, database.Version, null, database.Size,
            ProjectDatabaseVocabulary.ResourcesFor(database.Size), database.Variable,
            CredentialReference(project, environment.Id, database.Name), "pending-deploy", true,
            null, null, null, null, null, null)
        {
            EnvironmentKind = environment.Kind,
            BackupPolicy = new(policy.BackupIntervalHours, policy.BackupRetentionDays, policy.BackupsKept),
            VolumeAlertBytes = policy.VolumeAlertFor(database.Size),
        };
    }
}
