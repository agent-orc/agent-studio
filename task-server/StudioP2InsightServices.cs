namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration hook for the Studio P2 "bus messages, token usage,
/// runtime events, token pricing" bundle. This group registers nothing
/// beyond the shared <see cref="TaskServerStore"/> singleton: every route
/// in <see cref="StudioP2InsightEndpoints"/> is a direct store call or pure
/// in-process computation. This stub exists so the integration step can
/// call it unconditionally for every group.
/// </summary>
public static class StudioP2InsightServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2InsightServices(this IServiceCollection services) => services;
}
