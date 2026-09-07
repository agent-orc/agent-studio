using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Setup;

internal sealed record ControlPlaneResult(
    string ServerUrl,
    string Credential,
    string ReleaseVersion);

internal sealed record HostConfiguration(
    string ServerUrl,
    string Credential,
    string ReleaseVersion,
    string RunnerId,
    string RunnerName,
    string Role,
    string ExecutionUser,
    string ExecutionGroup,
    string HomeDirectory,
    string CliPath,
    string? ClaudePath,
    string? CodexPath,
    string CliArguments,
    string? GitRemote,
    string? GitPushRemote,
    int MaxParallelism);

internal sealed class NativeInstaller(
    InstallPaths paths,
    ProcessRunner processes,
    bool dryRun)
{
    public async Task<ControlPlaneResult> InstallControlPlaneAsync(
        string orchestratorRelease,
        string studioRelease,
        string listenUrl,
        string hostVisibleServerUrl,
        string version,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Installing the Control Plane");
        var studioCredential = ExistingCredential("studio.token")
                               ?? ExistingCredential("task-server.token")
                               ?? RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var engineCredential = ExistingCredential("engine.token")
                               ?? RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var environment = new Dictionary<string, string?>
        {
            ["NONINTERACTIVE"] = "1",
            ["LISTEN_URL"] = listenUrl,
            ["AUTH_MODE"] = "bearer",
            ["STUDIO_AUTH_TOKEN"] = studioCredential,
            ["ENGINE_AUTH_TOKEN"] = engineCredential,
            ["AGENT_ORCHESTRATOR_OPT_ROOT"] = paths.OrchestratorOpt,
            ["AGENT_ORCHESTRATOR_CONFIG_ROOT"] = paths.OrchestratorConfig,
            ["AGENT_ORCHESTRATOR_STATE_ROOT"] = paths.OrchestratorState,
            ["AGENT_ORCHESTRATOR_SYSTEMD_ROOT"] = paths.Systemd,
        };
        if (Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1")
        {
            environment["AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK"] = "1";
            environment["AGENT_ORCHESTRATOR_SKIP_USER_CREATE"] = "1";
        }
        if (Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMCTL") is { Length: > 0 } systemctl)
            environment["SYSTEMCTL_BIN"] = systemctl;

        await processes.RequireAsync(
            "/bin/sh",
            [Path.Combine(orchestratorRelease, "install.sh"), orchestratorRelease],
            environment,
            cancellationToken: cancellationToken);
        await InstallStudioAsync(studioRelease, version, cancellationToken);

        Console.WriteLine($"  [ok] Task Server and Orchestrator Engine: {listenUrl}");
        Console.WriteLine($"  [ok] Agent Studio static files: {paths.StudioOpt}/current/browser");
        Console.WriteLine(
            $"  Caddy remains host infrastructure. Template: {paths.OrchestratorOpt}/current/config/Caddyfile.template");
        if (!new Uri(hostVisibleServerUrl).IsLoopback)
        {
            Console.WriteLine(
                "  Before adding remote hosts, terminate TLS at the host-visible URL and proxy /api/*, /healthz and /readyz to the private listen URL.");
        }

        return new ControlPlaneResult(hostVisibleServerUrl, studioCredential, version);
    }

    public async Task InstallAgentHostAsync(
        string hostRelease,
        HostConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"Installing the {configuration.Role} agent host");
        var binarySource = Path.Combine(hostRelease, "linux-x64", "agent-host");
        var unitSource = Path.Combine(hostRelease, "systemd", "agent-host.service");
        var governanceSource = Path.Combine(hostRelease, "agent-host-resource-governance.sh");
        foreach (var required in new[] { binarySource, unitSource, governanceSource })
        {
            if (!File.Exists(required))
                throw new InvalidDataException($"Agent Host release is missing {required}.");
        }

        var versionRoot = Path.Combine(paths.HostOpt, configuration.ReleaseVersion);
        var unitName = configuration.Role == "coding"
            ? "agent-host.service"
            : "agent-host-review.service";
        var unitPath = Path.Combine(paths.Systemd, unitName);
        var environmentPath = Path.Combine(
            paths.HostConfig,
            configuration.Role == "coding" ? "runner.env" : "review.env");
        var credentialPath = Path.Combine(
            paths.HostConfig,
            configuration.Role == "coding" ? "runner.token" : "review.token");
        var workRoot = Path.Combine(paths.HostState, configuration.Role);
        var stateRoot = Path.Combine(paths.HostState, $"{configuration.Role}-state");

        await processes.RequireAsync(
            "install",
            ["-d", "-m", "0755", paths.HostOpt, versionRoot, paths.HostConfig, paths.Systemd],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            "install",
            ["-d", "-m", "0750", workRoot, stateRoot],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            "install",
            ["-m", "0755", binarySource, Path.Combine(versionRoot, "agent-host")],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            "install",
            ["-m", "0755", governanceSource, Path.Combine(versionRoot, "agent-host-resource-governance")],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            "ln",
            ["-sfnT", versionRoot, Path.Combine(paths.HostOpt, "current")],
            cancellationToken: cancellationToken);

        var environment = BuildRunnerEnvironment(configuration, credentialPath, workRoot, stateRoot);
        WriteProtectedFile(environmentPath, environment, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead);
        WriteProtectedFile(credentialPath, configuration.Credential + Environment.NewLine,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var unit = BuildUnit(
            configuration,
            unitName,
            environmentPath,
            workRoot,
            stateRoot);
        WriteProtectedFile(unitPath, unit,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") != "1")
        {
            await processes.RequireAsync(
                "chown",
                [
                    "-R",
                    $"{configuration.ExecutionUser}:{configuration.ExecutionGroup}",
                    workRoot,
                    stateRoot,
                ],
                cancellationToken: cancellationToken);
            await processes.RequireAsync(
                "chown",
                [
                    $"root:{configuration.ExecutionGroup}",
                    environmentPath,
                    credentialPath,
                ],
                cancellationToken: cancellationToken);
        }

        var skipSystemd = Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_SYSTEMD") == "1";
        if (skipSystemd)
        {
            Console.WriteLine("  [test] systemd start skipped by AGENT_SETUP_SKIP_SYSTEMD=1");
            return;
        }

        var systemctl = Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMCTL") ?? "systemctl";
        await processes.RequireAsync(systemctl, ["daemon-reload"], cancellationToken: cancellationToken);
        await processes.RequireAsync(
            systemctl,
            ["enable", "--now", unitName],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            systemctl,
            ["is-active", "--quiet", unitName],
            cancellationToken: cancellationToken);
        if (dryRun)
        {
            Console.WriteLine(
                $"[dry-run] registration check for {configuration.RunnerId} at {configuration.ServerUrl}");
            return;
        }
        var startupStatus = await VerifyRegistrationAsync(configuration, cancellationToken);
        Console.WriteLine(
            $"  [ok] {unitName} is active and registered as {configuration.RunnerId} ({startupStatus})");
        ExplainStartupStatus(startupStatus);
    }

    public async Task ConfigureGitHubCredentialAsync(
        HostConfiguration configuration,
        string username,
        string token,
        CancellationToken cancellationToken)
    {
        if (configuration.GitRemote is null)
            return;
        if (!Uri.TryCreate(configuration.GitRemote, UriKind.Absolute, out var remote)
            || !string.Equals(remote.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || remote.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(
                "The guided PAT store supports credential-free https://github.com URLs only.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("GitHub username and token are required.");

        var variants = GitHubUrlVariants(configuration.GitRemote);
        await RunAsUserAsync(
            configuration.ExecutionUser,
            configuration.HomeDirectory,
            "git",
            ["config", "--global", "credential.https://github.com.useHttpPath", "true"],
            cancellationToken: cancellationToken);
        var configuredHelpers = await RunAsUserAsync(
            configuration.ExecutionUser,
            configuration.HomeDirectory,
            "git",
            ["config", "--global", "--get-all", "credential.helper"],
            cancellationToken: cancellationToken,
            requireSuccess: false);
        if (string.IsNullOrWhiteSpace(configuredHelpers.Output))
        {
            await RunAsUserAsync(
                configuration.ExecutionUser,
                configuration.HomeDirectory,
                "git",
                ["config", "--global", "credential.helper", "store"],
                cancellationToken: cancellationToken);
        }
        foreach (var variant in variants)
        {
            var uri = new Uri(variant);
            var input =
                $"protocol=https\nhost=github.com\npath={uri.AbsolutePath.TrimStart('/')}\nusername={username}\npassword={token}\n\n";
            await RunAsUserAsync(
                configuration.ExecutionUser,
                configuration.HomeDirectory,
                "git",
                ["credential", "approve"],
                input,
                cancellationToken);
        }
        foreach (var variant in variants)
        {
            await RunAsUserAsync(
                configuration.ExecutionUser,
                configuration.HomeDirectory,
                "git",
                ["ls-remote", variant],
                cancellationToken: cancellationToken);
            Console.WriteLine($"  [ok] Git read credential verified for {variant}");
        }
    }

    public async Task VerifyGitReadAsync(
        HostConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.GitRemote is null)
            return;
        foreach (var variant in GitHubUrlVariants(configuration.GitRemote))
        {
            var result = await RunAsUserAsync(
                configuration.ExecutionUser,
                configuration.HomeDirectory,
                "git",
                ["ls-remote", variant],
                cancellationToken: cancellationToken,
                requireSuccess: false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Git could not read {variant} as {configuration.ExecutionUser}. Configure the host credential, then rerun setup.");
            Console.WriteLine($"  [ok] Git read access: {variant}");
        }
    }

    private async Task InstallStudioAsync(
        string studioRelease,
        string version,
        CancellationToken cancellationToken)
    {
        var browser = Path.Combine(studioRelease, "browser");
        if (!File.Exists(Path.Combine(browser, "index.html")))
            throw new InvalidDataException("Agent Studio release has no browser/index.html.");
        var target = Path.Combine(paths.StudioOpt, version);
        await processes.RequireAsync(
            "install",
            ["-d", "-m", "0755", paths.StudioOpt, target, Path.Combine(target, "browser")],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            "cp",
            ["-a", $"{browser}/.", Path.Combine(target, "browser")],
            cancellationToken: cancellationToken);
        await processes.RequireAsync(
            "ln",
            ["-sfnT", target, Path.Combine(paths.StudioOpt, "current")],
            cancellationToken: cancellationToken);
    }

    private string? ExistingCredential(string fileName)
    {
        var tokenPath = Path.Combine(paths.OrchestratorConfig, fileName);
        if (!File.Exists(tokenPath))
            return null;
        var token = File.ReadAllText(tokenPath).Trim();
        return token.Length >= 32 ? token : null;
    }

    private void WriteProtectedFile(string path, string content, UnixFileMode mode)
    {
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] write {path} ({content.Length} bytes, secret values redacted)");
            return;
        }
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"Path has no parent: {path}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}");
        File.WriteAllText(temporary, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, mode);
        File.Move(temporary, path, overwrite: true);
    }

    internal static string BuildRunnerEnvironment(
        HostConfiguration configuration,
        string credentialPath,
        string workRoot,
        string stateRoot)
    {
        var lines = new List<string>
        {
            $"RUNNER_SERVER_URL={EnvironmentValue(configuration.ServerUrl)}",
            $"RUNNER_ID={EnvironmentValue(configuration.RunnerId)}",
            $"RUNNER_NAME={EnvironmentValue(configuration.RunnerName)}",
            $"RUNNER_ROLE={configuration.Role}",
            $"RUNNER_AUTH_TOKEN_FILE={EnvironmentValue(credentialPath)}",
            $"RUNNER_WORKDIR={EnvironmentValue(workRoot)}",
            $"RUNNER_STATE_DIR={EnvironmentValue(stateRoot)}",
            $"RUNNER_MAX_PARALLELISM={configuration.MaxParallelism}",
            $"RUNNER_CLI_BIN={EnvironmentValue(configuration.CliPath)}",
            $"RUNNER_CLI_ARGS={EnvironmentValue(configuration.CliArguments)}",
        };
        if (configuration.ClaudePath is not null)
            lines.Add($"RUNNER_CLAUDE_CLI_BIN={EnvironmentValue(configuration.ClaudePath)}");
        if (configuration.CodexPath is not null)
            lines.Add($"RUNNER_CODEX_CLI_BIN={EnvironmentValue(configuration.CodexPath)}");
        if (configuration.GitRemote is not null)
            lines.Add($"RUNNER_GIT_REMOTE={EnvironmentValue(configuration.GitRemote)}");
        if (configuration.GitPushRemote is not null)
            lines.Add($"RUNNER_GIT_PUSH_REMOTE={EnvironmentValue(configuration.GitPushRemote)}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private string BuildUnit(
        HostConfiguration configuration,
        string unitName,
        string environmentPath,
        string workRoot,
        string stateRoot)
    {
        var quota = configuration.Role == "coding"
            ? Math.Max(1, Environment.ProcessorCount) * 100
            : Math.Max(100, Math.Max(1, Environment.ProcessorCount) * 100 / 3);
        var weight = configuration.Role == "coding" ? 100 : 30;
        var alias = unitName == "agent-host.service"
            ? $"{Environment.NewLine}Alias=agent-runner.service"
            : string.Empty;
        return $"""
            [Unit]
            Description=Agent Studio {configuration.Role} agent host
            After=network-online.target
            Wants=network-online.target
            StartLimitIntervalSec=300
            StartLimitBurst=5

            [Service]
            Type=simple
            User={configuration.ExecutionUser}
            Group={configuration.ExecutionGroup}
            WorkingDirectory={workRoot}
            Environment=HOME={configuration.HomeDirectory}
            EnvironmentFile={environmentPath}
            ExecStart={paths.HostOpt}/current/agent-host --poll
            Restart=always
            RestartSec=10s
            TimeoutStopSec=90s
            KillSignal=SIGTERM
            KillMode=process
            SyslogIdentifier={Path.GetFileNameWithoutExtension(unitName)}
            StandardOutput=journal
            StandardError=journal
            NoNewPrivileges=true
            # PrivateTmp=true tears the private /tmp mount away from a still-running
            # detached worker on every restart (MSB1025, SocketException (99), NuGet
            # mkdtemp ENOENT). KillMode=process deliberately leaves workers running.
            PrivateTmp=false
            ProtectSystem=full
            ReadWritePaths={workRoot} {stateRoot} {configuration.HomeDirectory}
            CPUQuota={quota}%
            CPUWeight={weight}
            IOWeight={weight}

            [Install]
            WantedBy=multi-user.target{alias}

            """;
    }

    private async Task<string> VerifyRegistrationAsync(
        HostConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(configuration.ServerUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", configuration.Credential);
        http.DefaultRequestHeaders.Add("X-Task-Protocol-Version", "2");
        http.DefaultRequestHeaders.Add("X-Actor-Id", "agent-orchestrator-setup");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var registered = false;
        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                using var response = await http.GetAsync(
                    "/api/v1/runners",
                    cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(body);
                    var snapshot = document.RootElement.EnumerateArray().FirstOrDefault(item =>
                        item.TryGetProperty("runnerId", out var id)
                        && string.Equals(
                            id.GetString(),
                            configuration.RunnerId,
                            StringComparison.Ordinal));
                    if (snapshot.ValueKind != JsonValueKind.Undefined)
                    {
                        registered = true;
                        if (configuration.Role == "review")
                            return "ready";
                        var push = AdvertisedStatus(snapshot, "git:push");
                        var workflow = AdvertisedStatus(snapshot, "git:workflow-push");
                        if (push is not null)
                            return ClassifyGitStatus(push, workflow);
                    }
                }
            }
            catch (HttpRequestException)
            {
                // The service can still be starting. The bounded deadline is authoritative.
            }
            await Task.Delay(1000, cancellationToken);
        }
        throw new InvalidOperationException(
            registered
                ? $"Agent Host {configuration.RunnerId} registered, but did not report its startup capability result within 90 seconds. Inspect journalctl for the service."
                : $"Agent Host did not register as {configuration.RunnerId} within 90 seconds. Inspect journalctl for the service.");
    }

    internal static string ClassifyGitStatus(string push, string? workflow)
        => !string.Equals(push, "ready", StringComparison.OrdinalIgnoreCase)
            ? "read-only"
            : string.Equals(
                workflow,
                "ready-no-workflow-scope",
                StringComparison.OrdinalIgnoreCase)
                ? "ready-no-workflow-scope"
                : "ready";

    private static string? AdvertisedStatus(JsonElement snapshot, string key)
    {
        if (!snapshot.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var capability in capabilities.EnumerateArray())
        {
            var hasKey = capability.TryGetProperty("key", out var candidate)
                         || capability.TryGetProperty("kind", out candidate);
            if (!hasKey
                || !string.Equals(
                    candidate.GetString()?.Replace('-', ':'),
                    key,
                    StringComparison.Ordinal))
                continue;
            if (capability.TryGetProperty("advertisedStatus", out var status)
                || capability.TryGetProperty("status", out status))
                return status.GetString();
        }
        return null;
    }

    private static void ExplainStartupStatus(string status)
    {
        switch (status)
        {
            case "ready":
                Console.WriteLine(
                    "       ready: repository contents and workflow updates passed the startup probe.");
                break;
            case "ready-no-workflow-scope":
                Console.WriteLine(
                    "       ready-no-workflow-scope: ordinary code pushes work, but the Git token cannot update .github/workflows.");
                Console.WriteLine(
                    "       Add the workflow permission from the token checklist before assigning workflow changes.");
                break;
            case "read-only":
                Console.WriteLine(
                    "       read-only: the host is registered, but new coding claims are blocked until Git push access is fixed.");
                break;
        }
    }

    private async Task<ProcessResult> RunAsUserAsync(
        string user,
        string homeDirectory,
        string command,
        IEnumerable<string> arguments,
        string? input = null,
        CancellationToken cancellationToken = default,
        bool requireSuccess = true)
    {
        var runCurrentUserDirectly =
            Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1"
            && string.Equals(user, Environment.UserName, StringComparison.Ordinal);
        var commandArguments = new List<string>
        {
            "env",
            $"HOME={homeDirectory}",
            command,
        };
        if (!runCurrentUserDirectly)
            commandArguments.InsertRange(0, ["-u", user, "--"]);
        commandArguments.AddRange(arguments);
        var result = await processes.RunAsync(
            runCurrentUserDirectly ? commandArguments[0] : "runuser",
            runCurrentUserDirectly ? commandArguments.Skip(1) : commandArguments,
            input: input,
            cancellationToken: cancellationToken);
        if (requireSuccess && result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{command} failed for execution user {user} with code {result.ExitCode}.");
        return result;
    }

    private static IReadOnlyList<string> GitHubUrlVariants(string remote)
    {
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return [remote];
        var withoutSuffix = remote.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? remote[..^4]
            : remote;
        return [withoutSuffix, withoutSuffix + ".git"];
    }

    private static string EnvironmentValue(string value)
        => value.Any(char.IsWhiteSpace) || value.Contains('"') || value.Contains('\\')
            ? $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
}
