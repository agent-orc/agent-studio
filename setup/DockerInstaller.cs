using System.Security.Cryptography;

namespace AgentStudio.Setup;

internal sealed record DockerControlPlaneResult(
    string ServerUrl,
    string Credential,
    string ReleaseVersion);

// Docker-target sibling of NativeInstaller.InstallControlPlaneAsync. It does
// not install Agent Studio's static browser bundle: the target topology
// never serves Angular from the control plane
// (docs/operations/remote-task-server-local-studio.md, "Studio location").
internal sealed class DockerInstaller(
    InstallPaths paths,
    ProcessRunner processes,
    bool dryRun)
{
    public async Task<DockerControlPlaneResult> InstallControlPlaneAsync(
        string orchestratorRelease,
        string wgAddress,
        string controlPlaneDomain,
        string offhostBackupPath,
        string runnerId,
        string version,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Installing the Docker control plane");
        var studioCredential = RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var engineCredential = RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var runnerCredential = RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var hostVisibleServerUrl = $"https://{controlPlaneDomain}";

        var environment = new Dictionary<string, string?>
        {
            ["NONINTERACTIVE"] = "1",
            ["CONTROL_PLANE_VERSION"] = version,
            ["CONTROL_PLANE_RUNNER_ID"] = runnerId,
            ["WG_ADDRESS"] = wgAddress,
            ["CONTROL_PLANE_DOMAIN"] = controlPlaneDomain,
            ["CONTROL_PLANE_OFFHOST_BACKUP_PATH"] = offhostBackupPath,
            ["STUDIO_AUTH_TOKEN"] = studioCredential,
            ["ENGINE_AUTH_TOKEN"] = engineCredential,
            ["RUNNER_AUTH_TOKEN"] = runnerCredential,
            ["AGENT_ORCHESTRATOR_CONFIG_ROOT"] = paths.OrchestratorConfig,
            ["AGENT_ORCHESTRATOR_COMPOSE_ROOT"] = Path.Combine(paths.OrchestratorOpt, "compose"),
        };
        if (Environment.GetEnvironmentVariable("AGENT_SETUP_SKIP_ROOT_CHECK") == "1")
        {
            environment["AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK"] = "1";
            environment["AGENT_ORCHESTRATOR_SKIP_USER_CREATE"] = "1";
        }

        var composeSource = Path.Combine(orchestratorRelease, "compose", "control-plane");
        var arguments = new List<string> { Path.Combine(orchestratorRelease, "install-docker.sh") };
        if (Directory.Exists(composeSource))
            arguments.AddRange(["--compose-source", composeSource]);

        if (dryRun)
        {
            Console.WriteLine($"[dry-run] install-docker.sh {string.Join(' ', arguments.Skip(1))}");
        }
        else
        {
            await processes.RequireAsync(
                "/bin/sh",
                arguments,
                environment,
                cancellationToken: cancellationToken);
        }

        Console.WriteLine($"  [ok] Task Server, Orchestrator Engine, backup, and edge: {hostVisibleServerUrl}");
        Console.WriteLine(
            "  WireGuard, the host firewall, and DNS are separate steps. See deploy/compose/control-plane/wireguard/ and network/.");

        return new DockerControlPlaneResult(hostVisibleServerUrl, runnerCredential, version);
    }
}
