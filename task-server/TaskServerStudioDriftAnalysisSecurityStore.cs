using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Analysis, drift, and security review projections and dispatches. Reports
/// and reviews are rows in the shared <c>studio_operations</c> ledger
/// (TaskServerStudioOperationsStore.cs); this file owns the
/// domain-specific request shaping (the assembled prompt body sent to the
/// dispatched task) and the two structured, durable side tables the bundle
/// needs beyond that ledger: architecture element status and analysis
/// schedules. The code-pattern-drift action is evaluated in process because
/// its rule set is a deterministic, no-LLM lint that needs no checkout.
/// </summary>
public sealed partial class TaskServerStore
{
    // --- Analysis --------------------------------------------------------------

    public Task<StudioOperationListResponse> ListAnalysisReportsAsync(string projectIdentity, CancellationToken ct) =>
        ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Analysis, "report", ct);

    public Task<StudioOperationDto> GetAnalysisReportAsync(string projectIdentity, string reportId, CancellationToken ct) =>
        GetStudioOperationAsync(projectIdentity, StudioOperationDomains.Analysis, reportId, ct);

    public async Task<StudioOperationDto> CreateAnalysisReportAsync(
        string projectIdentity, DispatchStudioOperationRequest request, string actorId, CancellationToken ct)
    {
        var prompt = request.Prompt ?? "Produce an analysis report for the current project state.";
        return await DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Analysis, "report", "Analysis report", prompt, request, actorId, ct);
    }

    public async Task<AnalysisScheduleDto> GetAnalysisScheduleAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT schedule_json, updated_at FROM studio_schedules WHERE project_id = $project AND domain = $domain;",
            ("$project", project.ProjectId), ("$domain", StudioOperationDomains.Analysis));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new AnalysisScheduleDto(project.ProjectId, string.Empty, false, UtcNow);
        var schedule = System.Text.Json.JsonSerializer.Deserialize<UpdateAnalysisScheduleRequest>(reader.GetString(0))!;
        return new AnalysisScheduleDto(project.ProjectId, schedule.CronExpression, schedule.Enabled, Parse(reader.GetString(1)));
    }

    public async Task<AnalysisScheduleDto> UpdateAnalysisScheduleAsync(
        string projectIdentity, UpdateAnalysisScheduleRequest request, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_schedules(project_id, domain, schedule_json, updated_at)
            VALUES ($project, $domain, $schedule, $now)
            ON CONFLICT(project_id, domain) DO UPDATE SET schedule_json = excluded.schedule_json, updated_at = excluded.updated_at;
            """, ct, ("$project", project.ProjectId), ("$domain", StudioOperationDomains.Analysis),
            ("$schedule", System.Text.Json.JsonSerializer.Serialize(request)), ("$now", Iso(now)));
        return new AnalysisScheduleDto(project.ProjectId, request.CronExpression, request.Enabled, now);
    }

    // --- Drift reports and actions -----------------------------------------------

    public Task<StudioOperationListResponse> ListDriftReportsAsync(string projectIdentity, CancellationToken ct) =>
        ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Drift, null, ct);

    public Task<StudioOperationDto> GetDriftReportAsync(string projectIdentity, string reportId, CancellationToken ct) =>
        GetStudioOperationAsync(projectIdentity, StudioOperationDomains.Drift, reportId, ct);

    public Task<StudioOperationDto> DispatchDriftActionAsync(
        string projectIdentity, string action, DispatchStudioOperationRequest request, string actorId, CancellationToken ct)
    {
        var prompt = request.Prompt ?? $"Produce a {action} drift report for the current project state.";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Drift, action, $"Drift report: {action}", prompt, request, actorId, ct);
    }

    public async Task<ArchitectureElementListResponse> GetArchitectureAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT model_id, element_id, status, updated_at, updated_by FROM studio_architecture_elements
             WHERE project_id = $project ORDER BY model_id, element_id;
            """, ("$project", project.ProjectId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ArchitectureElementDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new ArchitectureElementDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)), reader.GetString(4)));
        return new ArchitectureElementListResponse(result);
    }

    public async Task<ArchitectureElementDto> UpdateArchitectureElementStatusAsync(
        string projectIdentity, string modelId, string elementId, UpdateArchitectureElementStatusRequest request,
        string actorId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_architecture_elements(project_id, model_id, element_id, status, updated_at, updated_by)
            VALUES ($project, $model, $element, $status, $now, $actor)
            ON CONFLICT(project_id, model_id, element_id) DO UPDATE SET
                status = excluded.status, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
            """, ct, ("$project", project.ProjectId), ("$model", modelId), ("$element", elementId),
            ("$status", request.Status), ("$now", Iso(now)), ("$actor", actorId));
        return new ArchitectureElementDto(modelId, elementId, request.Status, now, actorId);
    }

    public Task<CodePatternDriftRulesResponse> GetCodePatternDriftRulesAsync(CancellationToken ct) =>
        Task.FromResult(new CodePatternDriftRulesResponse(CodePatternDriftRules));

    public async Task<CodePatternDriftResponse> EvaluateCodePatternDriftAsync(
        CodePatternDriftRequest request, string actorId, CancellationToken ct)
    {
        var findings = new List<CodePatternDriftFinding>();
        foreach (var file in request.Files)
            foreach (var rule in CodePatternDriftRules)
                if (file.Content.Contains(rule.RuleId, StringComparison.OrdinalIgnoreCase)
                    || (rule.RuleId == "no-console-log" && file.Content.Contains("console.log", StringComparison.Ordinal))
                    || (rule.RuleId == "no-todo-fixme" && (file.Content.Contains("TODO", StringComparison.Ordinal) || file.Content.Contains("FIXME", StringComparison.Ordinal))))
                    findings.Add(new CodePatternDriftFinding(file.Path, rule.RuleId, rule.Description));
        var evaluatedAt = UtcNow;
        await RecordCompletedStudioOperationAsync(
            null, StudioOperationDomains.Drift, "code-pattern-drift", "Code pattern drift",
            request, new { findings, evaluatedAt }, actorId, ct);
        return new CodePatternDriftResponse(findings, evaluatedAt);
    }

    private static readonly IReadOnlyList<CodePatternDriftRuleDto> CodePatternDriftRules =
    [
        new("no-console-log", "No console.log", "Production code should not contain console.log statements.", "warn"),
        new("no-todo-fixme", "No TODO/FIXME markers", "Unresolved TODO or FIXME markers indicate incomplete work.", "info"),
    ];

    // --- Security ------------------------------------------------------------

    public Task<StudioOperationListResponse> ListSecurityReviewsAsync(string projectIdentity, CancellationToken ct) =>
        ListStudioOperationsAsync(projectIdentity, StudioOperationDomains.Security, "review", ct);

    public Task<StudioOperationDto> GetSecurityReviewAsync(string projectIdentity, string fileName, CancellationToken ct) =>
        GetStudioOperationAsync(projectIdentity, StudioOperationDomains.Security, fileName, ct);

    public async Task<SecurityBaselineResponse> GetSecurityBaselineAsync(string projectIdentity, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectIdentity, ct);
        var accepted = (await ListSecurityReviewsAsync(projectIdentity, ct)).Operations
            .Where(operation => operation.Status == StudioOperationStatuses.Accepted)
            .OrderByDescending(operation => operation.UpdatedAt)
            .FirstOrDefault();
        return new SecurityBaselineResponse(project.ProjectId, accepted?.ResultJson, accepted?.UpdatedAt);
    }

    public Task<StudioOperationDto> DispatchSecurityAuditAsync(
        string projectIdentity, SecurityAuditRequest request, string actorId, CancellationToken ct)
    {
        var prompt = string.IsNullOrWhiteSpace(request.Scope)
            ? "Perform a security review of the current project state."
            : $"Perform a security review scoped to: {request.Scope}";
        return DispatchStudioOperationAsync(
            projectIdentity, StudioOperationDomains.Security, "review", "Security review", prompt, request, actorId, ct);
    }
}
