using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Storage for the P2 "analysis and drift" route bundle.
/// <para>
/// Analysis reports (<c>studio_analysis_reports</c>) and their schedule
/// (<c>studio_analysis_schedule</c>) are durable Task-Server-owned state: a
/// caller submits a report body directly, there is no Runner dispatch
/// involved.
/// </para>
/// <para>
/// Drift actions are dispatch-backed: the four drift kinds
/// (<see cref="StudioOperationKinds.DriftAdrCodeDrift"/>,
/// <see cref="StudioOperationKinds.DriftDocsMarketingDrift"/>,
/// <see cref="StudioOperationKinds.DriftSoftwareArchitectureDrift"/>,
/// <see cref="StudioOperationKinds.DriftCodePatternDrift"/>) are created as
/// fenced <c>studio_operations</c> rows (see
/// <c>TaskServerStudioOperationsStore.cs</c>) and this file only projects
/// their durable results into the drift report/architecture shapes. The one
/// piece of drift state this file owns directly is
/// <c>studio_drift_element_status</c>, the per-element tracking-status
/// override a user sets on the architecture surface without spawning a new
/// immutable drift report.
/// </para>
/// </summary>
public sealed partial class TaskServerStore
{
    private static readonly IReadOnlyList<string> DriftKinds =
    [
        StudioOperationKinds.DriftAdrCodeDrift,
        StudioOperationKinds.DriftDocsMarketingDrift,
        StudioOperationKinds.DriftSoftwareArchitectureDrift,
        StudioOperationKinds.DriftCodePatternDrift,
    ];

    private static readonly IReadOnlyList<DriftRuleDescriptor> CodePatternDriftRuleCatalog =
    [
        new DriftRuleDescriptor(
            "naming-consistency",
            "Naming consistency",
            "Flags sibling implementations of the same concept that diverge in naming convention from the canonical instance."),
        new DriftRuleDescriptor(
            "duplicate-structure",
            "Duplicate structure",
            "Flags near-duplicate blocks of logic that should share a single implementation but have drifted apart."),
        new DriftRuleDescriptor(
            "error-handling-pattern",
            "Error handling pattern",
            "Flags call sites that skip the project's standard error and retry handling pattern used elsewhere for the same operation kind."),
        new DriftRuleDescriptor(
            "validation-pattern",
            "Validation pattern",
            "Flags input validation that diverges from the canonical validation helper for the same input shape."),
        new DriftRuleDescriptor(
            "logging-pattern",
            "Logging pattern",
            "Flags call sites missing the structured logging fields present at other call sites of the same operation."),
    ];

    internal async Task ApplyStudioP2AnalysisDriftMigrationAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS studio_analysis_reports(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                trigger_tag TEXT,
                severity TEXT,
                topic TEXT,
                summary_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_analysis_schedule(
                project_id TEXT PRIMARY KEY,
                schedule_json TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS studio_drift_element_status(
                project_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                element_id TEXT NOT NULL,
                status TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(project_id, model_id, element_id)
            );
            CREATE INDEX IF NOT EXISTS ix_studio_analysis_reports_project_created
                ON studio_analysis_reports(project_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_studio_drift_element_status_project
                ON studio_drift_element_status(project_id);
            """, ct);
    }

    // ------------------------------------------------------------------
    // Analysis reports and schedule.
    // ------------------------------------------------------------------

    public async Task<AnalysisReportListResponse> ListAnalysisReportsAsync(
        string projectIdentity, string? severity, string? topic, string? trigger, int? limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var cap = Math.Clamp(limit ?? 100, 1, 500);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, trigger_tag, severity, topic, summary_json, created_at
              FROM studio_analysis_reports
             WHERE project_id = $project
               AND ($severity IS NULL OR severity = $severity)
               AND ($topic IS NULL OR topic = $topic)
               AND ($trigger IS NULL OR trigger_tag = $trigger)
             ORDER BY created_at DESC
             LIMIT $limit;
            """,
            ("$project", project.ProjectId), ("$severity", severity), ("$topic", topic),
            ("$trigger", trigger), ("$limit", cap));
        var reports = new List<AnalysisReportDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) reports.Add(ReadAnalysisReport(reader));
        return new AnalysisReportListResponse(reports);
    }

