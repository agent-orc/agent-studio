using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Setup;

internal static class SetupApplication
{
    /// <param name="releaseVerified">Runs once the selected release artifacts passed verification,
    /// before any host change; the product installer records its release pin there.</param>
    public static async Task<int> RunAsync(string[] args, InstallPaths? installationPaths = null,
        Func<Task>? releaseVerified = null)
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
                    $"agent-studio-setup {AssemblyVersion()}");
                return 0;
            }

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            await RunSetupAsync(options, shutdown.Token, installationPaths ?? InstallPaths.Load(),
                releaseVerified ?? (() => Task.CompletedTask));
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
        CancellationToken cancellationToken,
        InstallPaths paths,
        Func<Task> releaseVerified)
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

        // A dry run plans the native installation without writing system paths.
        if (!options.DryRun)
            await CheckRootAsync(processes);
        await CheckPlatformAsync(
            processes,
            native: true,
            controlPlane: mode is SetupMode.SingleMachine or SetupMode.ControlPlane,
            host: mode is SetupMode.SingleMachine or SetupMode.AgentHost);

        if (mode == SetupMode.AgentHost)
        {
            var payload = ReadJoinPayload(options);
            var route = await PreflightProbe.AuthorityAsync(new Uri(payload.ServerUrl), cancellationToken);
            if (route.Reachable != true || route.Trusted == false)
                throw new InvalidOperationException("The selected Task Server route did not pass the reachability and TLS probe; no package was installed.");
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
            NativeInstaller.ValidateHostRelease(hostRelease);
            await releaseVerified();
            await ConfigureAndInstallHostAsync(
                options,
                prompter,
                processes,
                hostRelease,
                payload,
                cancellationToken,
                paths,
                standaloneHost: true);
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
            await releaseVerified();
            var docker = new DockerInstaller(paths, processes, options.DryRun);
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
        var oneBoxHostRelease = mode == SetupMode.SingleMachine
            ? await artifacts.ExtractHostAsync(cancellationToken)
            : null;
        if (oneBoxHostRelease is not null)
            NativeInstaller.ValidateHostRelease(oneBoxHostRelease);
        await releaseVerified();
        var native = new NativeInstaller(paths, processes, options.DryRun);
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
            await ConfigureAndInstallHostAsync(
                options,
                prompter,
                processes,
                oneBoxHostRelease!,
                joinPayload,
                cancellationToken,
                paths);
            Console.WriteLine();
            Console.WriteLine("Single-machine setup is complete.");
            Console.WriteLine($"Task Server: {control.ServerUrl}");
            Console.WriteLine("Open Agent Studio at http://127.0.0.1:4011/.");
            PrintJoinInstructions(joinPayload, "Single-machine Studio");
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
        CancellationToken cancellationToken,
        InstallPaths paths,
        bool standaloneHost = false)
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
            prompter,
            options.NonInteractive,
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
        if (payload.SchemaVersion == 2 && !string.Equals(payload.RunnerId, runnerId, StringComparison.Ordinal))
            throw new InvalidOperationException($"This join token is bound to Runner '{payload.RunnerId}'. Select the matching host name and role.");
        // Keep the one-time enrolment code unused until local CLI and Git checks pass.
        var runnerCredential = new string('0', 64);
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
        string? gitAccess = null;
        string? sshKeyFile = null;
        if (gitRemote is not null)
        {
            Console.WriteLine("GitHub access: token file, a deploy key for this project, or an existing SSH key.");
            gitAccess = options.GitAccess ?? prompter.Ask(
                "Access method (token-file, deploy-key, existing-ssh)",
                gitRemote.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? "token-file" : "existing-ssh");
            if (gitAccess is not ("token-file" or "deploy-key" or "existing-ssh"))
                throw new ArgumentException("GitHub access must be token-file, deploy-key, or existing-ssh.");
            if (gitAccess is "deploy-key" or "existing-ssh")
            {
                if (gitRemote.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("SSH key access requires a git@github.com: or ssh:// Git remote.");
                sshKeyFile = options.GitSshKeyFile
                             ?? (gitAccess == "deploy-key"
                                 ? Path.Combine(homeDirectory, ".ssh", $"agent-studio-{runnerName}")
                                 : prompter.Ask("Existing SSH private key path"));
                sshKeyFile = Path.GetFullPath(sshKeyFile);
            }
        }
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
            agentCli.ToLowerInvariant(),
            installedClis.GetValueOrDefault("claude"),
            installedClis.GetValueOrDefault("codex"),
            gitRemote,
            gitPushRemote,
            options.MaxParallelism,
            sshKeyFile);
        var native = new NativeInstaller(paths, processes, options.DryRun);

        if (gitRemote is not null)
        {
            if (gitAccess == "token-file")
            {
                PrintGitHubTokenGuide(gitRemote);
                var username = options.GitUsername ?? prompter.Ask("GitHub username");
                var tokenFile = options.GitTokenFile
                    ?? prompter.Ask("Owner-only file containing the GitHub token");
                SetupSecrets.RequireProtected(tokenFile, "GitHub token file");
                var token = File.ReadAllText(tokenFile).Trim();
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
            else if (gitAccess == "deploy-key")
            {
                var publicKey = await native.PrepareDeployKeyAsync(
                    executionUser, homeDirectory, sshKeyFile!, options.NonInteractive, cancellationToken);
                Console.WriteLine("Add this public key as a write-enabled deploy key to the selected GitHub project:");
                Console.WriteLine(publicKey);
                if (!options.NonInteractive && !prompter.Confirm("Deploy key is registered with write access"))
                    throw new InvalidOperationException("Register the deploy key on GitHub and rerun setup.");
                await native.VerifyGitReadAsync(configuration, cancellationToken);
            }
            else
            {
                SetupSecrets.RequireProtected(sshKeyFile!, "SSH private key");
                await native.VerifyGitReadAsync(configuration, cancellationToken);
            }
            Console.WriteLine(
                "  The daemon will test push capability at startup and block coding claims if access is read-only.");
        }
        else
        {
            Console.WriteLine(
                "  No Git probe repository was selected. The host will register, but coding claims remain disabled until Git write access is configured.");
        }

        if (!options.DryRun)
            configuration = configuration with
            {
                Credential = await MintRunnerCredentialAsync(payload, runnerId, cancellationToken),
            };
        await native.InstallAgentHostAsync(
            hostRelease,
            configuration,
            cancellationToken);
        if (standaloneHost)
            await native.LinkHostSetupCommandAsync(cancellationToken);
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
            if (!client.DefaultRequestHeaders.Contains(TaskServerProtocol.HeaderName))
                client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            if (!client.DefaultRequestHeaders.Contains(TaskServerProtocol.ClientVersionHeaderName))
                client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName, ReleaseArtifacts.CurrentVersion());
            if (!client.DefaultRequestHeaders.Contains("X-Client-Id"))
                client.DefaultRequestHeaders.Add("X-Client-Id", "agent-orchestrator-setup");
            if (payload.SchemaVersion == 2)
            {
                if (payload.ExpiresAtUtc <= DateTime.UtcNow)
                    throw new InvalidOperationException("Join token expired. Issue a new token on the Task Server.");
                using var exchange = await client.PostAsJsonAsync("/api/v1/enrolments/exchange",
                    new ExchangeEnrolmentRequest(payload.Credential, payload.InstallationId!), cancellationToken);
                if (!exchange.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Task Server denied the single-use join token ({(int)exchange.StatusCode}). Issue a new token and retry.");
                var enrolled = await exchange.Content.ReadFromJsonAsync<ExchangedEnrolment>(cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Task Server returned an empty enrolment response.");
                if (enrolled.InstallationId != payload.InstallationId
                    || enrolled.Issued.Principal.RunnerId != runnerId)
                    throw new InvalidOperationException("Task Server returned an unexpected installation or Runner identity.");
                return enrolled.Issued.Credential
                    ?? throw new InvalidOperationException("Task Server returned no Runner credential.");
            }
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", payload.Credential);
            var response = await client.PostAsJsonAsync(
                "/api/v1/management/principals",
                new CreatePrincipalRequest(
                    $"runner:{runnerId}",
                    TaskServerPrincipalKinds.Runner,
                    RunnerId: runnerId),
                cancellationToken);
            string? operationId = null;
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                response.Dispose();
                operationId = $"setup-rejoin-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-"
                              + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                response = await client.PostAsJsonAsync(
                    $"/api/v1/management/principals/{Uri.EscapeDataString($"runner:{runnerId}")}/rotate",
                    new RotatePrincipalRequest(
                        OperationId: operationId,
                        Consumers: [new PrincipalRotationConsumer(runnerId, TaskServerScopes.TasksRead)]),
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
            var credential = issued.Credential
                             ?? throw new InvalidOperationException(
                                 "Task Server returned no Runner credential.");
            if (operationId is not null)
                await CompleteRunnerRotationAsync(client, operationId, runnerId, credential, cancellationToken);
            return credential;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    // A re-join rotates the enrolled Runner principal. Setup is its single
    // declared consumer: it records delivery, proves the new bearer with one
    // tasks:read call, and acknowledges, which retires the previous generation.
    // Without the acknowledgement the rotation stays open and the next re-join
    // after its deadline would need a recovery principal.
    private static async Task CompleteRunnerRotationAsync(
        HttpClient client,
        string operationId,
        string runnerId,
        string credential,
        CancellationToken cancellationToken)
    {
        var operation = Uri.EscapeDataString(operationId);
        foreach (var (method, path, content) in new (HttpMethod, string, HttpContent?)[]
                 {
                     (HttpMethod.Post, $"/api/v1/principal-rotations/{operation}/delivered", null),
                     (HttpMethod.Get, "/api/v1/workspaces", null),
                     (HttpMethod.Post, $"/api/v1/principal-rotations/{operation}/ack",
                         JsonContent.Create(new PrincipalRotationAcknowledgement(runnerId))),
                 })
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.Add("X-Principal-Consumer-Id", runnerId);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Task Server did not complete the Runner '{runnerId}' credential rotation at {method} {path} ({(int)response.StatusCode}).");
        }
    }

    private static JoinPayload ReadJoinPayload(SetupOptions options)
    {
        if (options.JoinTokenFile is null)
            throw new ArgumentException("Joining a host requires --join-token-file with an owner-only token file.");
        SetupSecrets.RequireProtected(options.JoinTokenFile, "Join token file");
        var token = File.ReadAllText(options.JoinTokenFile).Trim();
        var payload = JoinTokenCodec.Decode(token);
        if (payload.SchemaVersion != 2)
            throw new InvalidOperationException("Legacy join tokens carry reusable management credentials. Issue a single-use token with the join-token command.");
        return payload;
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
        {
            if (command == "systemctl"
                && Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMCTL") is { Length: > 0 } fixture
                && File.Exists(fixture))
            {
                Console.WriteLine($"  [ok] systemctl at {fixture}");
                continue;
            }
            await RequireCommandAsync(processes, command,
                $"Install the Linux package that provides '{command}'.");
        }
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
        ConsolePrompter prompter,
        bool nonInteractive,
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
            if (nonInteractive || !prompter.Confirm($"Start {cli} provider login as {user} now", true))
                throw new InvalidOperationException(
                    $"{cli} is not authenticated for {user}. Sign in as that user and rerun setup.");
            var loginArguments = new List<string> { "env", $"HOME={homeDirectory}", path };
            loginArguments.AddRange(cli.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? ["login", "--device-auth"]
                : ["auth", "login", "--claudeai"]);
            if (!inspectCurrentUserDirectly)
                loginArguments.InsertRange(0, ["-u", user, "--"]);
            Console.WriteLine($"Follow the {cli} sign-in instructions displayed by the CLI.");
            await processes.RequireInteractiveAsync(
                inspectCurrentUserDirectly ? loginArguments[0] : "runuser",
                inspectCurrentUserDirectly ? loginArguments.Skip(1) : loginArguments,
                cancellationToken);
            auth = await inspection.RunAsync(
                inspectCurrentUserDirectly ? authArguments[0] : "runuser",
                inspectCurrentUserDirectly ? authArguments.Skip(1) : authArguments,
                printOutput: false,
                cancellationToken: cancellationToken);
            if (auth.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{cli} login did not pass the authentication check for {user}.");
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

    private static void PrintJoinInstructions(JoinPayload payload, string label = "Control Plane")
    {
        Console.WriteLine();
        Console.WriteLine($"{label} setup is complete.");
        Console.WriteLine("After bootstrapping an owner, issue a single-use join token with 'agent-studio-setup join-token --help'.");
        Console.WriteLine($"Task Server URL for Agent Hosts: {payload.ServerUrl}");
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
            agent-studio-setup - guided Linux x64 remote onboarding

            Usage:
              ./agent-studio-setup --mode studio
              sudo ./agent-studio-setup --mode single
              sudo ./agent-studio-setup --mode control-plane --server-url https://tasks.example.com
              sudo ./agent-studio-setup --mode control-plane --target docker --server-url task-server-01.wg.internal
              sudo ./agent-studio-setup --join --join-token-file /secure/path/join.token

            Options:
              --mode <single|control-plane|agent-host>
              --target <systemd|docker>   Control Plane runtime; systemd is the default. docker requires --mode control-plane and boots deploy/compose/control-plane/compose.yaml.
              --release-version <X.Y.Z>   Release to install; defaults to this setup binary's version
              --release-dir <path>        Offline directory containing release archives and SHA256SUMS
              --listen-url <url>          Private Task Server listener (systemd target only)
              --server-url <url>          URL visible from Agent Host machines (systemd); private WireGuard-only DNS name (docker)
              --wg-address <ip>           task-server-01 WireGuard interface address (docker target only)
              --offhost-backup-path <path> Already-mounted off-host backup destination (docker target only)
              --join                      Join this machine as an Agent Host; requires --join-token-file
              --join-token-file <path>    Read the join token from a protected file
              --runner-name <name>        Host identity shown in the Control Plane; bootstrap Runner id for the docker target
              --execution-user <user>     Linux user that owns Agent CLI and Git credentials
              --agent-cli <codex|claude>  Host CLI to execute
              --git-remote <url>          Credential-free host Git probe URL
              --git-push-remote <url>     Optional separate push probe URL
              --role <coding|review>      Agent Host service role
              --max-parallelism <n>       Host run slots; default 2
              --non-interactive           Require all needed values as options or protected files
              --dry-run                   Print planned mutations after real prerequisite checks
              --version                   Print setup version
              -h, --help                  Show this help

            Join tokens are never accepted directly as command-line values.
            """);
    }
}
