using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Aggregates the idempotent migration methods of the 8 P2 "operations and
/// insight" feature groups into one call site, the same role
/// <see cref="StudioP2Endpoints"/> plays for route mapping. Each group's
/// migration method only creates its own tables/indexes; this file adds no
/// schema of its own.
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioP2MigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ApplyStudioP2AdminMigrationAsync(connection, ct);
        await ApplyStudioP2AnalysisDriftMigrationAsync(connection, ct);
        await ApplyStudioP2InsightMigrationAsync(connection, ct);
        await ApplyStudioP2SettingsMigrationAsync(connection, ct);
        await ApplyStudioP2SupervisorMigrationAsync(connection, ct);
        await ApplyStudioP2ProjectSettingsMigrationAsync(connection, ct);
        await ApplyStudioP2DesignMigrationAsync(connection, ct);
        await ApplyStudioP2ComplianceMigrationAsync(connection, ct);
    }
}
