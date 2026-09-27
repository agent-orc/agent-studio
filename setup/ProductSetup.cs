using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentStudio.Setup;

// The published one-box stack is installed from a checksum-verified release bundle.
// State is deliberately small; Compose owns the data volumes and credentials.
internal static class ProductSetup
{
    private sealed record Answers(
        string? Mode = null,
        string? Target = null,
        string? ReleaseVersion = null,
        string? ReleaseDirectory = null,
        string? InstallDirectory = null,
        int? UiPort = null,
        string? ServerUrl = null,
        string? JoinTokenFile = null);

    private sealed record InstalledState(string Mode, string Target, string Version,
        string? PreviousVersion, int UiPort);

    internal static bool IsProductCommand(string[] args)
    {
        if (args.Length == 0 || args[0] is "update" or "rollback" or "uninstall"
            || args.Contains("--unattended") || args.Contains("--answer-file")
            || args.Contains("--uninstall") || args.Contains("--purge")
            || args.Contains("--offline") || args.Contains("--help")
            || args.Contains("-h") || args.Contains("--version"))
            return true;
        if (args.Contains("--join") || args.Contains("--join-token-file")) return false;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--mode" && args[index + 1] is
                ("demo" or "single" or "single-machine" or "control-plane" or "agent-host"))
                return false;
        }
        return true;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await ExecuteAsync(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Setup cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Setup failed: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string[] args)
    {
        var command = args.FirstOrDefault() is "update" or "rollback" or "uninstall"
            ? args[0] : "install";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = command == "install" ? 0 : 1; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--unattended" or "--purge" or "--dry-run" or "--uninstall"
                or "--offline"
                or "--help" or "-h" or "--version")
            {
                flags.Add(option);
                continue;
            }
            if (option is not ("--mode" or "--target" or "--answer-file" or
                "--release-version" or "--release-dir" or "--install-dir" or
                "--ui-port" or "--server-url" or "--join-token-file"))
                throw new ArgumentException($"Unknown option: {option}");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            values[option] = args[index];
        }
        if (flags.Contains("--help") || flags.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }
        if (flags.Contains("--version"))
        {
            Console.WriteLine($"agent-studio-setup {ReleaseArtifacts.CurrentVersion()}");
            return 0;
        }
        if (flags.Contains("--uninstall")) command = "uninstall";
        if (flags.Contains("--purge") && command != "uninstall")
            throw new ArgumentException("--purge requires uninstall or --uninstall.");
        var answers = values.TryGetValue("--answer-file", out var answerFile)
            ? JsonSerializer.Deserialize<Answers>(await File.ReadAllTextAsync(answerFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? throw new InvalidDataException("Answer file is empty.")
            : new Answers();
        var unattended = flags.Contains("--unattended");
        var prompter = new ConsolePrompter(unattended);
        var mode = Get("--mode", answers.Mode)
            ?? (Get("--join-token-file", answers.JoinTokenFile) is null ? "studio" : "agent-host");
        var target = Get("--target", answers.Target)
            ?? (mode == "agent-host" ? "native" : "docker");
        if (mode != "studio")
        {
            if (mode is not ("control-plane" or "agent-host"))
                throw new PlatformNotSupportedException(
                    $"Mode '{mode}' is not available in this release.");
            if (command != "install")
                throw new ArgumentException($"{command} is supported for the Studio Docker installation only.");
            if (target == "docker" && mode == "agent-host")
                throw new PlatformNotSupportedException(
                    "Docker agent-host setup is not available in this release. Use --target native on Linux.");
            var legacy = new List<string> { "--mode", mode };
            if (mode == "control-plane")
                legacy.AddRange(["--target", target == "native" ? "systemd" : "docker"]);
            foreach (var (newOption, oldOption, answer) in new[]
            {
                ("--release-version", "--release-version", answers.ReleaseVersion),
                ("--release-dir", "--release-dir", answers.ReleaseDirectory),
                ("--server-url", "--server-url", answers.ServerUrl),
                ("--join-token-file", "--join-token-file", answers.JoinTokenFile),
            })
            {
                if (Get(newOption, answer) is { } value)
                    legacy.AddRange([oldOption, value]);
            }
            if (unattended) legacy.Add("--non-interactive");
            if (flags.Contains("--dry-run")) legacy.Add("--dry-run");
            return await SetupApplication.RunAsync(legacy.ToArray());
        }
        if (target is not ("docker" or "native"))
            throw new ArgumentException("--target must be docker or native.");
        if (target == "native")
            throw new PlatformNotSupportedException(
                "The native full Studio profile is not available in this release. " +
                "Use the Docker target; the existing Linux remote setup and Windows fallback tools remain available.");
        var root = Path.GetFullPath(Get("--install-dir", answers.InstallDirectory)
            ?? DefaultInstallRoot());
        var statePath = Path.Combine(root, "install-state.json");
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<InstalledState>(await File.ReadAllTextAsync(statePath))
            : null;
        if (command == "install" && state is not null)
            throw new InvalidOperationException(
                $"Studio is already installed at {root}. Use update or uninstall.");
        if (command != "install" && state is null)
            throw new InvalidOperationException($"No Studio installation found at {root}.");
        var process = new ProcessRunner(flags.Contains("--dry-run"));
        await CheckDockerAsync();

        if (command == "uninstall")
        {
            await ComposeAsync(process, root, state!.Version,
                flags.Contains("--purge") ? ["down", "--volumes", "--remove-orphans"]
                                          : ["down", "--remove-orphans"]);
            if (!flags.Contains("--dry-run"))
            {
                File.Delete(statePath);
                if (flags.Contains("--purge"))
                {
                    File.Delete(Path.Combine(root, ".env"));
                    var releases = Path.Combine(root, "releases");
                    if (Directory.Exists(releases)) Directory.Delete(releases, recursive: true);
                    if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
                }
            }
            Console.WriteLine(flags.Contains("--purge")
                ? "Studio and its data volumes were removed."
                : $"Studio was stopped. Data volumes and installation files remain at {root}.");
            return 0;
        }

        var version = command == "rollback"
            ? state!.PreviousVersion ?? throw new InvalidOperationException("No previous release is available.")
            : SetupOptions.NormalizeVersion(Get("--release-version", answers.ReleaseVersion))
              ?? ReleaseArtifacts.CurrentVersion();
        var portText = Get("--ui-port", answers.UiPort?.ToString());
        var port = portText is null
            ? state?.UiPort ?? int.Parse(prompter.Ask("Studio browser port", "4011"))
            : int.Parse(portText);
        if (port is < 1 or > 65535)
            throw new ArgumentException("UI port must be between 1 and 65535.");
        if (command == "update" && version == state!.Version)
            throw new InvalidOperationException("This version is already installed.");
        var bundle = Path.Combine(root, "releases", version);
        var offlineDirectory = Get("--release-dir", answers.ReleaseDirectory);
        if (flags.Contains("--offline") && offlineDirectory is null && command != "rollback")
            throw new ArgumentException("--offline requires --release-dir with a verified Compose bundle.");
        if (command == "rollback" && !File.Exists(Path.Combine(bundle, "docker-compose.yml")))
            throw new InvalidOperationException($"Previous Compose bundle is missing: {bundle}");

        if (command != "rollback" && !flags.Contains("--dry-run"))
        {
            await using var artifacts = new ReleaseArtifacts(version,
                offlineDirectory);
            var source = await artifacts.ExtractComposeAsync(default);
            CopyDirectory(source, bundle);
        }
        if (flags.Contains("--dry-run"))
            Console.WriteLine($"[dry-run] install verified Compose bundle v{version} at {bundle}");
        var envPath = Path.Combine(root, ".env");
        var previousEnv = File.Exists(envPath) ? await File.ReadAllTextAsync(envPath) : null;
        try
        {
            if (!flags.Contains("--dry-run"))
            {
                Directory.CreateDirectory(root);
                await WritePrivateFileAsync(envPath,
                    $"AGENT_STUDIO_VERSION={version}\nSTUDIO_UI_PORT={port}\n");
            }
            if (!flags.Contains("--offline") && command != "rollback")
                await ComposeAsync(process, root, version, ["pull"]);
            await ComposeAsync(process, root, version,
                !flags.Contains("--offline") && command != "rollback"
                    ? ["up", "-d", "--wait"]
                    : ["up", "-d", "--wait", "--pull", "never"]);
            if (!flags.Contains("--dry-run"))
            {
                var url = $"http://127.0.0.1:{port}/healthz";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await http.GetAsync(url);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"Studio health check returned {(int)response.StatusCode} at {url}.");
                var next = new InstalledState("studio", "docker", version,
                    command == "rollback" ? state!.Version : state?.Version, port);
                await WritePrivateFileAsync(statePath, JsonSerializer.Serialize(next));
            }
        }
        catch
        {
            if (!flags.Contains("--dry-run"))
            {
                if (previousEnv is null) File.Delete(envPath);
                else await WritePrivateFileAsync(envPath, previousEnv);
                if (state is not null)
                {
                    try { await ComposeAsync(process, root, state.Version, ["up", "-d", "--wait"]); }
                    catch (Exception recoveryError)
                    {
                        Console.Error.WriteLine($"Previous release recovery failed: {recoveryError.Message}");
                    }
                }
            }
            throw;
        }
        var browserUrl = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"Studio: {browserUrl}");
        Console.WriteLine("One runner container is started with the stack.");
        Console.WriteLine("Add a project and configure runner CLI credentials before running tasks.");
        if (command == "install" && !unattended && !flags.Contains("--dry-run") &&
            prompter.Confirm("Open Studio in your browser", true))
        {
            try { Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true }); }
            catch (Exception error)
            {
                Console.WriteLine($"Could not open the browser: {error.Message}");
                Console.WriteLine($"Open {browserUrl} manually.");
            }
        }
        return 0;

        string? Get(string option, string? answer)
            => values.TryGetValue(option, out var value) ? value : answer;
    }

    private static async Task CheckDockerAsync()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("This installer supports Windows and Linux x64.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("This installer requires an x64 host.");
        var check = new ProcessRunner(false);
        try
        {
            var result = await check.RunAsync("docker", ["compose", "version"], printOutput: false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("Docker Compose v2 is required.");
            result = await check.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], printOutput: false);
            if (result.ExitCode == 0) return;
        }
        catch (System.ComponentModel.Win32Exception) { }
        throw new InvalidOperationException(OperatingSystem.IsWindows()
            ? "Docker Desktop with the WSL2 backend is required and must be running. See https://docs.docker.com/desktop/setup/install/windows-install/."
            : "Docker Engine and Compose v2 must be installed and running. See https://docs.docker.com/engine/install/.");
    }

    private static async Task ComposeAsync(ProcessRunner process, string root, string version,
        IEnumerable<string> operation)
    {
        var args = new List<string> { "compose", "--project-name", "agent-studio",
            "--env-file", Path.Combine(root, ".env"), "-f",
            Path.Combine(root, "releases", version, "docker-compose.yml") };
        args.AddRange(operation);
        await process.RequireAsync("docker", args);
    }

    private static string DefaultInstallRoot()
        => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "agent-studio");

    private static async Task WritePrivateFileAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            agent-studio-setup - Agent Studio installer

            Usage:
              agent-studio-setup [--mode studio] [--target docker]
              agent-studio-setup --unattended --answer-file answers.json
              agent-studio-setup update [--release-version X.Y.Z]
              agent-studio-setup rollback
              agent-studio-setup uninstall [--purge]

            Options:
              --release-version X.Y.Z   Pinned release (defaults to this verified setup release)
              --release-dir PATH        Offline release directory with SHA256SUMS and Compose archive
              --offline                 Use locally loaded images without a registry pull
              --install-dir PATH        Installation state and versioned Compose bundles
              --ui-port NUMBER         Loopback browser port (default 4011)
              --answer-file PATH       JSON answers for unattended installation
              --unattended            Use defaults without prompts
              --dry-run               Check prerequisites and print Compose operations

            The Docker path runs without elevation. Docker Desktop on Windows and
            macOS may require a paid subscription for larger companies; Docker
            Engine on Linux does not. The native remote profiles remain available
            through the legacy --mode control-plane and --mode agent-host commands.
            """);
    }
}
