using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// Studio's record of one operation on a managed database (AGT-3878):
/// <c>backup</c> (dump now), <c>verify</c> (restore a dump into a scratch
/// database on the host) or <c>restore</c> (replace the data of
/// <see cref="TargetDatabase"/> with a dump of <see cref="SourceDatabase"/>).
/// <see cref="State"/> is <c>awaiting-approval</c>, <c>queued</c>, <c>succeeded</c>,
/// <c>failed</c> or <c>rejected</c>. The record is the operation's audit trail.
/// </summary>
public sealed record ManagedDatabaseOperationRecord(string Id, string EnvironmentId, string HostId, string Kind,
    string SourceDatabase, string TargetDatabase, string? BackupId, string State, string RequestedBy, DateTime RequestedAt)
{
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? Message { get; init; }

    public bool IsOpen => State is "awaiting-approval" or "queued";
}

public sealed record ManagedDatabaseOperationView(string Id, string Kind, string? BackupId, string SourceDatabase,
    string TargetDatabase, string State, string RequestedBy, DateTime RequestedAt, string? ApprovedBy, DateTime? ApprovedAt,
    DateTime? FinishedAt, string? Message);

public sealed record ManagedDatabaseOperationRequest(string? Kind, string? BackupId, string? TargetDatabase, string? Confirm);

public enum ManagedDatabaseOperationOutcome { Accepted, NotFound, Invalid, Conflict }

public sealed record ManagedDatabaseOperationDecision(ManagedDatabaseOperationOutcome Outcome,
    ManagedDatabaseOperationRecord? Operation, string? Error = null)
{
    public static ManagedDatabaseOperationDecision Fail(ManagedDatabaseOperationOutcome outcome, string error)
        => new(outcome, null, error);
}

/// <summary>
/// Pure rules of database operations. A backup or verification runs at once.
/// A restore replaces live data: the requester types the target's name, no
/// deploy may be open in the environment, and in production it waits for an
/// Owner's approval. One operation per database is open at a time. The host
/// answers each operation once; an operation it never answers fails after
/// <see cref="ResultTimeout"/>, an approval that never comes after <see cref="ApprovalTimeout"/>.
/// </summary>
public static class ManagedDatabaseOperationPolicy
{
    public static readonly TimeSpan ResultTimeout = TimeSpan.FromHours(6);
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromDays(7);
    public const int KeptPerProject = 200;
    public const int ShownPerDatabase = 10;