    public async Task<AnalysisReportDto> CreateAnalysisReportAsync(
        string projectIdentity, SubmitAnalysisReportRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (request.Summary.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("A report summary is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var id = $"sar_{Guid.NewGuid():N}";
        var now = Iso(UtcNow);
        var summaryJson = JsonSerializer.Serialize(request.Summary);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_analysis_reports(id, project_id, trigger_tag, severity, topic, summary_json, created_at)
            VALUES ($id, $project, $trigger, $severity, $topic, $summary, $now);
            """, ct,
            ("$id", id), ("$project", project.ProjectId), ("$trigger", request.Trigger),
            ("$severity", request.Severity), ("$topic", request.Topic), ("$summary", summaryJson), ("$now", now));
        return (await GetAnalysisReportAsync(project.ProjectId, id, ct))!;
    }

    public async Task<AnalysisReportDto?> GetAnalysisReportAsync(string projectIdentity, string reportId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT id, project_id, trigger_tag, severity, topic, summary_json, created_at
              FROM studio_analysis_reports
             WHERE id = $id AND project_id = $project;
            """, ("$id", reportId), ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAnalysisReport(reader) : null;
    }

    public async Task<AnalysisScheduleDto> GetAnalysisScheduleAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        return await ReadAnalysisScheduleAsync(project.ProjectId, ct);
    }

    public async Task<AnalysisScheduleDto> UpsertAnalysisScheduleAsync(
        string projectIdentity, UpdateAnalysisScheduleRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        var scheduleJson = JsonSerializer.Serialize(request.Schedule);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_analysis_schedule(project_id, schedule_json, updated_at)
            VALUES ($project, $schedule, $now)
            ON CONFLICT(project_id) DO UPDATE SET schedule_json = excluded.schedule_json, updated_at = excluded.updated_at;
            """, ct,
            ("$project", project.ProjectId), ("$schedule", scheduleJson), ("$now", now));
        return await ReadAnalysisScheduleAsync(project.ProjectId, ct);
    }

    private async Task<AnalysisScheduleDto> ReadAnalysisScheduleAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT schedule_json, updated_at FROM studio_analysis_schedule WHERE project_id = $project;
            """, ("$project", projectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new AnalysisScheduleDto(projectId, null, null);
        JsonElement? schedule = null;
        if (!reader.IsDBNull(0))
        {
            using var doc = JsonDocument.Parse(reader.GetString(0));
            schedule = doc.RootElement.Clone();
        }
        return new AnalysisScheduleDto(projectId, schedule, Parse(reader.GetString(1)));
    }

    private static AnalysisReportDto ReadAnalysisReport(SqliteDataReader reader)
    {
        using var doc = JsonDocument.Parse(reader.GetString(5));
        return new AnalysisReportDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            doc.RootElement.Clone(),
            Parse(reader.GetString(6)));
    }

    // ------------------------------------------------------------------
    // Drift actions (dispatch-backed).
    // ------------------------------------------------------------------

    public Task<StudioOperationAcceptedResponse> TriggerAdrCodeDriftAsync(string projectIdentity, CancellationToken ct)
        => DispatchDriftAsync(StudioOperationKinds.DriftAdrCodeDrift, projectIdentity, "adr-code-drift", ct);

    public Task<StudioOperationAcceptedResponse> TriggerDocsMarketingDriftAsync(string projectIdentity, CancellationToken ct)
        => DispatchDriftAsync(StudioOperationKinds.DriftDocsMarketingDrift, projectIdentity, "docs-marketing-drift", ct);

    public Task<StudioOperationAcceptedResponse> TriggerSoftwareArchitectureDriftAsync(string projectIdentity, CancellationToken ct)
        => DispatchDriftAsync(StudioOperationKinds.DriftSoftwareArchitectureDrift, projectIdentity, "software-architecture-drift", ct);

    public async Task<StudioOperationAcceptedResponse> TriggerCodePatternDriftAsync(
        TriggerCodePatternDriftRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId))
            throw new ArgumentException("A project id or name is required.");
        return await DispatchDriftAsync(StudioOperationKinds.DriftCodePatternDrift, request.ProjectId, "code-pattern-drift", ct);
    }

    private async Task<StudioOperationAcceptedResponse> DispatchDriftAsync(
        string kind, string projectIdentity, string trigger, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await CreateStudioOperationAsync(
            kind, project.ProjectId, taskId: null, request: new DriftDispatchRequest(project.ProjectId),
            trigger: trigger, severity: null, topic: null, ct: ct);
        return new StudioOperationAcceptedResponse(operation.OperationId, operation.Kind, operation.Status, operation.CreatedAt);
    }

    public async Task<DriftActionPromptResponse> GetSoftwareArchitectureDriftPromptAsync(string projectIdentity, CancellationToken ct)
    {
        await RequireProjectAsync(projectIdentity, ct);
        return new DriftActionPromptResponse(
            StudioOperationKinds.DriftSoftwareArchitectureDrift,
            "Compares the project's documented architecture model against the current source tree, module " +
            "boundaries, schemas, and tests, and reports a per-element drift verdict. Dispatched to a Runner, " +
            "which executes the scan in a detached checkout; poll the drift reports list or the architecture " +
            "surface for the result once the operation completes.");
    }

    public async Task<DriftArchitectureResponse> GetDriftArchitectureAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await GetLatestCompletedStudioOperationAsync(
            StudioOperationKinds.DriftSoftwareArchitectureDrift, project.ProjectId, ct);
        var overrides = await ListDriftElementStatusOverridesAsync(project.ProjectId, ct);
        JsonElement? model = null;
        if (operation is not null && !string.IsNullOrWhiteSpace(operation.ResultJson))
        {
            using var doc = JsonDocument.Parse(operation.ResultJson);
            model = doc.RootElement.Clone();
        }
        return new DriftArchitectureResponse(operation?.OperationId, operation?.CompletedAt, model, overrides);
    }

    public async Task<DriftElementStatusDto> SetDriftElementStatusAsync(
        string projectIdentity,
        string modelId,
        string elementId,
        UpdateDriftElementStatusRequest request,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request.Status))
            throw new ArgumentException("Status is required.");
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = Iso(UtcNow);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_drift_element_status(project_id, model_id, element_id, status, updated_at)
            VALUES ($project, $model, $element, $status, $now)
            ON CONFLICT(project_id, model_id, element_id)
            DO UPDATE SET status = excluded.status, updated_at = excluded.updated_at;
            """, ct,
            ("$project", project.ProjectId), ("$model", modelId), ("$element", elementId),
            ("$status", request.Status), ("$now", now));
        return new DriftElementStatusDto(project.ProjectId, modelId, elementId, request.Status, Parse(now));
    }

    private async Task<IReadOnlyList<DriftElementStatusDto>> ListDriftElementStatusOverridesAsync(
        string projectId, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT project_id, model_id, element_id, status, updated_at
              FROM studio_drift_element_status
             WHERE project_id = $project;
            """, ("$project", projectId));
        var overrides = new List<DriftElementStatusDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            overrides.Add(new DriftElementStatusDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4))));
        return overrides;
    }

    public async Task<DriftReportListResponse> ListDriftReportsAsync(
        string projectIdentity, string? severity, string? topic, string? trigger, int? limit, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var cap = Math.Clamp(limit ?? 100, 1, 500);
        var operations = new List<StudioOperationDto>();
        foreach (var kind in DriftKinds)
            operations.AddRange(await ListCompletedStudioOperationsAsync(kind, project.ProjectId, severity, topic, trigger, cap, ct));
        var ordered = operations
            .OrderByDescending(operation => operation.CompletedAt)
            .Take(cap)
            .Select(ToDriftReportDto)
            .ToList();
        return new DriftReportListResponse(ordered);
    }

    public async Task<DriftReportDto?> GetDriftReportAsync(string projectIdentity, string reportId, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var operation = await GetStudioOperationAsync(reportId, ct);
        if (operation is null
            || !string.Equals(operation.ProjectId, project.ProjectId, StringComparison.Ordinal)
            || !operation.Kind.StartsWith("drift.", StringComparison.Ordinal))
            return null;
        return ToDriftReportDto(operation);
    }

    private static DriftReportDto ToDriftReportDto(StudioOperationDto operation)
    {
        JsonElement? summary = null;
        if (!string.IsNullOrWhiteSpace(operation.ResultJson))
        {
            using var doc = JsonDocument.Parse(operation.ResultJson);
            summary = doc.RootElement.Clone();
        }
        return new DriftReportDto(
            operation.OperationId, operation.ProjectId, operation.Kind, operation.Trigger,
            operation.Severity, operation.Topic, operation.CompletedAt, summary);
    }

    public Task<IReadOnlyList<DriftRuleDescriptor>> GetCodePatternDriftRulesAsync(CancellationToken ct)
        => Task.FromResult(CodePatternDriftRuleCatalog);
}
