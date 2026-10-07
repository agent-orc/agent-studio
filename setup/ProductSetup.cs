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
        var dryRun = flags.Contains("--dry-run");

        // update, rollback, and uninstall act on the recorded installation
        // unless the operator names a profile explicitly.
        var explicitMode = Get("--mode", answers.Mode);
        var explicitTarget = Get("--target", answers.Target);
        var installDirectory = Get("--install-dir", answers.InstallDirectory);
        var serverUrl = Get("--server-url", answers.ServerUrl);
        var joinTokenFile = Get("--join-token-file", answers.JoinTokenFile);
        var tokenFile = Get("--token-file", answers.TokenFile);
        var backupPath = Get("--backup-path", null) ?? PassthroughValue(parsed, "--offhost-backup-path");
        if (command == "accept")
            return await AcceptanceJourney.RunAsync(new AcceptanceJourney.Request(
                LocateManifestRoot(installDirectory),
                serverUrl ?? throw new ArgumentException("accept requires --server-url with the Task Server URL."),
                tokenFile ?? throw new ArgumentException(
                    "accept requires --token-file with an owner-only management token file."),
                values.GetValueOrDefault("--recovery-checkpoint"),
                backupPath,
                Environment.GetEnvironmentVariable(AcceptanceJourney.CanaryCommandVariable),
                AcceptanceJourney.DefaultCanaryTimeout));
        var located = command is not ("install" or "preflight") && explicitMode is null && explicitTarget is null
            ? await LocateInstallationAsync(installDirectory)
            : null;
        var mode = located?.State.Mode ?? ProductCommand.NormalizeMode(explicitMode, joinTokenFile is not null);
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
        var journey = JourneyPolicy.ForMode(mode);
        var relocation = values.TryGetValue("--journey", out var journeyName)
            && JourneyPolicy.Parse(journeyName) == InstallationJourney.RelocateAuthority;
        // The plan is resolved before prompts and probes, so an unsupported role fails first.
        var resolvedPlan = command == "preflight"
            ? null
            : ProductPlanner.Plan(parsed, mode, target, OperatingSystem.IsWindows(), forwarded);
        if (command == "install" && resolvedPlan?.Profile == ProductProfile.ConnectorWindows)
        {
            // Prompted values pass the same preflight and protection checks as flags.
            serverUrl ??= prompter.Ask("Remote Task Server URL (https://...)", null);
            ValidateUpstream(serverUrl);
            tokenFile ??= prompter.Ask("File containing the Studio token for the remote Task Server", null);
        }
        var join = mode == "agent-host" ? ReadJoinToken(joinTokenFile) : null;
        var authorityUrl = ResolveAuthorityUrl(mode, relocation, serverUrl, join);
        var secretFile = joinTokenFile ?? tokenFile;
        var probeRoot = installDirectory ?? DefaultInstallRoot(ProductProfile.StudioDocker);
        if (command == "preflight" || command == "install")
        {
            PrintJourney(journey);
            var findings = PreflightPolicy.Evaluate(journey, target, await PreflightProbe.ObserveAsync(
                journey, target, authorityUrl, ContactsAuthority(mode, relocation), backupPath,
                secretFile is null ? null : Path.GetFullPath(secretFile), probeRoot, default));
            var failed = PrintPreflight(journey, target, findings);
            if (command == "preflight") return failed ? 1 : 0;
            if (failed && !dryRun)
                throw new InvalidOperationException(
                    "Preflight failed. Apply the recovery actions above and rerun; nothing was changed.");
        }
        var plan = resolvedPlan ?? throw new UnreachableException();
        if (command == "install" && relocation
            && JourneyPolicy.RelocationBlocker(values.GetValueOrDefault("--recovery-checkpoint"),
                flags.Contains("--authority-frozen")) is { } blocker)
            throw new InvalidOperationException(blocker);
        if (command == "install" && mode == "agent-host" && joinTokenFile is null)
            throw new ArgumentException(
                "Joining a host requires --join-token-file with an owner-only token file; interactive token paste is not supported by the guided installer.");
        if (command == "install" && secretFile is not null && !dryRun)
            SetupSecrets.RequireProtected(Path.GetFullPath(secretFile),
                mode == "agent-host" ? "Join token file" : "Token file");
        if (command == "install" && relocation)
        {
            var paths = InstallPaths.Load();
            var destination = Path.GetFullPath(installDirectory ?? paths.OrchestratorConfig);
            var proof = await RelocationGate.VerifyAsync(
                values.GetValueOrDefault("--source-manifest"),
                values.GetValueOrDefault("--recovery-checkpoint"), destination,
                flags.Contains("--authority-frozen"));
            var restored = proof.Restored;
            if (RelocationGate.AlreadyRelocated(proof))
            {
                Console.WriteLine($"Relocated authority {restored.InstallationId} is already recorded for this recovery set; no restore was repeated.");
                return 0;
            }
            if (!dryRun)
            {
                var targetUrl = serverUrl
                    ?? throw new ArgumentException("Relocation requires --server-url for the target Task Server.");
                var managementToken = tokenFile
                    ?? throw new ArgumentException("Relocation requires --token-file for the target management principal.");
                var authorityIdentity = await RelocationGate.RestoreAsync(targetUrl, managementToken,
                    values["--recovery-checkpoint"]);
                var postRestore = await RelocationGate.VerifyManifestAfterRestoreAsync(restored, destination);
                var relocated = RelocationGate.RelocatedManifest(postRestore, target, proof.SetSha256);
                await ManifestStore.WriteAsync(destination, relocated);
                await ManifestStore.CheckpointAsync(destination, relocated, "recovery-verified", "observed",
                    "Full recovery set hashes and target Task Server verify and restore responses passed.");
                await ManifestStore.CheckpointAsync(destination, relocated, "identity-matched", "observed",
                    $"Post-restore authority digest {authorityIdentity} and installation id, principals and project origin match the frozen source.");
                await ManifestStore.CheckpointAsync(destination, relocated, "authority-frozen", "operator attested",
                    "--authority-frozen was supplied; the old host mode was not observed by this installer.");
                await ManifestStore.CheckpointAsync(destination, relocated, "workspace-restored", "observed",
                    "The target Task Server reported full backup restoration; the empty-target rehearsal receipt was supplied.");
                await ManifestStore.CheckpointAsync(destination, relocated, "authenticated-canary", "not reached",
                    "Run 'agent-studio-setup accept' on the new authority after private HTTPS cutover.");
            }
            Console.WriteLine($"Relocated authority {restored.InstallationId} verified. Resume admission only after 'agent-studio-setup accept' passes after network cutover.");
            return 0;
        }
        if (plan.Profile == ProductProfile.Delegated)
        {
            var paths = ResolveDelegatedPaths(plan, installDirectory);
            var delegatedRoot = plan.Mode == "agent-host" ? paths.HostConfig : paths.OrchestratorConfig;
            var requestedDelegatedVersion = SetupOptions.NormalizeVersion(Get("--release-version", answers.ReleaseVersion));
            var delegatedVersion = requestedDelegatedVersion ?? ReleaseArtifacts.CurrentVersion();
            if (plan.Mode == "agent-host" && join is not null)
            {
                if (requestedDelegatedVersion is not null && requestedDelegatedVersion != join.ReleaseVersion)
                    throw new InvalidOperationException(
                        $"Join token requires release {join.ReleaseVersion}, but --release-version selected {requestedDelegatedVersion}.");
                delegatedVersion = join.ReleaseVersion;
            }
            var pendingDelegated = await DecideManifestAsync(delegatedRoot, command, plan, journey,
                delegatedVersion, null, dryRun);
            if (command == "uninstall")
            {
                if (pendingDelegated is null && !dryRun)
                    throw new InvalidOperationException($"No installation manifest exists at {delegatedRoot}.");
                await UninstallDelegatedAsync(plan, paths, dryRun, flags.Contains("--purge"));
                if (flags.Contains("--purge") && !dryRun)
                    PurgeDelegatedPaths(plan, paths);
                await FinishManifestAsync(delegatedRoot, pendingDelegated?.Manifest, dryRun,
                    flags.Contains("--purge"), InstallationManifest.PhaseUninstalled, "uninstalled");
                return 0;
            }
            // The release pin is recorded once the delegated flow has verified the
            // release artifacts, so a wrong or unavailable version leaves no pin behind.
            InstallationManifest? delegatedManifest = null;
            var delegatedResult = await SetupApplication.RunAsync(plan.DelegatedArguments.ToArray(), paths,
                async () => delegatedManifest = await CommitManifestAsync(pendingDelegated));
            if (delegatedResult == 0)
            {
                delegatedManifest ??= await CommitManifestAsync(pendingDelegated);
                await FinishManifestAsync(delegatedRoot, delegatedManifest, dryRun, false,
                    InstallationManifest.PhaseComplete,
                    plan.Mode == "agent-host" ? "host-enrolled" : "services-healthy");
            }
            return delegatedResult;
        }

        var root = located?.Root ?? Path.GetFullPath(installDirectory ?? DefaultInstallRoot(plan.Profile));
        var statePath = Path.Combine(root, "install-state.json");
        var state = located?.State ?? await ReadStateAsync(statePath);
        if (command != "install" && state is null)
            throw new InvalidOperationException($"No Agent Studio installation found at {root}.");
        if (state is not null && (state.Mode != plan.Mode || state.Target != plan.Target))
            throw new InvalidOperationException(
                $"The installation at {root} is --mode {state.Mode} --target {state.Target}.");
        var requestedVersion = command == "rollback"
            ? state?.PreviousVersion
            : SetupOptions.NormalizeVersion(Get("--release-version", answers.ReleaseVersion))
              ?? ReleaseArtifacts.CurrentVersion();
        var pendingManifest = await DecideManifestAsync(root, command, plan, journey, requestedVersion, state,
            dryRun);
        var manifest = pendingManifest?.Manifest;
        var process = new ProcessRunner(dryRun);
        if (plan.Profile != ProductProfile.StudioDocker)
            return await RunWindowsServicesAsync(parsed, plan, root, statePath, state, pendingManifest, process,
                prompter,
                Get("--release-version", answers.ReleaseVersion),
                Get("--release-dir", answers.ReleaseDirectory),
                serverUrl,
                tokenFile);
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
            await FinishManifestAsync(root, manifest, flags.Contains("--dry-run"), flags.Contains("--purge"),
                InstallationManifest.PhaseUninstalled, "uninstalled");
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
        if (command == "update" && version == state!.Version
            && manifest?.Phase != InstallationManifest.PhaseUpdating)
            throw new InvalidOperationException("This version is already installed.");
        if (command == "install" && state is not null)
            Console.WriteLine($"Installation {manifest?.InstallationId} is already present; re-applying {version} and keeping its data.");
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
        // The release pin is recorded after the bundle passed verification and
        // before any service changes, so an unavailable version leaves no pin.
        manifest = await CommitManifestAsync(pendingManifest);
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
                var previousVersion = PreviousVersionFor(state, version, command);
                var next = new InstalledState("studio", "docker", version, previousVersion, port);
                await WritePrivateFileAsync(statePath, JsonSerializer.Serialize(next));
                await FinishManifestAsync(root, manifest is null ? null : manifest with { ReleaseVersion = version }, false, false,
                    InstallationManifest.PhaseComplete, "services-healthy", url,
                    releaseVerifiedThisRun: command != "rollback");
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
        string root, string statePath, InstalledState? state, PendingManifest? pendingManifest,
        ProcessRunner process, ConsolePrompter prompter,
        string? releaseVersion, string? releaseDirectory, string? serverUrl, string? tokenFile)
    {
        var command = parsed.Verb;
        var manifest = pendingManifest?.Manifest;
        var dryRun = parsed.DryRun;
        if (!dryRun) WindowsServiceSetup.RequireAdministrator();
        var layout = WindowsServiceLayout.ForHost(root);
        var services = new WindowsServiceSetup(process, layout, dryRun);
        var profile = plan.Profile;

        if (command == "uninstall")
        {
            var purge = parsed.Has("--purge");
            await services.UninstallAsync(profile, purge);
            await FinishManifestAsync(root, manifest, dryRun, purge, InstallationManifest.PhaseUninstalled, "uninstalled");
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
            if (!dryRun)
                SetupSecrets.RequireProtected(tokenFile, "Studio token file");
        }
        else if (serverUrl is not null || tokenFile is not null)
        {
            throw new ArgumentException("--server-url and --token-file apply to a connector installation only.");
        }

        var version = command == "rollback"
            ? state!.PreviousVersion ?? throw new InvalidOperationException("No previous release is available.")
            : SetupOptions.NormalizeVersion(releaseVersion) ?? ReleaseArtifacts.CurrentVersion();
        if (command == "update" && version == state!.Version
            && manifest?.Phase != InstallationManifest.PhaseUpdating)
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
            manifest = await CommitManifestAsync(pendingManifest);
            await services.ApplyAsync(profile, previous, null, null);
        }
        else
        {
            await using var artifacts = new ReleaseArtifacts(version, releaseDirectory);
            var package = dryRun
                ? Path.Combine(Path.GetTempPath(), $"agent-orchestrator-{version}-win-x64")
                : await artifacts.ExtractWindowsPackageAsync(default);
            manifest = await CommitManifestAsync(pendingManifest);
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
                PreviousVersionFor(state, version, command), 0,
                upstream ?? state?.ServerUrl)));
        }
        var completion = WindowsCompletionCheckpoint(profile, command);
        if (manifest is not null)
            await FinishManifestAsync(root, manifest with { ReleaseVersion = version }, dryRun, false,
                InstallationManifest.PhaseComplete, completion,
                completion == "authority-reachable"
                    ? $"Preflight reached {upstream}; the connector health check passed."
                    : null,
                releaseVerifiedThisRun: command != "rollback");
        services.PrintSummary(profile, upstream ?? state?.ServerUrl);
        return 0;
    }

    /// <summary>
    /// A reconciled manifest that is not written yet. <see cref="Existing"/> is
    /// the manifest on disk when the decision was made.
    /// </summary>
    internal sealed record PendingManifest(string Root, InstallationManifest Manifest,
        InstallationManifest? Existing, bool Write, string Command, string Journey, string Target);

    /// <summary>
    /// Applies <see cref="ManifestPolicy"/>: a re-run keeps the installation id,
    /// recorded principals and data; an interrupted install resumes with the same
    /// release; a different release requires update or rollback. Nothing is
    /// written; <see cref="CommitManifestAsync"/> records the decision once the
    /// release is verified.
    /// </summary>
    internal static async Task<PendingManifest?> DecideManifestAsync(string root, string command,
        ProductPlan plan, InstallationJourney journey, string? version, InstalledState? state, bool dryRun)
    {
        if (command is "preflight" || version is null) return null;
        var onDisk = await ManifestStore.ReadAsync(root);
        var existing = onDisk;
        if (existing is null && state is not null)
        {
            // Installations from before the manifest adopt one without changing their data.
            existing = new InstallationManifest(InstallationManifest.CurrentSchema, NewInstallationId(),
                JourneyPolicy.Name(journey), state.Mode, state.Target, state.Version,
                InstallationManifest.PhaseComplete, [], null, null, null, null, DateTime.UtcNow, DateTime.UtcNow);
        }
        if (command is "uninstall" or "rollback")
        {
            if (existing is not null && (existing.Mode != plan.Mode || existing.Target != plan.Target))
                throw new InvalidOperationException(
                    $"Installation {existing.InstallationId} is --mode {existing.Mode} --target {existing.Target}.");
            if (command == "rollback" && existing?.Phase == InstallationManifest.PhaseUpdating)
                throw new InvalidOperationException(
                    $"Retry the interrupted update to {existing.ReleaseVersion} before rolling back.");
            return existing is null
                ? null
                : new PendingManifest(root, existing, onDisk, false, command, JourneyPolicy.Name(journey), plan.Target);
        }
        var principals = plan.Profile switch
        {
            ProductProfile.ConnectorWindows => new[] { "studio-connector" },
            ProductProfile.Delegated when plan.Mode == "agent-host" => ["runner-coding", "runner-review"],
            ProductProfile.Delegated when plan.Mode == "control-plane" =>
                ["task-server-admin", "orchestrator-engine", "runner-bootstrap"],
            _ => ["task-server-admin", "orchestrator-engine", "studio-bff", "runner-coding", "runner-review"],
        };
        var decision = ManifestPolicy.Decide(existing,
            new ManifestRequest(JourneyPolicy.Name(journey), plan.Mode, plan.Target, version, command, principals),
            NewInstallationId, DateTime.UtcNow);
        if (decision.Action == ManifestAction.Reject)
            throw new InvalidOperationException(decision.Reason);
        if (decision.Action == ManifestAction.Resume && existing!.Phase is
            InstallationManifest.PhaseInstalling or InstallationManifest.PhaseUpdating)
            Console.WriteLine($"Resuming interrupted {existing.Phase} operation for installation {existing.InstallationId} at {existing.ReleaseVersion}.");
        // ReleaseVersion is the requested pin while this operation is in progress.
        // install-state.json remains the observed release until health succeeds.
        var next = decision.Next! with
        {
            Phase = command == "update"
                ? InstallationManifest.PhaseUpdating
                : InstallationManifest.PhaseInstalling,
        };
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] installation manifest: {decision.Action} {next.InstallationId}");
            return null;
        }
        return new PendingManifest(root, next, onDisk, true, command, JourneyPolicy.Name(journey), plan.Target);
    }

    /// <summary>
    /// Writes a pending decision. It refuses when another setup run changed the
    /// manifest after the decision, instead of overwriting that run's state.
    /// </summary>
    internal static async Task<InstallationManifest?> CommitManifestAsync(PendingManifest? pending)
    {
        if (pending is null) return null;
        if (!pending.Write) return pending.Manifest;
        var current = await ManifestStore.ReadAsync(pending.Root);
        if (!SameRevision(current, pending.Existing))
            throw new InvalidOperationException(
                $"The installation manifest at {pending.Root} changed while setup was running. Rerun setup.");
        await ManifestStore.WriteAsync(pending.Root, pending.Manifest);
        if (pending.Command == "install")
            await ManifestStore.CheckpointAsync(pending.Root, pending.Manifest, "preflight", "observed",
                $"The {pending.Journey} preflight passed for --target {pending.Target}.");
        return pending.Manifest;
    }

    internal static async Task<InstallationManifest?> ReconcileManifestAsync(string root, string command,
        ProductPlan plan, InstallationJourney journey, string? version, InstalledState? state, bool dryRun)
        => await CommitManifestAsync(await DecideManifestAsync(root, command, plan, journey, version, state, dryRun));

    private static bool SameRevision(InstallationManifest? left, InstallationManifest? right)
        => left is null || right is null
            ? left is null && right is null
            : (left.InstallationId, left.Phase, left.ReleaseVersion, left.UpdatedUtc)
              == (right.InstallationId, right.Phase, right.ReleaseVersion, right.UpdatedUtc);

    /// <summary>
    /// Only a connector install probed the remote authority in this run; on
    /// update or rollback the connector health check alone shows local health.
    /// </summary>
    internal static string WindowsCompletionCheckpoint(ProductProfile profile, string command)
        => profile == ProductProfile.ConnectorWindows && command == "install"
            ? "authority-reachable"
            : "services-healthy";

    internal static string? PreviousVersionFor(InstalledState? state, string requestedVersion, string command)
        => command == "rollback" ? state?.Version
            : state?.Version == requestedVersion ? state.PreviousVersion
            : state?.Version;

    /// <param name="releaseVerifiedThisRun">False for a rollback, which re-activates a
    /// retained release without verifying artifacts in this run.</param>
    internal static async Task FinishManifestAsync(string root, InstallationManifest? manifest, bool dryRun,
        bool purge, string phase, string checkpoint, string? detail = null, bool releaseVerifiedThisRun = true)
    {
        if (manifest is null || dryRun) return;
        if (purge)
        {
            if (Directory.Exists(root))
            {
                File.Delete(Path.Combine(root, InstallationManifest.FileName));
                File.Delete(Path.Combine(root, InstallationManifest.CheckpointFileName));
            }
            return;
        }
        // Service health is a bootstrap milestone. A one-box installation is
        // accepted only after the I05, I07 and I09 evidence is verified.
        var awaitingAcceptance = phase == InstallationManifest.PhaseComplete
            && manifest.Journey == "one-box" && checkpoint == "services-healthy";
        var next = manifest with
        {
            Phase = awaitingAcceptance ? InstallationManifest.PhaseAwaitingAcceptance : phase,
            UpdatedUtc = DateTime.UtcNow,
        };
        await ManifestStore.WriteAsync(root, next);
        if (phase == InstallationManifest.PhaseComplete)
        {
            await ManifestStore.CheckpointAsync(root, next, "release-verified",
                releaseVerifiedThisRun ? "observed" : "retained",
                releaseVerifiedThisRun
                    ? "The selected package or Compose release passed the installer artifact verification path."
                    : "Rollback re-activated the retained release that was verified when it was first staged.");
            await ManifestStore.CheckpointAsync(root, next, checkpoint, "observed", detail);
            foreach (var pending in new[] { "identity-bootstrapped", "authenticated-canary", "recovery-checkpoint" })
                await ManifestStore.CheckpointAsync(root, next, pending, "not reached",
                    "Observed by 'agent-studio-setup accept' on the authority host; this install run did not verify it.");
            Console.WriteLine($"Installation id: {next.InstallationId} (recorded in {Path.Combine(root, InstallationManifest.FileName)})");
            if (next.Journey == "one-box")
            {
                Console.WriteLine("Installation state: awaiting acceptance; service health is verified.");
                Console.WriteLine("Next: bootstrap the first human session, register the canonical project origin, and enrol the first runner with finite coding and review budgets.");
                Console.WriteLine("Then run 'agent-studio-setup accept --server-url URL --token-file PATH' with the installation's canary configured in " +
                                  $"{AcceptanceJourney.CanaryCommandVariable}. It verifies identity, a full recovery set with its empty-target rehearsal receipt, and the canary, then marks the installation complete.");
            }
        }
        else
            await ManifestStore.CheckpointAsync(root, next, checkpoint, "observed", detail);
    }

    private static async Task UninstallDelegatedAsync(ProductPlan plan, InstallPaths paths, bool dryRun, bool purge)
    {
        var process = new ProcessRunner(dryRun);
        if (plan.Mode == "control-plane" && plan.Target == "docker")
        {
            var composeRoot = Path.Combine(paths.OrchestratorOpt, "compose");
            var args = new List<string> { "compose", "--project-directory", composeRoot,
                "--env-file", Path.Combine(paths.OrchestratorConfig, "docker.env"), "down" };
            if (purge) args.Add("--volumes");
            await process.RequireAsync("docker", args);
            return;
        }
        var units = plan.Mode == "agent-host"
            ? new[] { "agent-host-review.service", "agent-host.service" }
            : plan.Mode == "studio"
                ? new[] { "agent-host-review.service", "agent-host.service",
                    "agent-orchestrator-engine.service", "agent-task-server.service",
                    "agent-task-server-backup.timer", "agent-task-server-backup.service" }
                : new[] { "agent-orchestrator-engine.service", "agent-task-server.service",
                    "agent-task-server-backup.timer", "agent-task-server-backup.service" };
        var systemctl = Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMCTL") ?? "systemctl";
        if (plan.Mode is "agent-host" or "studio"
            && File.Exists(Path.Combine(paths.Systemd, "agent-host-review.service")))
        {
            var active = await process.RunAsync(systemctl, ["is-active", "--quiet", "agent-host-review.service"],
                printOutput: false);
            if (active.ExitCode == 0)
                await process.RequireAsync(Path.Combine(paths.HostOpt, "current", "agent-host"),
                    NativeInstaller.BuildReviewRestartGuardArguments(Path.Combine(paths.HostState, "review-state")));
            var guardPath = NativeInstaller.ResolveReviewRestartGuardPath(
                "review", paths.Systemd, "agent-host-review.service")!;
            if (!dryRun && File.Exists(guardPath)) File.Delete(guardPath);
            await process.RequireAsync(systemctl, ["daemon-reload"]);
        }
        foreach (var unit in units)
        {
            var path = Path.Combine(paths.Systemd, unit);
            if (!File.Exists(path)) continue;
            await process.RequireAsync(systemctl, ["disable", "--now", unit]);
            if (!dryRun) File.Delete(path);
        }
        await process.RequireAsync(systemctl, ["daemon-reload"]);
    }

    private static void PurgeDelegatedPaths(ProductPlan plan, InstallPaths paths)
    {
        var roots = plan.Mode == "agent-host"
            ? new[] { paths.HostOpt, paths.HostState, paths.HostConfig }
            : plan.Mode == "studio"
                ? new[] { paths.OrchestratorOpt, paths.OrchestratorState, paths.OrchestratorConfig,
                    paths.StudioOpt, paths.HostOpt, paths.HostState, paths.HostConfig }
                : new[] { paths.OrchestratorOpt, paths.OrchestratorState, paths.OrchestratorConfig };
        foreach (var root in roots)
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    internal static InstallPaths ResolveDelegatedPaths(ProductPlan plan, string? installDirectory)
    {
        var paths = InstallPaths.Load();
        if (installDirectory is null) return paths;
        var configRoot = Path.GetFullPath(installDirectory);
        return plan.Mode == "agent-host"
            ? paths with { HostConfig = configRoot }
            : paths with { OrchestratorConfig = configRoot };
    }

    private static string NewInstallationId() => $"inst_{Guid.NewGuid():N}";

    /// <summary>A readable join token is decoded before preflight; a malformed one fails here.</summary>
    internal static JoinPayload? ReadJoinToken(string? joinTokenFile)
        => joinTokenFile is not null && File.Exists(joinTokenFile)
            ? JoinTokenCodec.Decode(File.ReadAllText(joinTokenFile))
            : null;

    /// <summary>True when the run connects to an authority that already exists.</summary>
    internal static bool ContactsAuthority(string mode, bool relocation)
        => mode is "agent-host" or "connector" || relocation;

    /// <summary>
    /// The existing authority this run will contact, which preflight must probe.
    /// A host join uses the URL inside its join token; a fresh one-box or
    /// control-plane install creates its authority and has none.
    /// </summary>
    internal static string? ResolveAuthorityUrl(string mode, bool relocation, string? serverUrl, JoinPayload? join)
    {
        if (mode != "agent-host")
            return ContactsAuthority(mode, relocation) ? serverUrl : null;
        if (join is null) return null;
        if (serverUrl is not null && !string.Equals(serverUrl.TrimEnd('/'), join.ServerUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"The join token names authority {join.ServerUrl}, but --server-url is {serverUrl}. Remove --server-url or request a join token from that authority.");
        return join.ServerUrl;
    }

    /// <summary>The setup identity file of this host: a Studio root or the Linux authority configuration.</summary>
    private static string LocateManifestRoot(string? installDirectory)
    {
        var candidates = installDirectory is not null
            ? [Path.GetFullPath(installDirectory)]
            : new[]
            {
                DefaultInstallRoot(ProductProfile.StudioDocker),
                DefaultInstallRoot(ProductProfile.StudioWindowsServices),
                InstallPaths.Load().OrchestratorConfig,
            }.Distinct().ToArray();
        return candidates.FirstOrDefault(candidate =>
                   File.Exists(Path.Combine(candidate, InstallationManifest.FileName)))
               ?? throw new InvalidOperationException(
                   $"No installation manifest found at {string.Join(" or ", candidates)}. Pass --install-dir.");
    }

    private static string? PassthroughValue(ProductCommand command, string option)
    {
        var index = command.Passthrough.ToList().IndexOf(option);
        return index >= 0 && index + 1 < command.Passthrough.Count ? command.Passthrough[index + 1] : null;
    }

    private static void PrintJourney(InstallationJourney journey)
    {
        var steps = JourneyPolicy.Steps(journey);
        Console.WriteLine($"Journey: {JourneyPolicy.Name(journey)} (--mode {steps.Mode})");
        foreach (var fact in steps.Facts)
            Console.WriteLine($"  {fact.Name}: {fact.Meaning}");
        Console.WriteLine($"  Checkpoints: {string.Join(" -> ", steps.Checkpoints)}");
    }

    private static bool PrintPreflight(InstallationJourney journey, string target, IReadOnlyList<PreflightFinding> findings)
    {
        Console.WriteLine("Preflight:");
        foreach (var finding in findings)
        {
            var marker = finding.Status switch
            {
                PreflightStatus.Pass => "ok  ",
                PreflightStatus.Fail => "FAIL",
                _ => "n/a ",
            };
            Console.WriteLine($"  [{marker}] {finding.Check}: {finding.Observed}");
            if (finding.Recovery is not null)
                Console.WriteLine($"         Recovery: {finding.Recovery}");
        }
        if (PreflightPolicy.ExecutionPlatformNote(journey, OperatingSystem.IsWindows(), target) is { } note)
            Console.WriteLine($"  Note: {note}");
        return findings.Any(finding => finding.Status == PreflightStatus.Fail);
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
              agent-studio-setup preflight [--journey NAME] [--server-url URL]
              agent-studio-setup accept --server-url URL --token-file PATH
                                 [--recovery-checkpoint DIR] [--backup-path PATH]

            Journeys (--journey):
              one-box             Install the whole system on one machine; a
                                  workstation is a placement of this journey.
              join-host           Join a runner host to an existing Task Server.
              attach-studio       Attach a Studio edge to a remote Task Server.
              relocate-authority  Move Task Server and engine to an always-on box.
                                  Requires --recovery-checkpoint DIR,
                                  --source-manifest PATH and --authority-frozen
                                  (gated migration).

            Acceptance (accept): on a one-box or relocated authority, checks the
            service identities and an enrolled runner, creates and verifies a full
            recovery set (or verifies --recovery-checkpoint DIR with its empty-target
            rehearsal receipt), then runs the canary in
            AGENT_ORCHESTRATOR_CANARY_COMMAND. Exit 0 marks the installation
            complete; exit 2 names the pending step.

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
              --server-url URL          Task Server URL (connector, control-plane, accept);
                                        join-host reads it from the join token
              --token-file PATH         Owner-only token file: Studio token (connector) or
                                        management token (relocate-authority, accept)
              --answer-file PATH        JSON answers for unattended installation
              --unattended              Use defaults without prompts
              --dry-run                 Check prerequisites and print planned operations

            The Docker path runs without elevation. Docker Desktop on Windows and
            macOS may require a paid subscription for larger companies; Docker
            Engine on Linux does not. Use --target native on hosts without Docker.
            Re-running install keeps the installation id, principals and data.
            Secrets are read only from owner-only files, never from arguments.
            Uninstall keeps data unless --purge is given.
            """);
    }
}
