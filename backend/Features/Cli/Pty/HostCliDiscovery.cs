namespace AgentStudio.Cli;

/// <summary>
/// Host CLI model discovery runs only in the local profile. In the networked
/// and public-demo profiles <see cref="HostCliSpawnGuard"/> refuses interactive
/// provider CLIs, so discovery would only throw on every catalogue read. Those
/// profiles answer from the registry catalogue directly (AGT-3411).
/// </summary>
public static class HostCliDiscovery
{
    /// <summary>Catalogue source of a registry answer given because host discovery is disabled.</summary>
    public const string NetworkedRegistrySource = "networked-registry";

    public static bool Allowed(IConfiguration configuration) => SecurityProfiles.IsLocal(configuration);
}
