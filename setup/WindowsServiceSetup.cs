namespace AgentStudio.Setup;

/// <summary>
/// Fixed D6 layout: versioned binaries under InstallBase with a current
/// junction, configuration and credentials under ConfigRoot (ProgramData),
/// and task data in DataDirectory.
/// </summary>
internal sealed record WindowsServiceLayout(string InstallBase, string ConfigRoot, string DataDirectory)
{
    public const string TaskServerUrl = "http://127.0.0.1:5071";
    public const string ConnectorUrl = "http://127.0.0.1:5031";
    public const string ConnectorTaskName = "AgentOrchestrator-StudioConnector";

    public static WindowsServiceLayout ForHost(string stateRoot)
        => new(
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } drive ? drive : @"C:\",
                "AgentOrchestrator"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "AgentOrchestrator"),
            Path.Combine(stateRoot, "data"));

    public string ReleaseDirectory(string version) => Path.Combine(InstallBase, $"release-{version}");

    public string ScriptsRoot(string packageRoot) => Path.Combine(packageRoot, "deploy-windows");
}

/// <summary>Pure composition of the D6 PowerShell invocations.</summary>
internal static class WindowsServiceScripts
{
    private static readonly string[] HostArguments =
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    public static IReadOnlyList<string> Install(
        ProductProfile profile,
        WindowsServiceLayout layout,
        string packageRoot,
        string? upstreamUrl,
        string? upstreamTokenFile)
    {
        var scripts = layout.ScriptsRoot(packageRoot);
        var arguments = new List<string>(HostArguments);
        switch (profile)
        {
            case ProductProfile.StudioWindowsServices:
                arguments.AddRange([
                    Path.Combine(scripts, "fallback", "install-fallback-profile.ps1"),
                    "-ReleasePackageRoot", packageRoot,
                    "-DataDirectory", layout.DataDirectory,
                    "-InstallBase", layout.InstallBase,
                    "-ConfigRoot", layout.ConfigRoot,
                    "-ListenUrl", WindowsServiceLayout.TaskServerUrl,
                    "-ConnectorListenUrl", WindowsServiceLayout.ConnectorUrl,
                    "-RestMode", "Normal",
                ]);
                break;
            case ProductProfile.ConnectorWindows:
                arguments.AddRange([
                    Path.Combine(scripts, "studio-connector", "install-studio-connector.ps1"),
                    "-ReleasePackageRoot", packageRoot,
                    "-InstallBase", layout.InstallBase,
                    "-ConfigRoot", layout.ConfigRoot,
                    "-ConnectorListenUrl", WindowsServiceLayout.ConnectorUrl,
                ]);
                if (upstreamUrl is not null) arguments.AddRange(["-UpstreamUrl", upstreamUrl]);
                if (upstreamTokenFile is not null) arguments.AddRange(["-UpstreamTokenFile", upstreamTokenFile]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, "Not a Windows service profile.");
        }
        return arguments;
    }

    /// <summary>Uninstall keeps configuration and task data unless <paramref name="purge"/> is set.</summary>
    public static IReadOnlyList<string> Uninstall(
        ProductProfile profile,
        WindowsServiceLayout layout,
        string scriptsRoot,
        bool purge)
    {
        var arguments = new List<string>(HostArguments)
        {
            Path.Combine(scriptsRoot, "fallback", "uninstall-fallback-profile.ps1"),
            "-InstallBase", layout.InstallBase,
        };
        if (profile == ProductProfile.ConnectorWindows)
            arguments.AddRange(["-TaskNames", WindowsServiceLayout.ConnectorTaskName]);
        else if (purge)
            arguments.AddRange(["-RemoveData", "-DataDirectory", layout.DataDirectory]);
        return arguments;
    }