    public static ManagedDatabaseOperationDecision Request(IReadOnlyList<ManagedDatabaseRecord> records,
        IReadOnlyList<ManagedDatabaseOperationRecord> operations, string environmentId, string environmentKind, string name,
        ManagedDatabaseOperationRequest request, string actor, bool deployOpen, DateTime now)
    {
        var kind = request.Kind ?? "";
        if (!RunTargetVocabulary.DatabaseOperationKinds.Contains(kind))
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Invalid,
                "kind must be backup, verify or restore.");
        var source = Live(records, environmentId, name);
        if (source is null)
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.NotFound,
                "The database is not known for this environment.");
        var target = kind == "restore" ? request.TargetDatabase ?? name : name;
        string[] touched = [name, target];
        if (operations.FirstOrDefault(item => item.IsOpen && item.EnvironmentId == environmentId
                && (touched.Contains(item.SourceDatabase) || touched.Contains(item.TargetDatabase))) is { } busy)
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict,
                $"A {busy.Kind} of {busy.SourceDatabase} is still {busy.State}. Wait for it to finish.");
        if (kind == "backup")
            return source.State == "running" && source.Declared
                ? Accept(source, kind, name, name, null, "queued", actor, now)
                : ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict,
                    $"Only a running database can be backed up; {name} is {StateOf(source)}.");
        if (source.Backups.All(item => item.Id != request.BackupId))
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict,
                $"Backup {request.BackupId} is not among the backups the run host reports for {name}.");
        if (kind == "verify") return Accept(source, kind, name, name, request.BackupId, "queued", actor, now);

        if (!ProjectDatabaseVocabulary.Name().IsMatch(target))
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Invalid, "targetDatabase is not a database name.");
        if (!string.Equals(request.Confirm, target, StringComparison.Ordinal))
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Invalid,
                $"Type the database name '{target}' to confirm that its data is replaced.");
        if (Restorable(records, environmentId, source, target, deployOpen) is { } refusal)
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict, refusal);
        return Accept(source, kind, name, target, request.BackupId,
            environmentKind == "production" ? "awaiting-approval" : "queued", actor, now);
    }

    /// <summary>An Owner approves or rejects a production restore; the restore is checked again before it is queued.</summary>
    public static ManagedDatabaseOperationDecision Decide(ManagedDatabaseOperationRecord? operation,
        IReadOnlyList<ManagedDatabaseRecord> records, bool approve, string actor, bool deployOpen, DateTime now)
    {
        if (operation is null)
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.NotFound, "The operation is not known.");
        if (operation.State != "awaiting-approval")
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict,
                $"The operation is {operation.State}, not awaiting approval.");
        if (!approve)
            return new(ManagedDatabaseOperationOutcome.Accepted, operation with
            {
                State = "rejected", ApprovedBy = actor, ApprovedAt = now, FinishedAt = now, Message = $"Rejected by {actor}.",
            });
        var source = Live(records, operation.EnvironmentId, operation.SourceDatabase);
        var refusal = source is null ? $"Database {operation.SourceDatabase} is gone."
            : source.Backups.All(item => item.Id != operation.BackupId)
                ? $"Backup {operation.BackupId} is no longer on the run host."
                : Restorable(records, operation.EnvironmentId, source, operation.TargetDatabase, deployOpen);
        if (refusal is not null) return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict, refusal);
        return new(ManagedDatabaseOperationOutcome.Accepted, operation with { State = "queued", ApprovedBy = actor, ApprovedAt = now });
    }

    /// <summary>Closes operations whose approval or host result did not come in time.</summary>
    public static bool Expire(List<ManagedDatabaseOperationRecord> operations, DateTime now)
    {
        var changed = false;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.State == "awaiting-approval" && now - operation.RequestedAt > ApprovalTimeout)
                operations[index] = operation with { State = "rejected", FinishedAt = now,
                    Message = $"Not approved within {ApprovalTimeout.TotalDays:0} days." };
            else if (operation.State == "queued" && now - (operation.ApprovedAt ?? operation.RequestedAt) > ResultTimeout)
                operations[index] = operation with { State = "failed", FinishedAt = now,
                    Message = $"The run host did not report a result within {ResultTimeout.TotalHours:0} hours." };
            else continue;
            changed = true;
        }
        var finished = operations.Count(item => !item.IsOpen);
        if (finished > KeptPerProject)
        {
            var drop = operations.Where(item => !item.IsOpen).Take(finished - KeptPerProject).ToHashSet();
            operations.RemoveAll(drop.Contains);
            changed = true;
        }
        return changed;
    }

    /// <summary>The host's answer ends a queued operation of that host; any other answer is ignored.</summary>
    public static ManagedDatabaseOperationRecord? Complete(ManagedDatabaseOperationRecord operation,
        RunTargetDatabaseOperationResult result, string hostId, DateTime now)
    {
        if (operation.State != "queued" || operation.HostId != hostId || result.State is not ("succeeded" or "failed")
            || result.EnvironmentId != operation.EnvironmentId || result.Name != operation.SourceDatabase) return null;
        return operation with
        {
            State = result.State, FinishedAt = result.FinishedAt > now ? now : result.FinishedAt,
            Message = ManagedDatabasePolicy.HostText(result.Message),
        };
    }

    public static RunTargetDatabaseOperation Wire(string project, ManagedDatabaseOperationRecord operation)
        => new(operation.Id, project, operation.EnvironmentId, operation.SourceDatabase, operation.Kind, operation.BackupId,
            operation.Kind == "restore" ? operation.TargetDatabase : null);

    public static IReadOnlyList<ManagedDatabaseOperationView> Views(IReadOnlyList<ManagedDatabaseOperationRecord> operations,
        string environmentId, string name)
        => operations.Where(item => item.EnvironmentId == environmentId && (item.SourceDatabase == name || item.TargetDatabase == name))
            .OrderByDescending(item => item.RequestedAt).Take(ShownPerDatabase)
            .Select(item => new ManagedDatabaseOperationView(item.Id, item.Kind, item.BackupId, item.SourceDatabase,
                item.TargetDatabase, item.State, item.RequestedBy, item.RequestedAt, item.ApprovedBy, item.ApprovedAt,
                item.FinishedAt, item.Message))
            .ToArray();

    private static ManagedDatabaseRecord? Live(IReadOnlyList<ManagedDatabaseRecord> records, string environmentId, string name)
        => records.LastOrDefault(item => item.EnvironmentId == environmentId && item.Name == name && item.DeletedAt is null);

    private static string? Restorable(IReadOnlyList<ManagedDatabaseRecord> records, string environmentId,
        ManagedDatabaseRecord source, string target, bool deployOpen)
    {
        var destination = Live(records, environmentId, target);
        if (destination is null || !destination.Declared)
            return $"The environment declares no database {target}. Declare it in project.yml and deploy before restoring into it.";
        if (destination.State != "running")
            return $"Database {target} is {StateOf(destination)}; a restore needs it running.";
        if (destination.HostId != source.HostId)
            return $"Database {target} runs on another host than the backup of {source.Name}.";
        return deployOpen ? "A deploy of this environment is open. Restore after it finished." : null;
    }

    private static ManagedDatabaseOperationDecision Accept(ManagedDatabaseRecord source, string kind, string name, string target,
        string? backupId, string state, string actor, DateTime now)
        => new(ManagedDatabaseOperationOutcome.Accepted, new(Guid.NewGuid().ToString("N"), source.EnvironmentId, source.HostId,
            kind, name, target, backupId, state, actor, now));

    private static string StateOf(ManagedDatabaseRecord record) => record.Declared ? record.State : "retained";
}

