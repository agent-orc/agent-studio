using System.Globalization;

namespace AgentRunner.RunTargets;

/// <summary>
/// Pure schedule and retention of managed database dumps (AGT-3878). A dump
/// is due once the newest one is <see cref="RunTargetBackupPolicy.IntervalHours"/>
/// old; a failed attempt is retried after <see cref="RetryAfter"/>. A dump is
/// kept while it is younger than the retention or one of the
/// <see cref="RunTargetBackupPolicy.MinimumKept"/> newest, so a host that could
/// not dump for weeks still keeps its last good dumps.
/// </summary>
public static class RunTargetBackupSchedule
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    /// <summary>A refused major upgrade leaves the old server running on its data, so it is dumped like a running one.</summary>
    public static bool HoldsLiveData(RunTargetDatabaseState database) => database.Status is "running" or "upgrade-refused";

    /// <summary>Studio's policy within the bounds the host accepts; a missing one is <see cref="RunTargetBackupPolicy.Default"/>.</summary>
    public static RunTargetBackupPolicy Sanitize(RunTargetBackupPolicy? policy)
        => policy is null ? RunTargetBackupPolicy.Default
            : new(Math.Clamp(policy.IntervalHours, 1, 168), Math.Clamp(policy.RetentionDays, 1, 3650),
                Math.Clamp(policy.MinimumKept, 1, 100));

    public static DateTime? LastTakenAt(RunTargetDatabaseState database)
        => database.Backups.Count == 0 ? null : database.Backups.Max(item => item.TakenAt);

    public static bool IsDue(RunTargetDatabaseState database, DateTime now)
    {
        if (!HoldsLiveData(database)) return false;
        if (database.LastBackupError is not null && database.LastBackupAttemptAt is { } attempt && now - attempt < RetryAfter)
            return false;
        return LastTakenAt(database) is not { } last || now - last >= TimeSpan.FromHours(database.Backup.IntervalHours);
    }

    /// <summary>How long a due dump has waited; the most overdue database is dumped first.</summary>
    public static TimeSpan Overdue(RunTargetDatabaseState database, DateTime now)
        => LastTakenAt(database) is { } last ? now - last - TimeSpan.FromHours(database.Backup.IntervalHours) : TimeSpan.MaxValue;

    public static IReadOnlyList<RunTargetBackupRecord> Expired(IReadOnlyList<RunTargetBackupRecord> backups,
        RunTargetBackupPolicy policy, DateTime now)
        => backups.OrderByDescending(item => item.TakenAt).Skip(policy.MinimumKept)
            .Where(item => now - item.TakenAt > TimeSpan.FromDays(policy.RetentionDays)).ToArray();
}

