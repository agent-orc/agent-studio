namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration seam for Group G4 (global CLI/quota settings, crash
/// recovery, watch paths). All of this bundle's logic lives directly on
/// <see cref="TaskServerStore"/> (see
/// <c>TaskServerStudioP2SettingsStore.cs</c>), so there is nothing to
/// register; this stub exists so the integration step has a stable,
/// uniform <c>Add*Services</c> call to make across every P2 group.
/// </summary>
public static class StudioP2SettingsServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2SettingsServices(this IServiceCollection services) => services;
}
