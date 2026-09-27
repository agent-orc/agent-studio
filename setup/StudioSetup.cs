using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentStudio.Setup;

internal sealed record StudioAnswers(
    string? Mode,
    string? Target,
    string? ReleaseVersion,
    string? ReleaseDirectory,
    string? InstallDirectory,
    int? Port);

internal sealed record StudioRequest(
    string Action,
    string Target,
    string? Version,
    string? ReleaseDirectory,
    string? InstallDirectory,
    int Port,
    bool Unattended,
    bool Purge,
    bool DryRun);

internal static class StudioSetup
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Handles(string[] args)
        => args.Length == 0
           || (args.Length == 1 && args[0] is "--help" or "-h")
           || args[0] is "update" or "rollback" or "uninstall" or "--uninstall"
           || args.Contains("studio", StringComparer.OrdinalIgnoreCase)
           || args.Contains("--unattended", StringComparer.Ordinal);

    internal static StudioRequest Parse(string[] args)
    {
        var answerFileIndex = Array.IndexOf(args, "--unattended");
        StudioAnswers? answers = null;
        if (answerFileIndex >= 0)
        {
            if (answerFileIndex + 1 >= args.Length || args[answerFileIndex + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--unattended requires a JSON answer file.");
            answers = JsonSerializer.Deserialize<StudioAnswers>(File.ReadAllText(args[answerFileIndex + 1]), Json)
                      ?? throw new ArgumentException("Answer file is empty.");
        }
        var action = args.FirstOrDefault() switch
        {
            "update" => "update",
            "rollback" => "rollback",
            "uninstall" or "--uninstall" => "uninstall",
            _ => "install",
        };
        var target = answers?.Target ?? "docker";
        var version = answers?.ReleaseVersion;
        var releaseDirectory = answers?.ReleaseDirectory;
        var installDirectory = answers?.InstallDirectory;
        var port = answers?.Port ?? 4011;
        var purge = false;
        var dryRun = false;
        for (var i = action == "install" ? 0 : 1; i < args.Length; i++)
        {
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{args[i - 1]} requires a value.");
                return args[i];
            }
            switch (args[i])
            {
                case "--mode":
                    if (Value() != "studio")
                        throw new ArgumentException("This install flow requires --mode studio.");
                    break;
                case "--target": target = Value(); break;
                case "--release-version": version = Value(); break;
                case "--release-dir": releaseDirectory = Value(); break;
                case "--install-dir": installDirectory = Value(); break;
                case "--port":
                    if (!int.TryParse(Value(), out port))
                        throw new ArgumentException("--port must be an integer.");
                    break;
                case "--unattended": _ = Value(); break;
                case "--purge": purge = true; break;
                case "--dry-run": dryRun = true; break;
                case "--uninstall": action = "uninstall"; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (answers?.Mode is not null && answers.Mode != "studio")
            throw new ArgumentException("Answer file mode must be studio.");
        if (target is not ("docker" or "native"))
            throw new ArgumentException("--target must be docker or native.");
        if (port is < 1 or > 65535)
            throw new ArgumentException("--port must be between 1 and 65535.");
        if (releaseDirectory is not null) releaseDirectory = Path.GetFullPath(releaseDirectory);
        if (installDirectory is not null) installDirectory = Path.GetFullPath(installDirectory);
        return new StudioRequest(action, target, SetupOptions.NormalizeVersion(version),
            releaseDirectory, installDirectory, port, answerFileIndex >= 0, purge, dryRun);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Agent Studio setup: [--mode studio] [--target docker|native] [--release-version X.Y.Z] [--release-dir PATH] [--install-dir PATH] [--port 4011] [--unattended answers.json]\nActions: update, rollback, uninstall [--purge]. Docker is the default.");
            return 0;
        }
        var request = Parse(args);
        if (args.Length == 0 && !Console.IsInputRedirected)
        {
            Console.WriteLine("Agent Studio setup");
            Console.WriteLine("Docker is the recommended installation path.");
            var prompt = new ConsolePrompter(false);
            var target = prompt.Ask("Runtime (docker or native)", "docker").ToLowerInvariant();
            if (target is not ("docker" or "native"))
                throw new ArgumentException("Runtime must be docker or native.");
            var portText = prompt.Ask("Studio browser port", "4011");
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
                throw new ArgumentException("Studio browser port must be between 1 and 65535.");
            request = request with { Target = target, Port = port };
        }
        if (request.Target == "native")
            throw new PlatformNotSupportedException("The full native Studio service profile is not packaged yet. Use the Docker target, or the documented Windows fallback profile for Task Server and Engine.");
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Studio setup supports Windows and Linux x64.");
        var processes = new ProcessRunner(request.DryRun);
        var inspection = new ProcessRunner(false);
        if (await inspection.FindCommandAsync("docker") is null)
            throw new InvalidOperationException(OperatingSystem.IsWindows()
                ? "Docker Desktop is required. Install it with the WSL2 backend: https://docs.docker.com/desktop/setup/install/windows-install/"
                : "Docker Engine and Compose are required: https://docs.docker.com/engine/install/");
        var compose = await inspection.RunAsync("docker", ["compose", "version"], printOutput: false);
        if (compose.ExitCode != 0)
            throw new InvalidOperationException("Docker Compose v2 is required. Install the Compose plugin or update Docker Desktop.");
        var daemon = await inspection.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], printOutput: false);
        if (daemon.ExitCode != 0)
            throw new InvalidOperationException("Docker is installed but its daemon is unavailable. Start Docker Desktop or the Docker Engine service and retry.");
        if (OperatingSystem.IsWindows())
        {
            if (await inspection.FindCommandAsync("wsl.exe") is null)
                throw new InvalidOperationException("WSL2 is required for the Docker Desktop path. Install WSL2: https://docs.docker.com/desktop/features/wsl/");
            var wsl = await inspection.RunAsync("wsl.exe", ["--status"], printOutput: false);
            if (wsl.ExitCode != 0)
                throw new InvalidOperationException("WSL2 is required for the Docker Desktop path. Install WSL2 and enable Docker Desktop's WSL2 backend: https://docs.docker.com/desktop/features/wsl/");
            Console.WriteLine("Docker Desktop requires a paid subscription for larger companies. See https://docs.docker.com/desktop/setup/install/windows-install/.");
        }

        var root = request.InstallDirectory ?? DefaultInstallDirectory();
        var installer = new StudioDockerInstaller(root, processes, request.DryRun);
        switch (request.Action)
        {
            case "uninstall": await installer.UninstallAsync(request.Purge); break;
            case "rollback": await installer.RollbackAsync(); break;
            case "update":
                await installer.UpdateAsync(request.Version ?? await LatestVersionAsync(), request.ReleaseDirectory);
                break;
            default:
                var version = request.Version
                              ?? (request.ReleaseDirectory is not null
                                  ? ReleaseArtifacts.CurrentVersion()
                                  : await LatestVersionAsync());
                await installer.InstallAsync(version, request.ReleaseDirectory, request.Port);
                if (!request.Unattended && !request.DryRun)
                    OpenBrowser(request.Port);
                break;
        }
        return 0;
    }

    private static string DefaultInstallDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "agent-studio");
    }

    private static async Task<string> LatestVersionAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-studio-setup/1");
        using var response = await client.GetAsync("https://api.github.com/repos/agent-orc/agent-studio/releases/latest");
        response.EnsureSuccessStatusCode();
        using var release = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var tag = release?.RootElement.GetProperty("tag_name").GetString();
        return SetupOptions.NormalizeVersion(tag)
               ?? throw new InvalidDataException("The latest release has no semantic version tag.");
    }

    private static void OpenBrowser(int port)
    {
        try { Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true }); }
        catch { Console.WriteLine($"Open http://localhost:{port} in your browser."); }
    }
}
