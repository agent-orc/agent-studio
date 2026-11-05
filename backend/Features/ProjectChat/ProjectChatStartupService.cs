using AgentStudio.Diagnostics;
using AgentStudio.Management;

namespace AgentStudio.ProjectChat;

/// <summary>
/// Migrates legacy project chat and refreshes its indexes after the listener starts.
/// Host shutdown cancels the scan and waits for its current file or SQLite operation
/// before disposing services or releasing the workspace.
/// </summary>
public sealed class ProjectChatStartupService : BackgroundService
{
    private const string MigrationId = "project-chat-v1";
    private readonly Action<CancellationToken> _run;

    public ProjectChatStartupService(
        ProjectChatMigration migration,
        ProjectChatIndex index,
        TaskScannerService scanner,
        MigrationStateStore migrationState,
        CrashRecorder crashRecorder)
        : this(ct => Run(migration, index, scanner, migrationState, crashRecorder, ct))
    {
    }

    internal ProjectChatStartupService(Action<CancellationToken> run) => _run = run;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => _run(stoppingToken), stoppingToken);

    public override Task StopAsync(CancellationToken cancellationToken)
        // A timed-out shutdown must not leave a writer using disposed services.
        => base.StopAsync(CancellationToken.None);

    private static void Run(
        ProjectChatMigration migration,
        ProjectChatIndex index,
        TaskScannerService scanner,
        MigrationStateStore migrationState,
        CrashRecorder crashRecorder,
        CancellationToken ct)
    {
        try { migrationState.Begin(MigrationId, "Migrating legacy project chat and refreshing indexes."); }
        catch (Exception ex) { crashRecorder.Record("MigrationState.Begin:ProjectChatMigration", ex); }

        try
        {
            migration.MigrateAll(ct);
            foreach (var entry in scanner.GetWatchPaths())
            {
                ct.ThrowIfCancellationRequested();
                index.EnsureFresh(entry.Path, ct);
            }
            ct.ThrowIfCancellationRequested();
            try { migrationState.Complete(MigrationId); }
            catch (Exception ex) { crashRecorder.Record("MigrationState.Complete:ProjectChatMigration", ex); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { migrationState.Fail(MigrationId, "Interrupted by host shutdown."); }
            catch (Exception ex) { crashRecorder.Record("MigrationState.Fail:ProjectChatMigration", ex); }
        }
        catch (Exception ex)
        {
            try { migrationState.Fail(MigrationId, ex.Message); }
            catch (Exception stateEx) { crashRecorder.Record("MigrationState.Fail:ProjectChatMigration", stateEx); }
            crashRecorder.Record("ProjectChatMigration", ex);
        }
    }
}
