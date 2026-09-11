namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration hook for the Studio P2 supervisor/insight bundle. Every
/// route in <see cref="StudioP2SupervisorEndpoints"/> is a direct call
/// against the shared <see cref="TaskServerStore"/> singleton, so this group
/// registers nothing further. The stub exists so the integration step can
/// call it unconditionally for every group.
/// </summary>
public static class StudioP2SupervisorServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2SupervisorServices(this IServiceCollection services) => services;
}