/// <summary>
/// Pure alert conditions of one database (AGT-3878). Only a declared,
/// running database is watched. A dump is overdue once the newest one, or the
/// first time the database ran, is older than the interval plus
/// <see cref="Grace"/>. A failed attempt after the newest dump is its own
/// alert, which stands in for overdue while it lasts. The volume alert compares
/// the measured data directory with the environment's threshold.
/// </summary>
public static class ManagedDatabaseAlertPolicy
{
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    public static bool Watched(ManagedDatabaseRecord record)
        => record.Declared && record.DeletedAt is null && record.DeletionRequestedAt is null && record.RunningSince is not null
            && record.State is "running" or "unhealthy" or "upgrade-refused";

    public static bool BackupFailed(ManagedDatabaseRecord record)
        => record.LastBackupError is not null
            && (record.LastBackupAt is not { } last || record.LastBackupAttemptAt is { } attempt && attempt > last);

    public static bool BackupOverdue(ManagedDatabaseRecord record, ManagedDatabaseEnvironmentPolicy policy, DateTime now)
        => (record.LastBackupAt ?? record.RunningSince) is { } since
            && now - since > TimeSpan.FromHours(policy.BackupIntervalHours) + Grace;

    /// <summary><c>ok</c>, <c>pending</c> (no dump yet, not overdue), <c>overdue</c>, <c>failed</c>, or <c>none</c> when no dump is expected.</summary>
    public static string BackupStatus(ManagedDatabaseRecord record, ManagedDatabaseEnvironmentPolicy policy, DateTime now)
        => !Watched(record) ? "none"
            : BackupFailed(record) ? "failed"
            : BackupOverdue(record, policy, now) ? "overdue"
            : record.LastBackupAt is null ? "pending" : "ok";

    public static IReadOnlyList<(string Kind, string Summary)> Conditions(ManagedDatabaseRecord record,
        ManagedDatabaseEnvironmentPolicy policy, DateTime now)
    {
        if (!Watched(record)) return [];
        var conditions = new List<(string, string)>();
        var last = record.LastBackupAt is { } taken ? $"the last backup is from {Stamp(taken)}" : "no backup exists yet";
        if (BackupFailed(record))
            conditions.Add(("backup-failed", $"The last backup attempt at {Stamp(record.LastBackupAttemptAt ?? now)} failed "
                + $"({record.LastBackupError}); {last}."));
        else if (BackupOverdue(record, policy, now))
            conditions.Add(("backup-overdue", $"Backups run every {policy.BackupIntervalHours} h, but {last}."));
        var threshold = policy.VolumeAlertFor(record.Size);
        if (record.VolumeBytes is { } bytes && bytes >= threshold)
            conditions.Add(("volume-threshold", $"The data directory holds {Gigabytes(bytes)} GB, at or above the alert "
                + $"threshold of {Gigabytes(threshold)} GB."));
        return conditions;
    }

    public static string Stamp(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Gigabytes(long bytes)
        => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
