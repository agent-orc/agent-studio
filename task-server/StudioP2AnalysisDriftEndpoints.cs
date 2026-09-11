using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The P2 "analysis and drift" route bundle: the durable analysis-report
/// submission log and its schedule settings, plus the dispatch-backed drift
/// action surface (ADR / Code, Docs / Marketing, Software / Architecture,
/// and Code Pattern drift) and its architecture and reports projections.
/// See <see cref="TaskServerStudioP2AnalysisDriftStore"/> for storage and
/// <c>StudioOperationsEndpoints.cs</c> for the shared fenced Runner-operation
/// dispatch ledger every drift action rides on.
/// </summary>
public static class StudioP2AnalysisDriftEndpoints
{
    public static void MapStudioP2AnalysisDriftEndpoints(this WebApplication app)
    {
        MapAnalysisEndpoints(app);
        MapDriftEndpoints(app);
    }

    private static void MapAnalysisEndpoints(WebApplication app)
    {
        var analysis = app.MapGroup("/api/v1/studio/analysis/{project}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        analysis.MapGet("/reports", async (
            string project,
            string? severity,
            string? topic,
            string? trigger,
            int? limit,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ListAnalysisReportsAsync(project, severity, topic, trigger, limit, ct)));

        analysis.MapPost("/reports", async (
            string project, SubmitAnalysisReportRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.CreateAnalysisReportAsync(project, request, ct), StatusCodes.Status201Created))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        analysis.MapGet("/reports/{reportId}", async (
            string project, string reportId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetAnalysisReportAsync(project, reportId, ct)));

        analysis.MapGet("/schedule", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetAnalysisScheduleAsync(project, ct)));

        analysis.MapPut("/schedule", async (
            string project, UpdateAnalysisScheduleRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.UpsertAnalysisScheduleAsync(project, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);
    }

    private static void MapDriftEndpoints(WebApplication app)
    {
        var drift = app.MapGroup("/api/v1/studio/drift/{project}")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        drift.MapPost("/actions/adr-code-drift", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.TriggerAdrCodeDriftAsync(project, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        drift.MapPost("/actions/docs-marketing-drift", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.TriggerDocsMarketingDriftAsync(project, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        drift.MapPost("/actions/software-architecture-drift", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.TriggerSoftwareArchitectureDriftAsync(project, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        drift.MapGet("/actions/software-architecture-drift/prompt", async (
            string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetSoftwareArchitectureDriftPromptAsync(project, ct)));

        drift.MapGet("/architecture", async (string project, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetDriftArchitectureAsync(project, ct)));

        drift.MapPost("/architecture/{modelId}/elements/{elementId}/status", async (
            string project,
            string modelId,
            string elementId,
            UpdateDriftElementStatusRequest request,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.SetDriftElementStatusAsync(project, modelId, elementId, request, ct)))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        drift.MapGet("/reports", async (
            string project,
            string? severity,
            string? topic,
            string? trigger,
            int? limit,
            TaskServerStore store,
            CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.ListDriftReportsAsync(project, severity, topic, trigger, limit, ct)));

        drift.MapGet("/reports/{reportId}", async (
            string project, string reportId, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeNullableAsync(() => store.GetDriftReportAsync(project, reportId, ct)));

        var driftActions = app.MapGroup("/api/v1/studio/drift/actions")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        driftActions.MapPost("/code-pattern-drift", async (
            TriggerCodePatternDriftRequest request, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(
                () => store.TriggerCodePatternDriftAsync(request, ct), StatusCodes.Status202Accepted))
            .RequireTaskServerScope(TaskServerScopes.TasksWrite);

        driftActions.MapGet("/code-pattern-drift/rules", async (TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.GetCodePatternDriftRulesAsync(ct)));
    }
}
