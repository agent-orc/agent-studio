using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Setup;

internal static class SetupApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = SetupOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }
            if (options.ShowVersion)
            {
                Console.WriteLine(
                    $"agent-orchestrator-setup {AssemblyVersion()}");
                return 0;
            }

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            await RunSetupAsync(options, shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Setup cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Setup failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunSetupAsync(
        SetupOptions original,
        CancellationToken cancellationToken)
    {
        PrintHeader();
        var prompter = new ConsolePrompter(original.NonInteractive);
        var mode = original.Mode == SetupMode.Guided
            ? AskMode(prompter)
            : original.Mode;
        var options = original with { Mode = mode };
        var processes = new ProcessRunner(options.DryRun);
        var version = options.ReleaseVersion ?? ReleaseArtifacts.CurrentVersion();

        if (mode == SetupMode.Demo)
        {
            await CheckPlatformAsync(
                processes,
                native: false,
                controlPlane: false,
                host: false);
            await new DemoInstaller(processes, options.DryRun).InstallAsync(
                version,
                options.DemoPort,
                cancellationToken);
            return;
        }

        await CheckRootAsync(processes);
        await CheckPlatformAsync(
            processes,
            native: true,
            controlPlane: mode is SetupMode.SingleMachine or SetupMode.ControlPlane,
            host: mode is SetupMode.SingleMachine or SetupMode.AgentHost);

        if (mode == SetupMode.AgentHost)
        {
            var payload = ReadJoinPayload(options, prompter);
            if (options.ReleaseVersion is not null
                && !string.Equals(
                    options.ReleaseVersion,
                    payload.ReleaseVersion,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Join token requires release {payload.ReleaseVersion}, but --release-version selected {options.ReleaseVersion}.");
            await using var hostArtifacts = new ReleaseArtifacts(
                payload.ReleaseVersion,
                options.ReleaseDirectory);
            var hostRelease = await hostArtifacts.ExtractHostAsync(cancellationToken);
            await ConfigureAndInstallHostAsync(
                options,
                prompter,
                processes,
                hostRelease,
                payload,
                cancellationToken);
            PrintHostFinish(payload.ServerUrl);
            return;
        }

        if (mode == SetupMode.ControlPlane && options.Target == SetupTarget.Docker)
        {
            var controlPlaneDomain = options.ServerUrl
                                     ?? prompter.Ask(
                                         "Private control-plane DNS name (WireGuard-only)",
                                         "task-server-01.wg.internal");
            var wgAddress = options.WireGuardAddress
                            ?? prompter.Ask("task-server-01 WireGuard address", "10.60.0.1");
            var offhostBackupPath = options.OffhostBackupPath
                                    ?? prompter.Ask(
                                        "Off-host backup mount path (already mounted)",
                                        "/mnt/agent-orchestrator-offhost-backup");
            var runnerId = options.RunnerName ?? "agent-runner-01";

            await using var dockerArtifacts = new ReleaseArtifacts(version, options.ReleaseDirectory);
            var dockerOrchestratorRelease = await dockerArtifacts.ExtractOrchestratorAsync(cancellationToken);
            var docker = new DockerInstaller(InstallPaths.Load(), processes, options.DryRun);
            var dockerControl = await docker.InstallControlPlaneAsync(
                dockerOrchestratorRelease,
                wgAddress,
                controlPlaneDomain,
                offhostBackupPath,
                runnerId,
                version,
                cancellationToken);

            Console.WriteLine();
            Console.WriteLine("Docker control-plane setup is complete.");
            Console.WriteLine($"Task Server: {dockerControl.ServerUrl}");
            Console.WriteLine($"Runner id: {runnerId}");
            Console.WriteLine($"  RUNNER_SERVER_URL={dockerControl.ServerUrl}");
            Console.WriteLine(
                "  The runner credential is at /etc/agent-orchestrator/secrets/runner.token on task-server-01; copy it to the Runner over an already-trusted channel, never through chat or task text.");
            Console.WriteLine(
                "  WireGuard, the host firewall, and DNS are separate steps; see docs/operations/setup/control-plane-docker.md.");
            return;
        }

        var listenUrl = options.ListenUrl
                        ?? prompter.Ask(
                            "Private Task Server listen URL",
                            "http://127.0.0.1:5071");
        _ = SetupValidation.RequireServerUrl(listenUrl, allowLoopbackHttp: true);
        var hostVisibleDefault = mode == SetupMode.SingleMachine
            ? listenUrl
            : options.NonInteractive
                ? null
                : "https://tasks.example.com";
        var hostVisibleServerUrl = options.ServerUrl
                                   ?? prompter.Ask(
                                       "Task Server URL visible from agent hosts",
                                       hostVisibleDefault);
        _ = SetupValidation.RequireServerUrl(
            hostVisibleServerUrl,
            allowLoopbackHttp:
                mode == SetupMode.SingleMachine
                || Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1");

        await using var artifacts = new ReleaseArtifacts(version, options.ReleaseDirectory);
        var orchestratorRelease = await artifacts.ExtractOrchestratorAsync(cancellationToken);
        var studioRelease = await artifacts.ExtractStudioAsync(cancellationToken);
        var native = new NativeInstaller(InstallPaths.Load(), processes, options.DryRun);
        var control = await native.InstallControlPlaneAsync(
            orchestratorRelease,
            studioRelease,
            listenUrl,
            hostVisibleServerUrl,
            version,
            cancellationToken);
        var joinPayload = new JoinPayload(
            1,
            control.ServerUrl,
            control.Credential,
            control.ReleaseVersion,
            DateTime.UtcNow);

        if (mode == SetupMode.SingleMachine)
        {
            var hostRelease = await artifacts.ExtractHostAsync(cancellationToken);
            await ConfigureAndInstallHostAsync(
                options,
                prompter,
                processes,
                hostRelease,
                joinPayload,
                cancellationToken);
            Console.WriteLine();
            Console.WriteLine("Single-machine setup is complete.");
            Console.WriteLine($"Task Server: {control.ServerUrl}");
            Console.WriteLine(
                "Serve /opt/agent-studio/current/browser with the supplied Caddy template to open the native Studio UI.");
        }
        else
        {
            PrintJoinInstructions(joinPayload);
        }
    }

    private static async Task ConfigureAndInstallHostAsync(
        SetupOptions options,
        ConsolePrompter prompter,
        ProcessRunner processes,
        string hostRelease,
        JoinPayload payload,
        CancellationToken cancellationToken)
    {
        var executionUser = options.ExecutionUser
                            ?? Environment.GetEnvironmentVariable("SUDO_USER");
        if (string.IsNullOrWhiteSpace(executionUser) || executionUser == "root")
        {
            executionUser = prompter.Ask(
                "Linux user that already owns the Agent CLI login");
        }
        executionUser = SetupValidation.RequireSimpleName(
            executionUser,
            "Execution user");
        var (executionGroup, homeDirectory) = await ResolveUserAsync(
            processes,
            executionUser,
            cancellationToken);

        var installedClis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { "codex", "claude" })
        {
            if (await processes.FindCommandAsync(
                    candidate,
                    executionUser,
                    homeDirectory) is { Length: > 0 } path)
                installedClis[candidate] = path;
        }
        if (installedClis.Count == 0)
            throw new InvalidOperationException(
                $"No supported host CLI was found for {executionUser}. Install and authenticate Codex or Claude on this host, then rerun setup.");
        var agentCli = options.AgentCli
                       ?? (installedClis.Count == 1
                           ? installedClis.Keys.Single()
                           : prompter.Ask(
                               $"Agent CLI ({string.Join(" or ", installedClis.Keys.Order())})",
                               installedClis.ContainsKey("codex") ? "codex" : "claude"));
        if (!installedClis.TryGetValue(agentCli, out var cliPath))
            throw new InvalidOperationException(
                $"Agent CLI '{agentCli}' is not installed for {executionUser}. Found: {string.Join(", ", installedClis.Keys)}.");
        await VerifyCliAsync(
            processes,
            executionUser,
            homeDirectory,
            agentCli,
            cliPath,
            cancellationToken);

        var runnerName = SetupValidation.RequireSimpleName(
            options.RunnerName
            ?? prompter.Ask("Agent Host name", Environment.MachineName.ToLowerInvariant()),
            "Agent Host name");
        var runnerId = SetupValidation.RequireSimpleName(
            options.Role == "coding" ? runnerName : $"{runnerName}-review",
            "Runner id");
        var runnerCredential = options.DryRun
            ? new string('0', 64)
            : await MintRunnerCredentialAsync(payload, runnerId, cancellationToken);
        var gitRemote = string.IsNullOrWhiteSpace(options.GitRemote)
            ? prompter.Ask(
                "Git repository used for the host read/write probe (leave empty to register read-only)",
                defaultValue: string.Empty,
                required: false)
            : options.GitRemote;
        gitRemote = string.IsNullOrWhiteSpace(gitRemote)
            ? null
            : SetupValidation.RequireGitRemote(gitRemote);
        var gitPushRemote = string.IsNullOrWhiteSpace(options.GitPushRemote)
            ? gitRemote
            : SetupValidation.RequireGitRemote(options.GitPushRemote);
        var configuration = new HostConfiguration(
            payload.ServerUrl,
            runnerCredential,
            payload.ReleaseVersion,
            runnerId,
            runnerName,
            options.Role,
            executionUser,
            executionGroup,
            homeDirectory,
            cliPath,
            installedClis.GetValueOrDefault("claude"),
            installedClis.GetValueOrDefault("codex"),
            agentCli.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? "exec --skip-git-repo-check --sandbox danger-full-access -"
                : "-p",
            gitRemote,
            gitPushRemote,
            options.MaxParallelism);
        var native = new NativeInstaller(InstallPaths.Load(), processes, options.DryRun);

        if (gitRemote is not null)
        {
            PrintGitHubTokenGuide(gitRemote);
            var storeCredential = Uri.TryCreate(gitRemote, UriKind.Absolute, out var gitUri)
                                  && gitUri.Scheme == Uri.UriSchemeHttps
                                  && string.Equals(
                                      gitUri.Host,
                                      "github.com",
                                      StringComparison.OrdinalIgnoreCase)
                                  && prompter.Confirm(
                                      $"Store and verify a GitHub PAT for {executionUser}",
                                      defaultValue: false);
            if (storeCredential)
            {
                var username = prompter.Ask("GitHub username");
                var token = prompter.Secret("GitHub token");
                try
                {
                    await native.ConfigureGitHubCredentialAsync(
                        configuration,
                        username,
                        token,
                        cancellationToken);
                }
                finally
                {
                    token = string.Empty;
                }
            }
            else
            {
                await native.VerifyGitReadAsync(configuration, cancellationToken);
                Console.WriteLine(
                    "  Setup will let the daemon report the actual push capability. A read-only result registers the host but blocks new claims.");
            }
        }
        else
        {
            Console.WriteLine(
                "  No Git probe repository was selected. The host will register, but coding claims remain disabled until Git write access is configured.");
        }

        await native.InstallAgentHostAsync(
            hostRelease,
            configuration,
            cancellationToken);
    }

    internal static async Task<string> MintRunnerCredentialAsync(
        JoinPayload payload,
        string runnerId,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        var ownsClient = client is null;
        client ??= new HttpClient
        {
            BaseAddress = new Uri(payload.ServerUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        try
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", payload.Credential);
            if (!client.DefaultRequestHeaders.Contains(TaskServerProtocol.HeaderName))
                client.DefaultRequestHeaders.Add(
                    TaskServerProtocol.HeaderName,
                    TaskServerProtocol.Current.ToString());
            if (!client.DefaultRequestHeaders.Contains("X-Client-Id"))
                client.DefaultRequestHeaders.Add("X-Client-Id", "agent-orchestrator-setup");
            var response = await client.PostAsJsonAsync(
                "/api/v1/management/principals",
                new CreatePrincipalRequest(
                    $"runner:{runnerId}",
                    TaskServerPrincipalKinds.Runner,
                    RunnerId: runnerId),
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                response.Dispose();
                response = await client.PostAsJsonAsync(
                    $"/api/v1/management/principals/{Uri.EscapeDataString($"runner:{runnerId}")}/rotate",
                    new RotatePrincipalRequest(),
                    cancellationToken);
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Task Server could not mint the credential for Runner '{runnerId}' ({(int)response.StatusCode}): {body}");
            var issued = await response.Content.ReadFromJsonAsync<IssuedPrincipalCredential>(
                             cancellationToken: cancellationToken)
                         ?? throw new InvalidOperationException(
                             "Task Server returned an empty Runner credential response.");
            response.Dispose();
            return issued.Credential;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    private static JoinPayload ReadJoinPayload(
        SetupOptions options,
        ConsolePrompter prompter)
    {
        string token;
        if (options.JoinTokenFile is not null)
        {
            if (!File.Exists(options.JoinTokenFile))
                throw new FileNotFoundException(
                    "Join token file does not exist.",
                    options.JoinTokenFile);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(options.JoinTokenFile);
                if ((mode & (UnixFileMode.GroupRead
                             | UnixFileMode.GroupWrite
                             | UnixFileMode.GroupExecute
                             | UnixFileMode.OtherRead
                             | UnixFileMode.OtherWrite
                             | UnixFileMode.OtherExecute)) != 0)
                    throw new InvalidOperationException(
                        "Join token file must be readable only by its owner.");
            }
            token = File.ReadAllText(options.JoinTokenFile).Trim();
        }
        else
        {
            token = prompter.Secret("Paste the join token");
        }
        return JoinTokenCodec.Decode(token);
    }

    private static async Task CheckRootAsync(ProcessRunner processes)
    {
        if (Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1")
            return;
        var inspection = new ProcessRunner(dryRun: false);
        var result = await inspection.RunAsync("id", ["-u"], printOutput: false);
        if (result.ExitCode != 0 || result.Output.Trim() != "0")
            throw new InvalidOperationException(
                "Native setup must run as root. Rerun with sudo.");
    }

    private static async Task CheckPlatformAsync(
        ProcessRunner processes,
        bool native,
        bool controlPlane,
        bool host)
    {
        Console.WriteLine();
        Console.WriteLine("Prerequisites");
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "This setup executable supports Linux x64.");
        Console.WriteLine("  [ok] Linux x64");
        Console.WriteLine("  [ok] .NET SDK/runtime is not required. Setup and product binaries are self-contained.");

        if (!native)
        {
            await RequireCommandAsync(processes, "docker", "Install Docker Engine from https://docs.docker.com/engine/install/.");
            var compose = await new ProcessRunner(dryRun: false).RunAsync(
                "docker",
                ["compose", "version"],
                printOutput: false);
            if (compose.ExitCode != 0)
                throw new InvalidOperationException(
                    "Docker Compose v2 is required for the demo path. Install the Docker Compose plugin.");
            Console.WriteLine("  [ok] Docker Compose v2 (demo only)");
            return;
        }

        foreach (var command in new[] { "systemctl", "install", "cp", "ln" })
            await RequireCommandAsync(processes, command, $"Install the Linux package that provides '{command}'.");
        if (controlPlane)
            await RequireCommandAsync(processes, "curl", "Install curl with your distribution package manager.");
        if (host)
        {
            await RequireCommandAsync(processes, "git", "Install Git with your distribution package manager.");
            await RequireCommandAsync(processes, "runuser", "Install util-linux with your distribution package manager.");
            Console.WriteLine("  [ok] Git");
            Console.WriteLine("  Agent CLI checks follow after the execution user is selected.");
        }
    }

    private static async Task RequireCommandAsync(
        ProcessRunner processes,
        string command,
        string instruction)
    {
        if (await processes.FindCommandAsync(command) is not null)
        {
            Console.WriteLine($"  [ok] {command}");
            return;
        }
        throw new InvalidOperationException(
            $"Missing prerequisite: {command}. {instruction}");
    }

    private static async Task<(string Group, string Home)> ResolveUserAsync(
        ProcessRunner processes,
        string user,
        CancellationToken cancellationToken)
    {
        var inspection = new ProcessRunner(dryRun: false);
        var group = await inspection.RunAsync(
            "id",
            ["-gn", user],
            printOutput: false,
            cancellationToken: cancellationToken);
        var passwd = await inspection.RunAsync(
            "getent",
            ["passwd", user],
            printOutput: false,
            cancellationToken: cancellationToken);
        if (group.ExitCode != 0 || passwd.ExitCode != 0)
            throw new InvalidOperationException(
                $"Linux execution user '{user}' does not exist.");
        var fields = passwd.Output.Trim().Split(':');
        if (fields.Length < 6 || !Path.IsPathRooted(fields[5]))
            throw new InvalidOperationException(
                $"Could not resolve the home directory for {user}.");
        return (group.Output.Trim(), fields[5]);
    }

    private static async Task VerifyCliAsync(
        ProcessRunner processes,
        string user,
        string homeDirectory,
        string cli,
        string path,
        CancellationToken cancellationToken)
    {
        var inspection = new ProcessRunner(dryRun: false);
        var inspectCurrentUserDirectly =
            Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1"
            && string.Equals(user, Environment.UserName, StringComparison.Ordinal);
        var versionCommand = new List<string>
        {
            "env",
            $"HOME={homeDirectory}",
            path,
            "--version",
        };
        if (!inspectCurrentUserDirectly)
            versionCommand.InsertRange(0, ["-u", user, "--"]);
        var version = await inspection.RunAsync(
            inspectCurrentUserDirectly ? versionCommand[0] : "runuser",
            inspectCurrentUserDirectly ? versionCommand.Skip(1) : versionCommand,
            printOutput: false,
            cancellationToken: cancellationToken);
        if (version.ExitCode != 0)
            throw new InvalidOperationException(
                $"{cli} is present at {path}, but '{cli} --version' failed for {user}.");
        Console.WriteLine($"  [ok] {version.Output.Trim()}");

        var authArguments = new List<string> { "env", $"HOME={homeDirectory}", path };
        authArguments.AddRange(cli.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? ["login", "status"]
            : ["auth", "status", "--text"]);
        if (!inspectCurrentUserDirectly)
            authArguments.InsertRange(0, ["-u", user, "--"]);
        var auth = await inspection.RunAsync(
            inspectCurrentUserDirectly ? authArguments[0] : "runuser",
            inspectCurrentUserDirectly ? authArguments.Skip(1) : authArguments,
            printOutput: false,
            cancellationToken: cancellationToken);
        if (auth.ExitCode != 0)
        {
            var login = cli.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? $"sudo -u {user} -H {path} login --device-auth"
                : $"sudo -u {user} -H {path} auth login --claudeai";
            throw new InvalidOperationException(
                $"{cli} is not authenticated for {user}. Run '{login}' on this host, verify the login, then rerun setup.");
        }
        Console.WriteLine($"  [ok] {cli} authentication belongs to host user {user}");
    }

    private static SetupMode AskMode(ConsolePrompter prompter)
    {
        Console.WriteLine();
        Console.WriteLine("Choose an onboarding path:");
        Console.WriteLine("  1. Demo only (Docker, no repositories)");
        Console.WriteLine("  2. Single machine (Control Plane and Agent Host)");
        Console.WriteLine("  3. Multi-machine Control Plane");
        Console.WriteLine("  4. Join this machine as an Agent Host");
        return prompter.Ask("Selection", "2") switch
        {
            "1" => SetupMode.Demo,
            "2" => SetupMode.SingleMachine,
            "3" => SetupMode.ControlPlane,
            "4" => SetupMode.AgentHost,
            _ => throw new ArgumentException("Selection must be 1, 2, 3 or 4."),
        };
    }

    private static void PrintJoinInstructions(JoinPayload payload)
    {
        var token = JoinTokenCodec.Encode(payload);
        Console.WriteLine();
        Console.WriteLine("Control Plane setup is complete.");
        Console.WriteLine("On each Agent Host machine, copy the setup executable and run:");
        Console.WriteLine();
        Console.WriteLine("  sudo ./agent-orchestrator-setup --join");
        Console.WriteLine();
        Console.WriteLine("Paste this token when prompted:");
        Console.WriteLine();
        Console.WriteLine($"  {token}");
        Console.WriteLine();
        Console.WriteLine(
            "Treat the join token as a secret. It contains a credential that setup uses to mint one bound Runner principal; it is encoded, not encrypted.");
        Console.WriteLine(
            "The join token never becomes the Runner service credential. Revoke or rotate its Studio principal after enrollment, and do not place it in shell history, chat, task text or source control.");
    }

    private static void PrintGitHubTokenGuide(string remote)
    {
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return;
        Console.WriteLine();
        Console.WriteLine("Create token");
        Console.WriteLine(
            "  Preferred: a fine-grained GitHub PAT owned by the organization or account that owns the repository.");
        Console.WriteLine(
            "  Limit it to assigned repositories. Grant Contents: Read and write and Workflows: Read and write.");
        Console.WriteLine(
            "  Compatibility fallback: a classic PAT with repo and workflow, plus organization SSO authorization when required.");
        Console.WriteLine(
            "  Prefer a dedicated machine account. A PAT belongs to the user who creates it, not to the organization.");
        Console.WriteLine(
            "  Setup records and verifies both the URL with and without the .git suffix.");
        Console.WriteLine(
            "  Full checklist: docs/operations/setup/linux-runner-host.md#token-requirements");
    }

    private static void PrintHostFinish(string serverUrl)
    {
        Console.WriteLine();
        Console.WriteLine("Agent Host setup is complete.");
        Console.WriteLine($"Control Plane: {serverUrl}");
        Console.WriteLine("Inspect the host with: systemctl status agent-host");
    }

    private static void PrintHeader()
    {
        Console.WriteLine("Agent Orchestrator guided setup");
        Console.WriteLine("================================");
    }

    private static string AssemblyVersion()
        => typeof(SetupApplication).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? "unknown";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            agent-orchestrator-setup - guided Linux x64 onboarding

            Usage:
              sudo ./agent-orchestrator-setup
              ./agent-orchestrator-setup --mode demo [--demo-port 4011]
              sudo ./agent-orchestrator-setup --mode single
              sudo ./agent-orchestrator-setup --mode control-plane --server-url https://tasks.example.com
              sudo ./agent-orchestrator-setup --mode control-plane --target docker --server-url task-server-01.wg.internal
              sudo ./agent-orchestrator-setup --join
              sudo ./agent-orchestrator-setup --join --join-token-file /secure/path/join.token

            Options:
              --mode <demo|single|control-plane|agent-host>
              --target <systemd|docker>   Control Plane runtime; systemd is the default. docker requires --mode control-plane and boots deploy/compose/control-plane/compose.yaml.
              --release-version <X.Y.Z>   Release to install; defaults to this setup binary's version
              --release-dir <path>        Offline directory containing release archives and SHA256SUMS
              --listen-url <url>          Private Task Server listener (systemd target only)
              --server-url <url>          URL visible from Agent Host machines (systemd); private WireGuard-only DNS name (docker)
              --wg-address <ip>           task-server-01 WireGuard interface address (docker target only)
              --offhost-backup-path <path> Already-mounted off-host backup destination (docker target only)
              --join                      Join this machine as an Agent Host; token is prompted securely
              --join-token-file <path>    Read the join token from a protected file
              --runner-name <name>        Host identity shown in the Control Plane; bootstrap Runner id for the docker target
              --execution-user <user>     Linux user that owns Agent CLI and Git credentials
              --agent-cli <codex|claude>  Host CLI to execute
              --git-remote <url>          Credential-free host Git probe URL
              --git-push-remote <url>     Optional separate push probe URL
              --role <coding|review>      Agent Host service role
              --max-parallelism <n>       Host run slots; default 2
              --demo-port <port>          Loopback demo UI port; default 4011
              --non-interactive           Require all needed values as options or protected files
              --dry-run                   Print planned mutations after real prerequisite checks
              --version                   Print setup version
              -h, --help                  Show this help

            Join tokens are never accepted directly as command-line values.
            """);
    }
}