    /// <summary>Configuration files the purge removes; the connector owns only its own files.</summary>
    public static IReadOnlyList<string> PurgedConfiguration(ProductProfile profile, WindowsServiceLayout layout)
        => profile == ProductProfile.ConnectorWindows
            ?
            [
                Path.Combine(layout.ConfigRoot, "studio-connector.env"),
                Path.Combine(layout.ConfigRoot, "studio-connector-upstream.token"),
                Path.Combine(layout.ConfigRoot, "studio-connector"),
            ]
            : [layout.ConfigRoot];
}

/// <summary>
/// Windows native service path. It needs administrator rights because it
/// registers start-up scheduled tasks and writes ProgramData; the Docker path
/// never reaches this class.
/// </summary>
internal sealed class WindowsServiceSetup(ProcessRunner process, WindowsServiceLayout layout, bool dryRun)
{
    public static void RequireAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows service path runs on Windows only.");
        if (!Environment.IsPrivilegedProcess)
            throw new UnauthorizedAccessException(
                "The native Windows service path needs administrator rights. Right-click " +
                "agent-studio-setup.exe and choose Run as administrator, or run it from an elevated " +
                "terminal. The Docker path (--target docker) runs without elevation.");
    }

    /// <summary>Stages <paramref name="packageRoot"/> as the current release and starts its tasks.</summary>
    public async Task ApplyAsync(ProductProfile profile, string packageRoot, string? upstreamUrl, string? upstreamTokenFile)
    {
        await process.RequireAsync("powershell.exe",
            WindowsServiceScripts.Install(profile, layout, packageRoot, upstreamUrl, upstreamTokenFile));
        if (!dryRun) await VerifyAsync(profile);
    }

    public async Task UninstallAsync(ProductProfile profile, bool purge)
    {
        // The uninstall script removes the release tree it would otherwise run
        // from, so it runs from a private copy of the installed scripts.
        var installed = Path.Combine(layout.InstallBase, "current", "deploy-windows");
        var copy = Path.Combine(Path.GetTempPath(), $"agent-studio-uninstall-{Guid.NewGuid():N}");
        if (!dryRun) ProductSetup.CopyDirectory(installed, copy);
        try
        {
            await process.RequireAsync("powershell.exe",
                WindowsServiceScripts.Uninstall(profile, layout, dryRun ? installed : copy, purge));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
        if (!purge) return;
        foreach (var path in WindowsServiceScripts.PurgedConfiguration(profile, layout))
        {
            if (dryRun) Console.WriteLine($"[dry-run] remove {path}");
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    public void PrintSummary(ProductProfile profile, string? upstreamUrl)
    {
        if (profile == ProductProfile.StudioWindowsServices)
        {
            Console.WriteLine($"Task Server: {WindowsServiceLayout.TaskServerUrl} (mode Normal)");
            Console.WriteLine("Engine: scheduled task AgentOrchestrator-Engine");
        }
        else
        {
            Console.WriteLine($"Upstream Task Server: {upstreamUrl ?? "unchanged"}");
        }
        Console.WriteLine($"Studio connector: {WindowsServiceLayout.ConnectorUrl}");
        Console.WriteLine($"Configuration and credentials: {layout.ConfigRoot}");
        if (profile == ProductProfile.StudioWindowsServices)
            Console.WriteLine($"Task data: {layout.DataDirectory}");
        Console.WriteLine("Services start with Windows as scheduled tasks and restart when they exit.");
    }

    private static async Task VerifyAsync(ProductProfile profile)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (profile == ProductProfile.StudioWindowsServices)
        {
            var ready = await http.GetStringAsync($"{WindowsServiceLayout.TaskServerUrl}/readyz");
            if (!ready.Contains("\"status\":\"ready\"", StringComparison.Ordinal)
                || !ready.Contains("\"mode\":\"Normal\"", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Task Server is not ready in mode Normal at {WindowsServiceLayout.TaskServerUrl}/readyz: {ready}");
        }
        var live = await http.GetStringAsync($"{WindowsServiceLayout.ConnectorUrl}/healthz");
        if (!live.Contains("\"status\":\"live\"", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Studio connector is not live at {WindowsServiceLayout.ConnectorUrl}/healthz: {live}");
    }
}
