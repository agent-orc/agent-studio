using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Backing store for the Studio P2 "security review, deployment, publish,
/// and wiki grading" bundle. Security audits, deployment compiles, and
/// publish package/website are dispatched through the shared fenced
/// studio-operation ledger (<see cref="TaskServerStore.CreateStudioOperationAsync"/>)
/// and read back through <see cref="TaskServerStore.GetLatestCompletedStudioOperationAsync"/>
/// / <see cref="TaskServerStore.ListCompletedStudioOperationsAsync"/> - there is no
/// separate materialized table for any of them. Publish automation is the
/// one plain setting in the bundle and owns <c>studio_publish_settings</c>.
/// </summary>
public sealed partial class TaskServerStore
{
    internal async Task ApplyStudioP2ComplianceMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_publish_settings(
                project_id TEXT PRIMARY KEY,
                automation_enabled INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            """, ct);
    }

    public async Task<StudioOperationDto> RequestSecurityAuditAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        return await CreateStudioOperationAsync(
            StudioOperationKinds.SecurityAudit,
            project.ProjectId,
            taskId: null,
            request: new { project.ProjectId, actorId },
            trigger: "audit",
            severity: null,
            topic: null,
            ct);
    }

    public async Task<SecurityBaselineDto> GetSecurityBaselineAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var latest = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.SecurityAudit, project.ProjectId, ct);
        return latest is null
            ? new SecurityBaselineDto(null, null, null)
            : new SecurityBaselineDto(latest.OperationId, latest.ResultJson, latest.CompletedAt);
    }

    public async Task<SecurityReviewListResponse> ListSecurityReviewsAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operations = await ListCompletedStudioOperationsAsync(
            StudioOperationKinds.SecurityAudit, project.ProjectId, severity: null, topic: null, trigger: null, limit: 50, ct);
        var items = operations
            .Select(operation => new SecurityReviewListItemDto(operation.OperationId, operation.CompletedAt, operation.ResultJson))
            .ToList();
        return new SecurityReviewListResponse(items);
    }

    public async Task<SecurityReviewDetailDto> GetSecurityReviewByFileNameAsync(
        string projectIdentity, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var latest = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.SecurityAudit, project.ProjectId, ct);

        using var document = TryParseJson(latest?.ResultJson);
        if (document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("reviews", out var reviews)
            && reviews.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in reviews.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("fileName", out var entryFileName)
                    && entryFileName.ValueKind == JsonValueKind.String
                    && string.Equals(entryFileName.GetString(), fileName, StringComparison.Ordinal))
                {
                    return new SecurityReviewDetailDto(fileName, entry.GetRawText());
                }
            }
        }
        throw new KeyNotFoundException($"Security review '{fileName}' was not found.");
    }

    public async Task<StudioOperationDto> RequestDeploymentCompileAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        return await CreateStudioOperationAsync(
            StudioOperationKinds.DeploymentCompile,
            project.ProjectId,
            taskId: null,
            request: new { project.ProjectId, actorId },
            trigger: "compile",
            severity: null,
            topic: null,
            ct);
    }

    public async Task<DeploymentSummaryDto> GetDeploymentSummaryAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var latest = await GetLatestCompletedStudioOperationAsync(StudioOperationKinds.DeploymentCompile, project.ProjectId, ct);
        return latest is null
            ? new DeploymentSummaryDto(null, null, null)
            : new DeploymentSummaryDto(latest.OperationId, latest.ResultJson, latest.CompletedAt);
    }

    public async Task<PublishAutomationSettingDto> SetPublishAutomationAsync(
        string projectIdentity, SetPublishAutomationRequest request, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO studio_publish_settings(project_id, automation_enabled, updated_at)
                VALUES ($project, $enabled, $now)
                ON CONFLICT(project_id) DO UPDATE SET
                    automation_enabled = excluded.automation_enabled,
                    updated_at = excluded.updated_at;
                """, ct, transaction,
                ("$project", project.ProjectId), ("$enabled", request.Enabled ? 1 : 0), ("$now", now));
            await AuditAsync(connection, transaction, actorId, "studio-publish.automation.updated",
                "project", project.ProjectId, JsonSerializer.Serialize(new { request.Enabled }), ct);
        }, ct);
        return new PublishAutomationSettingDto(project.ProjectId, request.Enabled, Parse(now));
    }

    public async Task<StudioOperationDto> RequestPublishPackageAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        return await CreateStudioOperationAsync(
            StudioOperationKinds.PublishPackage,
            project.ProjectId,
            taskId: null,
            request: new { project.ProjectId, actorId },
            trigger: "package",
            severity: null,
            topic: null,
            ct);
    }

    public async Task<StudioOperationDto> RequestPublishWebsiteAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        return await CreateStudioOperationAsync(
            StudioOperationKinds.PublishWebsite,
            project.ProjectId,
            taskId: null,
            request: new { project.ProjectId, actorId },
            trigger: "website",
            severity: null,
            topic: null,
            ct);
    }

    public async Task<StudioOperationDto> RequestWikiGradingRunAsync(string projectIdentity, string actorId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        return await CreateStudioOperationAsync(
            StudioOperationKinds.WikiGradingRun,
            project.ProjectId,
            taskId: null,
            request: new { project.ProjectId, actorId },
            trigger: "run",
            severity: null,
            topic: null,
            ct);
    }

    /// <summary>
    /// Aborts the most recent pending/claimed wiki grading run for the
    /// project, if any. "Nothing in flight" is a reasonable idempotent
    /// outcome for an abort, not an error, so this never throws for that
    /// case - it reports <c>Aborted: false</c> instead.
    /// </summary>
    public async Task<WikiGradingAbortResponse> AbortWikiGradingRunAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operationId = await FindMostRecentNonTerminalOperationAsync(StudioOperationKinds.WikiGradingRun, project.ProjectId, ct);
        if (operationId is null) return new WikiGradingAbortResponse(false, null);
        await CancelStudioOperationAsync(operationId, ct);
        return new WikiGradingAbortResponse(true, operationId);
    }

    private async Task<string?> FindMostRecentNonTerminalOperationAsync(string kind, string? projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var result = await ScalarAsync(connection, """
            SELECT id FROM studio_operations
             WHERE kind = $kind AND project_id = $project
               AND status IN ('pending', 'claimed')
             ORDER BY created_at DESC
             LIMIT 1;
            """, ct, ("$kind", kind), ("$project", projectId));
        return result as string;
    }

    private static JsonDocument? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
