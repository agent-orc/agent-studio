namespace AgentStudio.Management;

/// <summary>
/// Boot effect for backend/Host, the only package that registers LinkSupervisor. When
/// Connectivity:ManifestPath is set, the installation manifest becomes the single source of
/// RunnerLinks; inline RunnerLinks entries are then a second owner and stop the boot.
/// </summary>
public static class ConnectivityManifestLoader
{
    public const string ManifestPathKey = "Connectivity:ManifestPath";

    /// <summary>Returns the configuration RunnerLinks are read from: the manifest when one is set.</summary>
    public static IConfiguration RunnerLinkSource(IConfiguration configuration)
    {
        var path = configuration[ManifestPathKey]?.Trim();
        if (string.IsNullOrEmpty(path)) return configuration;
        var resolved = ConnectivityPolicy.Resolve(
            InstallationConnectivityManifest.Parse(File.ReadAllText(Path.GetFullPath(path))));
        if (resolved.Package != ConnectivityPackages.LegacyBackend)
            throw new InvalidOperationException(
                $"Connectivity manifest selects package '{resolved.Package}'; backend/Host serves legacy-backend only.");
        if (configuration.GetSection("RunnerLinks").GetChildren().Any(section => section.GetValue("Enabled", true)))
            throw new InvalidOperationException(
                "RunnerLinks are configured inline and in the connectivity manifest. Remove the inline RunnerLinks; the manifest is the single owner.");
        var values = new Dictionary<string, string?>(ConnectivityPolicy.RunnerLinksConfiguration(resolved))
        {
            ["TaskRepository"] = configuration["TaskRepository"],
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
