
using AgentStudio.Search;
using AgentStudio.Proposals;

namespace AgentStudio.Host;

/// <summary>
/// Composition root for all HTTP routes. <see cref="Program"/> calls
/// <see cref="MapAllEndpoints"/> once at startup; this method does
/// nothing on its own beyond delegating to the per-feature mappers
/// in <see cref="AgentStudio.Tasks"/> and the sibling
/// classes in this namespace. Splitting the routes by feature keeps
/// each file under ~150 lines and makes "where is the handler for
/// X" answerable in one grep.
/// </summary>
public static class EndpointMapping
{
    public static void MapAllEndpoints(this WebApplication app)
    {
        var mapsLocalV1 = MapsLocalV1(app.Configuration);
        if (!mapsLocalV1) app.MapTaskServerPlaneProxy();
        app.MapAccessSecurityEndpoints();
        var tasks = app.MapGroup("/api/tasks")
            .AddEndpointFilter<TaskOperationTimingFilter>();
        tasks.MapTaskCrudEndpoints();
        tasks.MapBatchMoveEndpoints();
        tasks.MapTaskFilesEndpoints();
        tasks.MapTaskRunnerEndpoints();
        tasks.MapTaskGitEndpoints();
        tasks.MapTaskClaudeEndpoints();
        tasks.MapTaskReviewEvidenceEndpoints();
        tasks.MapTaskExternalCompletionEndpoints();
        tasks.MapTaskCodeReviewEndpoints();
        tasks.MapTaskRegressionRadarEndpoints();
        tasks.MapTaskPipelineEndpoints();
        tasks.MapTaskMergeEndpoints();
        tasks.MapTaskIntegrationRecordEndpoints();
        tasks.MapTaskIntegrationRecoveryEndpoints();

        app.MapEpicEndpoints();
        app.MapCompletedLaneAuditEndpoints();
        app.MapParkedCardEndpoints();
        app.MapRunnerEndpoints();
        app.MapRemoteQueueStarvationEndpoints();
        app.MapAutoReviewQueueEndpoints();
        app.MapAdaptiveReviewParallelismEndpoints();
        app.MapCodingYieldEndpoints();
        app.MapAcceptedIntegrationBackstopEndpoints();
        app.MapAcceptanceRailEndpoints();
        app.MapOrchestratorSessionEndpoints();
        app.MapOrchestratorContextEndpoints();
        app.MapLeaseEndpoints();
        app.MapAttemptAuthorityEndpoints();
        if (mapsLocalV1) app.MapV1ReviewPlaneEndpoints();
        app.MapIntegrationLeaseEndpoints();
        app.MapLogIngestionEndpoints();
        app.MapRunnerEventIngestionEndpoints();
        app.MapDemoReplayEndpoints();
        app.MapArtifactIngestionEndpoints();
        app.MapRegistryEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapWorkspaceSettingsEndpoints();
        app.MapProjectSettingsEndpoints();
        app.MapCrashRecoveryEndpoints();
        app.MapProjectRegressionRadarEndpoints();
        app.MapProjectDocsEndpoints();
        app.MapProjectProposalEndpoints();
        app.MapWikiGradingEndpoints();
        app.MapProjectSteeringDocsEndpoints();
        app.MapSkillReadinessEndpoints();
        app.MapSecurityReviewEndpoints();
        app.MapDesignSurfaceEndpoints();
        app.MapProjectTokenUsageEndpoints();
        app.MapPipelineHealthEndpoints();
        app.MapFailureInterventionEndpoints();
        app.MapTokenPricingEndpoints();
        app.MapReviewDecisionsEndpoints();
        app.MapProjectSnapshotEndpoints();
        app.MapProjectOperatorDashboardEndpoints();
        app.MapProjectCycleTimeEndpoints();
        app.MapTestRunEndpoints();
        app.MapProjectGraphEndpoints();
        app.MapPublishEndpoints();
        if (SecurityProfiles.IsLocal(app.Configuration)) app.MapFilesystemLayerEndpoints();
        app.MapSystemEndpoints();
        app.MapCliEndpoints();
        if (SecurityProfiles.IsLocal(app.Configuration)) app.MapDevToolsEndpoints();
        app.MapAdminConfigEndpoints();
        app.MapGitTelemetryAdminEndpoints();
        app.MapSupervisorEndpoints();
        if (SecurityProfiles.IsLocal(app.Configuration)) app.MapDiagnosticsEndpoints();
        if (SecurityProfiles.IsLocal(app.Configuration)) app.MapInternalProbeEndpoints();
        app.MapTitleGenerationEndpoints();
        app.MapPromptEnhancementEndpoints();
        app.MapAdHocUsageEndpoints();
        app.MapBusEndpoints();
        app.MapRuntimeEventEndpoints();
        app.MapClientEndpoints();
        app.MapAnalysisReportEndpoints();
        app.MapDriftReportEndpoints();
        app.MapTagEndpoints();
        app.MapProjectChatEndpoints();
        app.MapConceptDocsEndpoints();
        app.MapGlobalSearchEndpoints();
        // Interim v1 adapters such as AGT-2325's review plane must remain
        // inside this ownership branch. Never mount a local v1 writer beside
        // the standalone proxy.
        if (mapsLocalV1) app.MapManagementEndpoints();
    }

    internal static bool MapsLocalV1(IConfiguration configuration)
        => !TaskServerPlaneProxy.IsConfigured(configuration);
}
