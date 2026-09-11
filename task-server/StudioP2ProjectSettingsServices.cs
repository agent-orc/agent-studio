namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration hook for the Studio P2 project-settings bundle. All
/// routes in this bundle are served directly by <see cref="TaskServerStore"/>
/// (already registered elsewhere), so there is nothing additional to wire up
/// here; this stub exists so the integration step has a consistent
/// <c>Add*Services</c> extension to call for every bundle.
/// </summary>
public static class StudioP2ProjectSettingsServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2ProjectSettingsServices(this IServiceCollection services) => services;
}
