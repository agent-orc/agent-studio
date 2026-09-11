namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration hook for the Studio P2 "admin/prompts/utility" bundle.
/// This group registers nothing beyond the shared <see cref="TaskServerStore"/>
/// singleton: every route in <see cref="StudioP2AdminEndpoints"/> is a
/// direct store call or pure in-process computation. This stub exists so
/// the integration step can call it unconditionally for every group.
/// </summary>
public static class StudioP2AdminServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2AdminServices(this IServiceCollection services) => services;
}