/// <summary>
/// Care of the managed databases on a run host (AGT-3878): a readiness check
/// every cycle, connection counts every minute, at most one scheduled dump per
/// cycle, retention, and the operations Studio sends. Dumps are PostgreSQL
/// custom-format archives (<c>pg_dump --format=custom</c>) written by the
/// server's own tools into the environment's backup directory, a host
/// directory outside every Docker volume. A dump is written under a
/// <c>.partial</c> name, its table of contents is read back, and only then is
/// it renamed and listed. A verification restores a dump into a throwaway
/// PostgreSQL container without a network. A restore loads the dump into a new
/// database next to the live one, so a failed load leaves the live data alone,
/// then swaps the two names; the data it replaces is dumped first.
/// </summary>
public sealed class RunTargetDatabaseBackups(
    IRunTargetDocker docker,
    RunTargetStateStore store,
    string backupRoot,
    RunTargetReconcilerSettings settings,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    public const int KeptResults = 100;
    private static readonly TimeSpan ConnectionsEvery = TimeSpan.FromMinutes(1);
    private const string ScratchDatabase = "scratch";
    private const string ScratchUser = "postgres";
    private const string ConnectionsSql =
        "SELECT count(*) FILTER (WHERE backend_type = 'client backend'), current_setting('max_connections') FROM pg_stat_activity";
    private const string TablesSql =
        "SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + "WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'";

    public string Root { get; } = backupRoot;

    /// <summary>Host directory mounted at <see cref="RunTargetNames.BackupMount"/> in the environment's database containers.</summary>
    public string EnvironmentDirectory(string appKey) => RunTargetNames.BackupDirectory(Root, appKey);

    /// <summary>Creates the database's own subdirectory before its container mounts the environment directory.</summary>
    public void EnsureDirectory(string appKey, string database)
        => Directory.CreateDirectory(Path.Combine(EnvironmentDirectory(appKey), database));

    /// <summary>
    /// Checks every running database with <c>pg_isready</c> over TCP and, at
    /// most every minute, counts its client connections. Runs while Studio is
    /// unreachable as well. A database that stops answering is <c>unhealthy</c>
    /// until it answers again.
    /// </summary>
    public async Task CheckAsync(RunTargetHostState state, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = false;
        foreach (var (databaseKey, database) in state.Databases)
        {
            if (database.Status is not ("running" or "unhealthy")) continue;
            var ready = (await docker.InspectAsync(databaseKey, ct)).State == RunTargetContainerState.Running
                && (await docker.ExecAsync(databaseKey, ReadyCheck(RunTargetNames.DatabaseUser, database.Name), ct)).Ok;
            var status = ready ? "running" : "unhealthy";
            if (database.Status != status)
                log($"run-target: {Label(database)} is {(ready ? "ready again" : "not ready")}");
            changed |= database.Status != status || database.Ready != ready;
            database.Status = status;
            database.Ready = ready;
            if (!ready || database.ConnectionsMeasuredAt is { } measured && now - measured < ConnectionsEvery) continue;
            var counted = await docker.ExecAsync(databaseKey, Psql(RunTargetNames.DatabaseUser, database.Name, ConnectionsSql), ct);
            database.ConnectionsMeasuredAt = now;
            changed = true;
            var parts = counted.Ok ? counted.Detail.Trim().Split('|') : [];
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var connections)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var maximum))
            {
                database.Connections = connections;
                database.MaxConnections = maximum;
            }
            else log($"run-target: could not count the connections of {Label(database)}");
        }
        if (changed) store.Save(state);
    }

    /// <summary>
    /// Answers Studio's operations, each id once, then takes at most one
    /// scheduled dump and applies retention. Returns the result of every
    /// operation Studio sent, so a result lost in transit is sent again.
    /// </summary>
    public async Task<IReadOnlyList<RunTargetDatabaseOperationResult>> RunAsync(RunTargetHostState state,
        IReadOnlyList<RunTargetDatabaseOperation> operations, CancellationToken ct)
    {
        var results = new List<RunTargetDatabaseOperationResult>();
        foreach (var operation in operations.DistinctBy(item => item.Id))
        {
            var done = state.OperationResults.FirstOrDefault(item => item.Id == operation.Id);
            if (done is null)
            {
                done = await ExecuteAsync(state, operation, ct);
                state.OperationResults.Add(done);
                if (state.OperationResults.Count > KeptResults)
                    state.OperationResults.RemoveRange(0, state.OperationResults.Count - KeptResults);
                store.Save(state);
            }
            results.Add(done);
        }
        var now = clock.GetUtcNow().UtcDateTime;
        // One dump per cycle bounds how long a cycle holds up supervision and deploys.
        var due = state.Databases.Where(item => RunTargetBackupSchedule.IsDue(item.Value, now))
            .OrderByDescending(item => RunTargetBackupSchedule.Overdue(item.Value, now)).FirstOrDefault();
        if (due.Value is not null) await BackupAsync(due.Key, due.Value, "scheduled", ct);
        foreach (var (databaseKey, database) in state.Databases.Where(item => RunTargetBackupSchedule.HoldsLiveData(item.Value)))
            await PruneAsync(databaseKey, database, ct);
        store.Save(state);
        return results;
    }

    /// <summary>Removes every dump of a database whose data an owner deleted; the volume goes as well.</summary>
    public async Task<bool> DeleteAllAsync(string appKey, string database, string? version, CancellationToken ct)
    {
        var directory = Path.Combine(EnvironmentDirectory(appKey), database);
        if (!Directory.Exists(directory)) return true;
        // The server wrote the dumps as root inside its container; a throwaway container removes them the same way.
        var removed = await docker.RunOnceAsync(RunTargetNames.PostgresImage(version is { Length: > 0 } ? version : "17"),
            EnvironmentDirectory(appKey), ["rm", "-rf", RunTargetNames.BackupMount + "/" + database], ct);
        if (!removed.Ok)
        {
            log($"run-target: could not delete the backups of database {database} in {directory}: {removed.Detail}");
            return false;
        }
        return true;
    }

    private async Task<RunTargetDatabaseOperationResult> ExecuteAsync(RunTargetHostState state,
        RunTargetDatabaseOperation operation, CancellationToken ct)
    {
        var appKey = RunTargetNames.AppKey(operation.Project, operation.EnvironmentId);
        var databaseKey = RunTargetNames.DatabaseKey(appKey, operation.Name);
        RunTargetDatabaseOperationResult Result(bool ok, string message, string? backupId = null)
        {
            log($"run-target: {operation.Kind} {operation.Id} of {operation.Project}/{operation.EnvironmentId} database "
                + $"{operation.Name} {(ok ? "succeeded" : "failed")}: {message}");
            return new(operation.Id, operation.Project, operation.EnvironmentId, operation.Name, ok ? "succeeded" : "failed",
                message, clock.GetUtcNow().UtcDateTime, backupId);
        }
        if (!RunTargetNames.IsDatabaseName(operation.Name) || !state.Databases.TryGetValue(databaseKey, out var database)
            || !Owns(database, operation.Project, operation.EnvironmentId, operation.Name))
            return Result(false, "The run host has no such database.");
        switch (operation.Kind)
        {
            case "backup":
                if (!RunTargetBackupSchedule.HoldsLiveData(database))
                    return Result(false, $"The database is {database.Status}, so it cannot be dumped.");
                var taken = await BackupAsync(databaseKey, database, "manual", ct);
                return taken is null ? Result(false, database.LastBackupError ?? "The dump failed.")
                    : Result(true, $"Backup {taken.Id} written ({taken.Bytes} bytes).", taken.Id);
            case "verify":
                return await VerifyAsync(appKey, databaseKey, database, operation.BackupId, Result, ct);
            case "restore":
                var target = operation.TargetName ?? operation.Name;
                var targetKey = RunTargetNames.DatabaseKey(appKey, target);
                if (!RunTargetNames.IsDatabaseName(target) || !state.Databases.TryGetValue(targetKey, out var targetDatabase)
                    || !Owns(targetDatabase, operation.Project, operation.EnvironmentId, target))
                    return Result(false, $"The environment has no database {target} on this host.");
                return await RestoreAsync(targetKey, targetDatabase, database, operation, Result, ct);
            default:
                return Result(false, $"Unknown operation {operation.Kind}.");
        }
    }

    /// <returns>The new dump, or null with <see cref="RunTargetDatabaseState.LastBackupError"/> set.</returns>
    private async Task<RunTargetBackupRecord?> BackupAsync(string databaseKey, RunTargetDatabaseState database, string kind,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var id = RunTargetNames.NewBackupId(now, kind);
        var file = RunTargetNames.BackupFile(database.Name, id);
        var partial = file + ".partial";
        database.LastBackupAttemptAt = now;
        string? failure = null;
        var dumped = await docker.ExecAsync(databaseKey, ["pg_dump", "-U", RunTargetNames.DatabaseUser, "-d", database.Name,
            "--format=custom", "--compress=6", "--file", partial], ct);
        if (!dumped.Ok) failure = "pg_dump failed: " + dumped.Detail;
        else if (await docker.ExecAsync(databaseKey, ["pg_restore", "--list", partial], ct) is { Ok: false } listed)
            failure = "The dump is not readable: " + listed.Detail;
        else if (await docker.ExecAsync(databaseKey, ["mv", partial, file], ct) is { Ok: false } moved)
            failure = "The dump could not be stored: " + moved.Detail;
        long bytes = 0;
        if (failure is null)
        {
            var size = await docker.ExecAsync(databaseKey, ["stat", "-c", "%s", file], ct);
            if (!size.Ok || !long.TryParse(size.Detail.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bytes))
                failure = "The size of the dump could not be read: " + size.Detail;
        }
        if (failure is not null)
        {
            await docker.ExecAsync(databaseKey, ["rm", "-f", partial], ct);
            database.LastBackupError = Bounded(failure);
            log($"run-target: {kind} backup of {Label(database)} failed: {database.LastBackupError}");
            return null;
        }
        var record = new RunTargetBackupRecord(id, now, bytes, kind);
        database.Backups.Add(record);
        database.LastBackupError = null;
        log($"run-target: {kind} backup {id} of {Label(database)} written ({bytes} bytes)");
        return record;
    }

    private async Task PruneAsync(string databaseKey, RunTargetDatabaseState database, CancellationToken ct)
    {
        foreach (var expired in RunTargetBackupSchedule.Expired(database.Backups, database.Backup, clock.GetUtcNow().UtcDateTime))
        {
            var removed = await docker.ExecAsync(databaseKey, ["rm", "-f", RunTargetNames.BackupFile(database.Name, expired.Id)], ct);
            if (!removed.Ok)
            {
                log($"run-target: could not prune backup {expired.Id} of {Label(database)}: {removed.Detail}");
                return;
            }
            database.Backups.Remove(expired);
            log($"run-target: pruned backup {expired.Id} of {Label(database)} after {database.Backup.RetentionDays} days");
        }
    }

    private async Task<RunTargetDatabaseOperationResult> VerifyAsync(string appKey, string databaseKey,
        RunTargetDatabaseState database, string? backupId, Func<bool, string, string?, RunTargetDatabaseOperationResult> result,
        CancellationToken ct)
    {
        var backup = database.Backups.FirstOrDefault(item => item.Id == backupId);
        if (backup is null) return result(false, $"Backup {backupId} is not on the run host.", backupId);
        var container = RunTargetNames.VerifyContainer(databaseKey);
        await docker.RemoveContainerAsync(container, ct);
        try
        {
            var started = await docker.StartScratchAsync(container, RunTargetNames.PostgresImage(database.Version),
                Path.Combine(EnvironmentDirectory(appKey), database.Name), RunTargetNames.BackupMount + "/" + database.Name, ct);
            if (!started.Ok) return result(false, "The scratch database did not start: " + Bounded(started.Detail), backupId);
            if (!await WaitReadyAsync(container, ScratchUser, ScratchDatabase, ct))
                return result(false, "The scratch database did not become ready.", backupId);
            var restored = await docker.ExecAsync(container, ["pg_restore", "-U", ScratchUser, "-d", ScratchDatabase,
                "--no-owner", "--no-privileges", "--exit-on-error", RunTargetNames.BackupFile(database.Name, backup.Id)], ct);
            if (!restored.Ok) return result(false, "The backup could not be restored: " + Bounded(restored.Detail), backupId);
            var counted = await docker.ExecAsync(container, Psql(ScratchUser, ScratchDatabase, TablesSql), ct);
            if (!counted.Ok || !int.TryParse(counted.Detail.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tables))
                return result(false, "The restored scratch database could not be read: " + Bounded(counted.Detail), backupId);
            var verified = backup with { VerifiedAt = clock.GetUtcNow().UtcDateTime, VerifiedTables = tables };
            database.Backups[database.Backups.IndexOf(backup)] = verified;
            return result(true, $"Backup {backup.Id} restored into a scratch database: {tables} tables.", backupId);
        }
        finally
        {
            var removed = await docker.RemoveContainerAsync(container, CancellationToken.None);
            if (!removed.Ok) log($"run-target: could not remove scratch container {container}: {removed.Detail}");
        }
    }

    private async Task<RunTargetDatabaseOperationResult> RestoreAsync(string targetKey, RunTargetDatabaseState target,
        RunTargetDatabaseState source, RunTargetDatabaseOperation operation,
        Func<bool, string, string?, RunTargetDatabaseOperationResult> result, CancellationToken ct)
    {
        var backup = source.Backups.FirstOrDefault(item => item.Id == operation.BackupId);
        if (backup is null) return result(false, $"Backup {operation.BackupId} is not on the run host.", operation.BackupId);
        if (target.Status != "running")
            return result(false, $"Database {target.Name} is {target.Status}; a restore needs it running.", operation.BackupId);
        var before = await BackupAsync(targetKey, target, "pre-restore", ct);
        if (before is null)
            return result(false, $"The current data of {target.Name} could not be backed up first, so nothing was restored: "
                + target.LastBackupError, operation.BackupId);
        var name = target.Name;
        var staging = RunTargetNames.RestoreStaging(name, operation.Id);
        var retired = RunTargetNames.RestoreRetired(name, operation.Id);
        // An earlier attempt that stopped half way may have left the staging database.
        await Sql(targetKey, $"DROP DATABASE IF EXISTS \"{staging}\" WITH (FORCE)", ct);
        if (await Sql(targetKey, $"CREATE DATABASE \"{staging}\"", ct) is { Ok: false } created)
            return result(false, "The restore database could not be created: " + Bounded(created.Detail), operation.BackupId);
        var loaded = await docker.ExecAsync(targetKey, ["pg_restore", "-U", RunTargetNames.DatabaseUser, "-d", staging,
            "--no-owner", "--exit-on-error", RunTargetNames.BackupFile(source.Name, backup.Id)], ct);
        if (!loaded.Ok)
        {
            await Sql(targetKey, $"DROP DATABASE IF EXISTS \"{staging}\" WITH (FORCE)", ct);
            return result(false, $"The backup could not be loaded; the data of {name} is unchanged: " + Bounded(loaded.Detail),
                operation.BackupId);
        }
        // No new session may start while the names swap; the app reconnects to the restored data.
        var swapped = await Sql(targetKey, [
            $"ALTER DATABASE \"{name}\" WITH ALLOW_CONNECTIONS false",
            $"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = '{name}' AND pid <> pg_backend_pid()",
            $"ALTER DATABASE \"{name}\" RENAME TO \"{retired}\"",
            $"ALTER DATABASE \"{staging}\" RENAME TO \"{name}\"",
        ], ct);
        if (!swapped.Ok)
        {
            await Sql(targetKey, $"ALTER DATABASE \"{retired}\" RENAME TO \"{name}\"", ct);
            await Sql(targetKey, $"ALTER DATABASE \"{name}\" WITH ALLOW_CONNECTIONS true", ct);
            await Sql(targetKey, $"DROP DATABASE IF EXISTS \"{staging}\" WITH (FORCE)", ct);
            return result(false, $"The restored data could not replace {name}; the data of {name} is unchanged: "
                + Bounded(swapped.Detail), operation.BackupId);
        }
        var dropped = await Sql(targetKey, $"DROP DATABASE \"{retired}\" WITH (FORCE)", ct);
        if (!dropped.Ok) log($"run-target: could not drop {retired} after the restore of {Label(target)}: {dropped.Detail}");
        var from = source.Name == name ? "" : $" of {source.Name}";
        return result(true, $"Backup {backup.Id}{from} restored into {name}. The data it replaced is in backup {before.Id}.",
            operation.BackupId);
    }

    private async Task<bool> WaitReadyAsync(string container, string user, string database, CancellationToken ct)
    {
        for (var attempt = 0; attempt < settings.HealthAttempts; attempt++)
        {
            if (attempt > 0) await delay(TimeSpan.FromSeconds(settings.HealthIntervalSeconds), ct);
            if ((await docker.ExecAsync(container, ReadyCheck(user, database), ct)).Ok) return true;
        }
        return false;
    }

    private Task<RunTargetDockerResult> Sql(string databaseKey, string statement, CancellationToken ct)
        => Sql(databaseKey, [statement], ct);

    /// <summary>Statements run in one session on the maintenance database, stopping at the first error.</summary>
    private Task<RunTargetDockerResult> Sql(string databaseKey, IReadOnlyList<string> statements, CancellationToken ct)
        => docker.ExecAsync(databaseKey, ["psql", "-U", RunTargetNames.DatabaseUser, "-d", "postgres", "-v", "ON_ERROR_STOP=1",
            "-tAq", .. statements.SelectMany(statement => new[] { "-c", statement })], ct);

    /// <summary>Over TCP: the image's initialisation server listens on the socket only, so ready means the final server.</summary>
    private static string[] ReadyCheck(string user, string database)
        => ["pg_isready", "-q", "-h", "127.0.0.1", "-p", RunTargetNames.DatabasePort.ToString(CultureInfo.InvariantCulture),
            "-U", user, "-d", database];

    private static string[] Psql(string user, string database, string sql)
        => ["psql", "-U", user, "-d", database, "-v", "ON_ERROR_STOP=1", "-tA", "-c", sql];

    private static string Label(RunTargetDatabaseState database)
        => $"{database.Project}/{database.EnvironmentId} database {database.Name}";

    private static string Bounded(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static bool Owns(RunTargetDatabaseState database, string project, string environmentId, string name)
        => database.Project == project && database.EnvironmentId == environmentId && database.Name == name;
}
