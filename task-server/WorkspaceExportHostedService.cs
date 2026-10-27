using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed record WorkspaceExportStatusDto(
    bool Enabled,
    string? Root,
    string Branch,
    bool Push,
    DateTime? LastExportAt,
    WorkspaceExportResult? LastExport,
    string? LastCommitSha,
    DateTime? LastPushAt,
    bool? LastPushSucceeded,
    int? AheadCommits,
    string? LastError);

/// <summary>Last outcome of the export job, for operators; never read on a board path.</summary>
public sealed class WorkspaceExportStatus
{
    private readonly object _gate = new();
    private WorkspaceExportStatusDto _current = new(false, null, "main", false, null, null, null, null, null, null, null);

    public WorkspaceExportStatusDto Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(Func<WorkspaceExportStatusDto, WorkspaceExportStatusDto> change)
    {
        lock (_gate) _current = change(_current);
    }
}

/// <summary>
/// Drives the periodic workspace export (migration M6, decision D9 A): every
/// <see cref="TaskServerOptions.WorkspaceExportIntervalMinutes"/> it writes the
/// task folders, commits the change, and, when enabled, pushes. Off unless
/// <see cref="TaskServerOptions.WorkspaceExportRoot"/> names a directory. A
/// failed or hanging push only grows the export's own backlog: the board and
/// every other read answer from SQLite and never wait for this job.
/// </summary>
public sealed class WorkspaceExportHostedService(
    WorkspaceExportService export,
    WorkspaceExportStatus status,
    IOptions<TaskServerOptions> options,
    TimeProvider clock,
    ILogger<WorkspaceExportHostedService> logger,
    WorkspaceExportGit? git = null) : BackgroundService
{
    private readonly WorkspaceExportGit _git = git ?? new WorkspaceExportGit(options.Value.GitCommand);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.WorkspaceExportRoot))
        {
            logger.LogInformation("Workspace export is off: TaskServer:WorkspaceExportRoot is not set.");
            return;
        }
        var interval = TimeSpan.FromMinutes(Math.Clamp(configured.WorkspaceExportIntervalMinutes, 1, 24 * 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Workspace export cycle failed");
                status.Update(current => current with { LastError = exception.Message });
            }
            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One export, commit and push; returns the status it recorded.</summary>
    public async Task<WorkspaceExportStatusDto> RunOnceAsync(CancellationToken ct)
    {
        var configured = options.Value;
        var root = Path.GetFullPath(configured.WorkspaceExportRoot!);
        var branch = string.IsNullOrWhiteSpace(configured.WorkspaceExportBranch) ? "main" : configured.WorkspaceExportBranch.Trim();
        var result = await export.ExportAsync(root, ct);
        var exportedAt = clock.GetUtcNow().UtcDateTime;
        WorkspaceExportCommit? commit = null;
        WorkspaceExportPush? push = null;
        if (WorkspaceExportGit.IsRepository(root))
        {
            commit = await _git.CommitAsync(root, result, ct);
            if (configured.WorkspaceExportPush && commit.Error is null)
                push = await _git.PushAsync(root, branch,
                    TimeSpan.FromSeconds(Math.Clamp(configured.WorkspaceExportPushTimeoutSeconds, 10, 3600)), ct);
        }
        logger.LogInformation(
            "workspace-export root={Root} tasks={Tasks} written={Written} removed={Removed} commit={Commit} push={Push} ahead={Ahead}",
            root, result.Tasks, result.FilesWritten, result.FilesRemoved, commit?.Sha ?? "none",
            push is null ? "off" : push.Succeeded ? "ok" : "failed", push?.AheadCommits);
        if (push is { Succeeded: false })
            logger.LogWarning("workspace-export-push-failed root={Root} branch={Branch} ahead={Ahead} error={Error}",
                root, branch, push.AheadCommits, push.Error);
        var error = commit?.Error ?? push?.Error;
        status.Update(current => new WorkspaceExportStatusDto(
            true, root, branch, configured.WorkspaceExportPush, exportedAt, result,
            commit?.Sha ?? current.LastCommitSha,
            push is { Attempted: true } ? exportedAt : current.LastPushAt,
            push?.Succeeded ?? current.LastPushSucceeded,
            push?.AheadCommits ?? current.AheadCommits,
            error));
        return status.Current;
    }
}
