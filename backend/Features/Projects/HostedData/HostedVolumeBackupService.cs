using System.Text.Json;

namespace AgentStudio.Projects;

public sealed record HostedVolumeSchedule(string Project, string Environment, string Volume, int? HourUtc,
    DateTimeOffset? LastAttemptAt, string? LastError, IReadOnlyList<HostedVolumeBackup> Backups);
public sealed record HostedVolumeAudit(DateTimeOffset At, string Actor, string Action, string Environment,
    string Volume, string? BackupId, string Outcome)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
public sealed record HostedVolumeState(IReadOnlyList<HostedVolumeSchedule> Schedules,
    IReadOnlyList<HostedVolumeAudit> Audit);
public sealed record HostedVolumeScheduleRequest(int? HourUtc);

public static class HostedVolumeSchedulePolicy
{
    public static bool IsDue(HostedVolumeSchedule schedule, DateTimeOffset now)
        => schedule.HourUtc is { } hour && now.UtcDateTime.Hour >= hour
            && schedule.LastAttemptAt?.UtcDateTime.Date != now.UtcDateTime.Date;
}

/// <summary>Studio coordination and durable schedule/audit state; the host adapter owns volume bytes.</summary>
public sealed class HostedVolumeBackupService(
    IConfiguration configuration, ProjectEnvironmentService environments,
    IHostedVolumeBackupEngine engine, TimeProvider clock, ILogger<HostedVolumeBackupService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<HostedVolumeState?> ReadAsync(string project, CancellationToken cancellationToken)
    {
        if (environments.DeclarationFiles(project) is null) return null;
        await _gate.WaitAsync(cancellationToken);
        try { return ReadState(project); }
        finally { _gate.Release(); }
    }

    public async Task<HostedVolumeSchedule> SetScheduleAsync(string project, string environment, string volume,
        int? hourUtc, string actor, CancellationToken cancellationToken)
    {
        ValidateVolume(project, environment, volume);
        if (hourUtc is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(hourUtc));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = ReadState(project);
            var schedules = state.Schedules.Where(item => item.Environment != environment || item.Volume != volume).ToList();
            var previous = state.Schedules.FirstOrDefault(item => item.Environment == environment && item.Volume == volume);
            var schedule = previous is null
                ? new HostedVolumeSchedule(project, environment, volume, hourUtc, null, null, [])
                : previous with { HourUtc = hourUtc };
            schedules.Add(schedule);
            WriteState(project, state with { Schedules = schedules, Audit =
                Append(state.Audit, new(clock.GetUtcNow(), actor, "schedule", environment, volume, null,
                    hourUtc is null ? "disabled" : $"daily-{hourUtc:00}:00-utc")) });
            return schedule;
        }
        finally { _gate.Release(); }
    }

    public async Task<HostedVolumeBackup> BackupAsync(string project, string environment, string volume,
        string actor, CancellationToken cancellationToken)
    {
        ValidateVolume(project, environment, volume);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = ReadState(project);
            var schedule = state.Schedules.FirstOrDefault(item => item.Environment == environment && item.Volume == volume)
                ?? new HostedVolumeSchedule(project, environment, volume, null, null, null, []);
            var now = clock.GetUtcNow();
            try
            {
                var backup = await engine.BackupAsync(new(project, environment, volume), now, cancellationToken);
                var updated = schedule with { LastAttemptAt = now, LastError = null,
                    Backups = schedule.Backups.Append(backup).ToArray() };
                Save(project, state, updated, new(now, actor, "backup", environment, volume, backup.Id, "succeeded"));
                return backup;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Save(project, state, schedule with { LastAttemptAt = now, LastError = ex.Message },
                    new(now, actor, "backup", environment, volume, null, "failed"));
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync(string project, string environment, string volume, string backupId,
        string actor, CancellationToken cancellationToken)
    {
        ValidateVolume(project, environment, volume);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = ReadState(project);
            var backup = state.Schedules.SelectMany(item => item.Backups)
                .SingleOrDefault(item => item.Volume == new HostedVolumeKey(project, environment, volume) && item.Id == backupId);
            if (backup is null)
            {
                WriteState(project, state with { Audit = Append(state.Audit,
                    new(clock.GetUtcNow(), actor, "restore", environment, volume, backupId, "not-found")) });
                throw new FileNotFoundException("Backup is not registered for this declared volume.");
            }
            try
            {
                await engine.RestoreAsync(backup, cancellationToken);
                WriteState(project, state with { Audit = Append(state.Audit,
                    new(clock.GetUtcNow(), actor, "restore", environment, volume, backupId, "succeeded")) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteState(project, state with { Audit = Append(state.Audit,
                    new(clock.GetUtcNow(), actor, "restore", environment, volume, backupId, "failed")) });
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var root = StoreRoot();
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var project = Path.GetFileNameWithoutExtension(file);
            var state = await ReadAsync(project, cancellationToken);
            if (state is null) continue;
            var now = clock.GetUtcNow();
            foreach (var schedule in state.Schedules.Where(item => HostedVolumeSchedulePolicy.IsDue(item, now)))
            {
                try { await BackupAsync(project, schedule.Environment, schedule.Volume, "schedule", cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // BackupAsync records the failed attempt so the sweep is bounded until the next UTC day.
                    logger.LogWarning(ex, "Hosted volume backup failed for {Project}/{Environment}/{Volume}",
                        project, schedule.Environment, schedule.Volume);
                }
            }
        }
    }

    private void ValidateVolume(string project, string environment, string volume)
    {
        var declaration = environments.DeclarationFiles(project)?
            .FirstOrDefault(item => item.EnvironmentId == environment)?.Declaration;
        if (declaration?.Data.Volumes.Any(item => item.Name == volume) != true)
            throw new ArgumentException("The volume is not declared for this environment.");
    }

    private HostedVolumeState ReadState(string project)
    {
        var file = FilePath(project);
        return File.Exists(file)
            ? JsonSerializer.Deserialize<HostedVolumeState>(File.ReadAllText(file), _json)
                ?? new([], [])
            : new([], []);
    }

    private void Save(string project, HostedVolumeState state, HostedVolumeSchedule schedule, HostedVolumeAudit audit)
        => WriteState(project, state with
        {
            Schedules = state.Schedules.Where(item => item.Environment != schedule.Environment || item.Volume != schedule.Volume)
                .Append(schedule).ToArray(),
            Audit = Append(state.Audit, audit),
        });

    private static IReadOnlyList<HostedVolumeAudit> Append(IReadOnlyList<HostedVolumeAudit> audit, HostedVolumeAudit entry)
        => audit.Append(entry).TakeLast(500).ToArray();

    private void WriteState(string project, HostedVolumeState state)
    {
        var file = FilePath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, _json));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string FilePath(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || project.Length > 128
            || project.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' ')) || project is "." or "..")
            throw new ArgumentException("Invalid project name.", nameof(project));
        return Path.Combine(StoreRoot(), project + ".json");
    }

    private string StoreRoot() => Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required."), "hosted-volume-backups");
}

public sealed class HostedVolumeBackupWorker(HostedVolumeBackupService backups, TimeProvider clock,
    ILogger<HostedVolumeBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await backups.RunDueAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Hosted volume backup sweep failed");
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
