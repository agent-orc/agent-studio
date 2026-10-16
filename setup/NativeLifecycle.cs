namespace AgentStudio.Setup;

// Product command entry point for retained, checksum-verified native releases.
// The orchestrator's drain and readiness switch remains owned by its release
// scripts; setup owns the version selection, manifest and Studio/host services.
internal static class NativeLifecycle
{
    internal static string PreviousVersion(ProductPlan plan, InstallPaths paths)
    {
        var root = plan.Mode == "agent-host" ? paths.HostOpt : paths.OrchestratorOpt;
        var previous = Path.Combine(root, "previous");
        var target = Directory.ResolveLinkTarget(previous, returnFinalTarget: true);
        if (target is null || !target.Exists)
            throw new InvalidOperationException("No retained previous native release is available.");
        return Path.GetFileName(target.FullName);
    }

    internal static async Task RunAsync(ProductPlan plan, InstallPaths paths, string command,
        string version, string? releaseDirectory, ProductSetup.PendingManifest? pending,
        bool dryRun, CancellationToken cancellationToken = default)
    {
        if (plan.Target != "native" || command is not ("update" or "rollback"))
            throw new ArgumentException("Native lifecycle requires update or rollback on a native profile.");
        if (!dryRun && Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") != "1"
            && Environment.UserName != "root")
            throw new InvalidOperationException("Native update and rollback must run as root. Rerun with sudo.");
        var process = new ProcessRunner(dryRun);
        var native = new NativeInstaller(paths, process, dryRun);
        string? orchestrator = null;
        string? studio = null;
        string? host = null;
        await using var artifacts = command == "update"
            ? new ReleaseArtifacts(version, releaseDirectory)
            : null;
        if (artifacts is not null)
        {
            if (plan.Mode != "agent-host")
            {
                orchestrator = await artifacts.ExtractOrchestratorAsync(cancellationToken);
                studio = await artifacts.ExtractStudioAsync(cancellationToken);
                NativeInstaller.ValidateStudioRelease(orchestrator, studio);
            }
            if (plan.Mode is "studio" or "agent-host")
            {
                host = await artifacts.ExtractHostAsync(cancellationToken);
                NativeInstaller.ValidateHostRelease(host);
            }
        }
        // No state changes precede verification of every artifact this profile needs.
        var manifest = await ProductSetup.CommitManifestAsync(pending);
        if (plan.Mode != "agent-host")
        {
            var script = Path.Combine(paths.OrchestratorOpt, "current",
                command == "update" ? "update.sh" : "rollback.sh");
            var environment = new Dictionary<string, string?>
            {
                ["AGENT_ORCHESTRATOR_OPT_ROOT"] = paths.OrchestratorOpt,
                ["AGENT_ORCHESTRATOR_CONFIG_ROOT"] = paths.OrchestratorConfig,
                ["AGENT_ORCHESTRATOR_STATE_ROOT"] = paths.OrchestratorState,
                ["AGENT_ORCHESTRATOR_SYSTEMD_ROOT"] = paths.Systemd,
            };
            if (Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1")
                environment["AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK"] = "1";
            if (Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMCTL") is { Length: > 0 } systemctl)
                environment["SYSTEMCTL_BIN"] = systemctl;
            await process.RequireAsync("/bin/sh", command == "update"
                    ? [script, orchestrator!]
                    : [script], environment, cancellationToken: cancellationToken);
            if (command == "update")
                await native.InstallStudioAsync(studio!, version, cancellationToken);
            else
                await process.RequireAsync("ln", ["-sfnT", Path.Combine(paths.StudioOpt, version),
                    Path.Combine(paths.StudioOpt, "current")], cancellationToken: cancellationToken);
            var serverEnv = Path.Combine(paths.OrchestratorConfig, "server.env");
            var listenUrl = File.ReadLines(serverEnv).FirstOrDefault(line => line.StartsWith("LISTEN_URL=", StringComparison.Ordinal))
                ?["LISTEN_URL=".Length..]
                ?? throw new InvalidOperationException($"{serverEnv} has no LISTEN_URL.");
            await native.InstallStudioServicesAsync(listenUrl, cancellationToken, restartExisting: true);
        }
        if (plan.Mode is "studio" or "agent-host")
        {
            var currentLink = Path.Combine(paths.HostOpt, "current");
            var oldTarget = Directory.ResolveLinkTarget(currentLink, returnFinalTarget: true)?.FullName;
            if (oldTarget is null && !dryRun)
                throw new InvalidOperationException("No active agent-host release exists.");
            if (command == "update")
            {
                var target = Path.Combine(paths.HostOpt, version);
                await process.RequireAsync("install", ["-d", "-m", "0755", target],
                    cancellationToken: cancellationToken);
                await process.RequireAsync("install", ["-m", "0755",
                    Path.Combine(host!, "linux-x64", "agent-host"), Path.Combine(target, "agent-host")],
                    cancellationToken: cancellationToken);
                await process.RequireAsync("install", ["-m", "0755",
                    Path.Combine(host!, "agent-host-resource-governance.sh"),
                    Path.Combine(target, "agent-host-resource-governance")],
                    cancellationToken: cancellationToken);
                await process.RequireAsync("install", ["-m", "0755",
                    Path.Combine(host!, "agent-studio-setup"),
                    Path.Combine(target, "agent-studio-setup")],
                    cancellationToken: cancellationToken);
            }
            var targetToActivate = Path.Combine(paths.HostOpt, version);
            await ActivateHostReleaseAsync(process, currentLink, oldTarget, targetToActivate,
                async () =>
                {
                    if (plan.Mode == "agent-host")
                        await native.LinkHostSetupCommandAsync(cancellationToken);
                    if (Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_SYSTEMD") == "1") return;
                    var systemctl = Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMCTL") ?? "systemctl";
                    // Refuse an active Review replacement before restarting the coding unit.
                    foreach (var name in new[] { "agent-host-review.service", "agent-host.service" })
                    {
                        if (!File.Exists(Path.Combine(paths.Systemd, name))) continue;
                        if (name == "agent-host-review.service" && !dryRun)
                            await native.ReplaceReviewServiceAsync(systemctl, name,
                                Path.Combine(paths.HostOpt, "current", "agent-host"),
                                Path.Combine(paths.HostState, "review-state"), cancellationToken);
                        else
                            await process.RequireAsync(systemctl, ["restart", name],
                                cancellationToken: cancellationToken);
                        await process.RequireAsync(systemctl, ["is-active", "--quiet", name],
                            cancellationToken: cancellationToken);
                    }
                }, cancellationToken);
        }
        await ProductSetup.FinishManifestAsync(
            plan.Mode == "agent-host" ? paths.HostConfig : paths.OrchestratorConfig,
            manifest is null ? null : manifest with { ReleaseVersion = version },
            dryRun, purge: false, InstallationManifest.PhaseComplete,
            plan.Mode == "agent-host" ? "host-enrolled" : "services-healthy",
            releaseVerifiedThisRun: command != "rollback");
        Console.WriteLine($"Native {plan.Mode} {command} is complete at release {version}.");
    }

    internal static async Task ActivateHostReleaseAsync(ProcessRunner process, string currentLink,
        string? oldTarget, string targetToActivate, Func<Task> restartServices,
        CancellationToken cancellationToken = default)
    {
        var changed = !string.Equals(oldTarget, targetToActivate, StringComparison.Ordinal);
        if (changed)
            await process.RequireAsync("ln", ["-sfnT", targetToActivate, currentLink],
                cancellationToken: cancellationToken);
        try
        {
            await restartServices();
            if (changed && oldTarget is not null)
                await process.RequireAsync("ln", ["-sfnT", oldTarget,
                    Path.Combine(Path.GetDirectoryName(currentLink)!, "previous")],
                    cancellationToken: cancellationToken);
        }
        catch
        {
            if (changed && oldTarget is not null)
                await process.RequireAsync("ln", ["-sfnT", oldTarget, currentLink],
                    cancellationToken: CancellationToken.None);
            throw;
        }
    }
}
