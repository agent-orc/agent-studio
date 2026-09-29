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
        string? JoinTokenFile = null,
        string? TokenFile = null);

    internal sealed record InstalledState(string Mode, string Target, string Version,
        string? PreviousVersion, int UiPort, string? ServerUrl = null);

    /// <summary>Everything except the legacy-only demo and single modes is a product command.</summary>
    internal static bool IsProductCommand(string[] args) => !ProductCommand.IsLegacyOnly(args);

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
        var parsed = ProductCommand.Parse(args);
        var command = parsed.Verb;
        var values = parsed.Values;
        var flags = parsed.Flags;
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
        var answers = values.TryGetValue("--answer-file", out var answerFile)
            ? JsonSerializer.Deserialize<Answers>(await File.ReadAllTextAsync(answerFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? throw new InvalidDataException("Answer file is empty.")
            : new Answers();
        var unattended = parsed.Unattended;
        var prompter = new ConsolePrompter(unattended);

        // update, rollback, and uninstall act on the recorded installation
        // unless the operator names a profile explicitly.
        var explicitMode = Get("--mode", answers.Mode);
        var explicitTarget = Get("--target", answers.Target);
        var installDirectory = Get("--install-dir", answers.InstallDirectory);
        var located = command != "install" && explicitMode is null && explicitTarget is null
            ? await LocateInstallationAsync(installDirectory)
            : null;
        var mode = located?.State.Mode ?? ProductCommand.NormalizeMode(explicitMode,
            Get("--join-token-file", answers.JoinTokenFile) is not null);
        var target = located?.State.Target ?? ProductCommand.NormalizeTarget(explicitTarget, mode);
        var forwarded = new List<(string, string)>();
        foreach (var (option, answer) in new[]
        {
            ("--release-version", answers.ReleaseVersion),
            ("--release-dir", answers.ReleaseDirectory),
            ("--server-url", answers.ServerUrl),
            ("--join-token-file", answers.JoinTokenFile),
        })
        {
            if (Get(option, answer) is { } value) forwarded.Add((option, value));
        }
        var plan = ProductPlanner.Plan(parsed, mode, target, OperatingSystem.IsWindows(), forwarded);
        if (plan.Profile == ProductProfile.Delegated)
            return await SetupApplication.RunAsync(plan.DelegatedArguments.ToArray());

        var root = located?.Root ?? Path.GetFullPath(installDirectory ?? DefaultInstallRoot(plan.Profile));
        var statePath = Path.Combine(root, "install-state.json");
        var state = located?.State ?? await ReadStateAsync(statePath);
        if (command == "install" && state is not null)
            throw new InvalidOperationException(
                $"Agent Studio ({state.Mode}, {state.Target}) is already installed at {root}. Use update or uninstall.");
        if (command != "install" && state is null)
            throw new InvalidOperationException($"No Agent Studio installation found at {root}.");
        if (state is not null && (state.Mode != plan.Mode || state.Target != plan.Target))
            throw new InvalidOperationException(
                $"The installation at {root} is --mode {state.Mode} --target {state.Target}.");
        var process = new ProcessRunner(flags.Contains("--dry-run"));
        if (plan.Profile != ProductProfile.StudioDocker)
            return await RunWindowsServicesAsync(parsed, plan, root, statePath, state, process, prompter,
                Get("--release-version", answers.ReleaseVersion),
                Get("--release-dir", answers.ReleaseDirectory),
                Get("--server-url", answers.ServerUrl),
                Get("--token-file", answers.TokenFile));
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
            try
            {
                // Shell execution ignores CreateNoWindow; it is set for the repository-wide guard.
                Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true, CreateNoWindow = true });
            }
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

    private static async Task<int> RunWindowsServicesAsync(ProductCommand parsed, ProductPlan plan,
        string root, string statePath, InstalledState? state, ProcessRunner process, ConsolePrompter prompter,
        string? releaseVersion, string? releaseDirectory, string? serverUrl, string? tokenFile)
    {
        var command = parsed.Verb;
        var dryRun = parsed.DryRun;
        if (!dryRun) WindowsServiceSetup.RequireAdministrator();
        var layout = WindowsServiceLayout.ForHost(root);
        var services = new WindowsServiceSetup(process, layout, dryRun);
        var profile = plan.Profile;

        if (command == "uninstall")
        {
            var purge = parsed.Has("--purge");
            await services.UninstallAsync(profile, purge);
            if (!dryRun)
            {
                File.Delete(statePath);
                if (purge && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                    Directory.Delete(root);
            }
            Console.WriteLine(purge
                ? "The services, configuration, credentials, and task data were removed."
                : $"The services were removed. Configuration remains in {layout.ConfigRoot}" +
                  (profile == ProductProfile.StudioWindowsServices ? $" and task data in {layout.DataDirectory}." : "."));
            return 0;
        }

        string? upstream = null;
        if (profile == ProductProfile.ConnectorWindows && command == "install")
        {
            upstream = serverUrl ?? prompter.Ask("Remote Task Server URL (https://...)", null);
            ValidateUpstream(upstream);
            tokenFile ??= prompter.Ask("File containing the Studio token for the remote Task Server", null);
            tokenFile = Path.GetFullPath(tokenFile);
            if (!dryRun && !File.Exists(tokenFile))
                throw new FileNotFoundException($"Token file not found: {tokenFile}", tokenFile);
        }
        else if (serverUrl is not null || tokenFile is not null)
        {
            throw new ArgumentException("--server-url and --token-file apply to a connector installation only.");
        }

        var version = command == "rollback"
            ? state!.PreviousVersion ?? throw new InvalidOperationException("No previous release is available.")
            : SetupOptions.NormalizeVersion(releaseVersion) ?? ReleaseArtifacts.CurrentVersion();
        if (command == "update" && version == state!.Version)
            throw new InvalidOperationException("This version is already installed.");
        if (parsed.Has("--offline") && releaseDirectory is null && command != "rollback")
            throw new ArgumentException("--offline requires --release-dir with the verified Windows package.");

        if (command == "rollback")
        {
            // The D6 installer keeps every staged release; the previous one is
            // re-activated from its own scripts without a download.
            var previous = layout.ReleaseDirectory(version);
            if (!dryRun && !Directory.Exists(previous))
                throw new InvalidOperationException($"Previous release is missing: {previous}");
            await services.ApplyAsync(profile, previous, null, null);
        }
        else
        {
            await using var artifacts = new ReleaseArtifacts(version, releaseDirectory);
            var package = dryRun
                ? Path.Combine(Path.GetTempPath(), $"agent-orchestrator-{version}-win-x64")
                : await artifacts.ExtractWindowsPackageAsync(default);
            try
            {
                await services.ApplyAsync(profile, package, upstream, tokenFile);
            }
            catch when (state is not null && !dryRun)
            {
                try { await services.ApplyAsync(profile, layout.ReleaseDirectory(state.Version), null, null); }
                catch (Exception recoveryError)
                {
                    Console.Error.WriteLine($"Previous release recovery failed: {recoveryError.Message}");
                }
                throw;
            }
        }

        if (!dryRun)
        {
            Directory.CreateDirectory(root);
            await WritePrivateFileAsync(statePath, JsonSerializer.Serialize(new InstalledState(
                plan.Mode, plan.Target, version,
                command == "install" ? null : state!.Version, 0,
                upstream ?? state?.ServerUrl)));
        }
        services.PrintSummary(profile, upstream ?? state?.ServerUrl);
        return 0;
    }

    internal static void ValidateUpstream(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException(
                "--server-url must be an https URL, or http on a loopback address.");
    }

    private static async Task<InstalledState?> ReadStateAsync(string statePath)
        => File.Exists(statePath)
            ? JsonSerializer.Deserialize<InstalledState>(await File.ReadAllTextAsync(statePath))
            : null;

    private static async Task<(string Root, InstalledState State)?> LocateInstallationAsync(string? installDirectory)
    {
        var candidates = installDirectory is not null
            ? [Path.GetFullPath(installDirectory)]
            : new[] { ProductProfile.StudioDocker, ProductProfile.StudioWindowsServices }
                .Select(DefaultInstallRoot).Distinct().ToArray();
        foreach (var candidate in candidates)
        {
            if (await ReadStateAsync(Path.Combine(candidate, "install-state.json")) is { } state)
                return (candidate, state);
        }
        throw new InvalidOperationException(
            $"No Agent Studio installation found at {string.Join(" or ", candidates)}.");
    }

    // The Docker path runs as the user; the Windows services are machine-wide.
    private static string DefaultInstallRoot(ProductProfile profile)
        => !OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "agent-studio")
            : profile == ProductProfile.StudioDocker
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AgentStudio");

    private static async Task WritePrivateFileAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static void CopyDirectory(string source, string destination)
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
              agent-studio-setup [--mode studio] [--target docker|native]
              agent-studio-setup --mode connector --server-url URL --token-file PATH
              agent-studio-setup --mode control-plane [--target docker|native] --server-url URL
              agent-studio-setup --mode agent-host --join-token-file PATH
              agent-studio-setup --unattended --answer-file answers.json
              agent-studio-setup update [--release-version X.Y.Z]
              agent-studio-setup rollback
              agent-studio-setup uninstall [--purge]

            Modes:
              studio          Full product on this machine (default). Docker by default;
                              native installs Windows services (administrator) or, on
                              Linux, the systemd single-machine profile (root).
              connector       Windows: local Studio connector for a remote Task Server.
              control-plane   Linux: remote Task Server and Engine (Docker by default).
              agent-host      Linux: runner that joins a Task Server with a join token.

            Options:
              --release-version X.Y.Z   Pinned release (defaults to this verified setup release)
              --release-dir PATH        Offline release directory with SHA256SUMS and archives
              --offline                 Use locally loaded images without a registry pull
              --install-dir PATH        Installation state (and Compose bundles for Docker)
              --ui-port NUMBER          Loopback browser port for Docker (default 4011)
              --server-url URL          Task Server URL (connector, control-plane)
              --token-file PATH         Studio token file for the remote Task Server (connector)
              --answer-file PATH        JSON answers for unattended installation
              --unattended              Use defaults without prompts
              --dry-run                 Check prerequisites and print planned operations

            The Docker path runs without elevation. Docker Desktop on Windows and
            macOS may require a paid subscription for larger companies; Docker
            Engine on Linux does not. Use --target native on hosts without Docker.
            Uninstall keeps data unless --purge is given.
            """);
    }
}
