namespace AgentStudio.TaskServer;

/// <summary>
/// Aggregates the 8 independently developed P2 "operations and insight"
/// feature groups (102 routes total: admin/prompts/utility, analysis and
/// drift, bus/token-usage/runtime, global CLI/quota/crash-recovery/watch
/// paths, supervisor and queue metrics, project settings and CRUD, design
/// and proposals, security/deployment/publish/wiki-grading) into one call
/// site so <c>Program.cs</c> only ever grows by one line for this bundle.
/// Each group maps its own routes and owns its own tables; this file adds
/// no behavior of its own.
/// </summary>
public static class StudioP2Endpoints
{
    public static void MapStudioP2Endpoints(this WebApplication app)
    {
        app.MapStudioP2AdminEndpoints();
        app.MapStudioP2AnalysisDriftEndpoints();
        app.MapStudioP2InsightEndpoints();
        app.MapStudioP2SettingsEndpoints();
        app.MapStudioP2SupervisorEndpoints();
        app.MapStudioP2ProjectSettingsEndpoints();
        app.MapStudioP2DesignEndpoints();
        app.MapStudioP2ComplianceEndpoints();
    }

    public static IServiceCollection AddStudioP2Services(this IServiceCollection services)
        => services
            .AddStudioP2AdminServices()
            .AddStudioP2AnalysisDriftServices()
            .AddStudioP2InsightServices()
            .AddStudioP2SettingsServices()
            .AddStudioP2SupervisorServices()
            .AddStudioP2ProjectSettingsServices()
            .AddStudioP2DesignServices()
            .AddStudioP2ComplianceServices();
}
