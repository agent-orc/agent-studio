using System.Text.Json;

namespace AgentRunner.DeployAdapters;

/// <summary>Deploys a verified static bundle or pinned compose image to an existing VM.</summary>
public sealed class SshDockerDeployAdapter(IDeploySecretResolver secrets, IDeployCommandRunner commands) : IDeployTargetAdapter
{
    public string Kind => "ssh-docker";

    public async Task DeployAsync(DeployTargetRequest request, CancellationToken ct)
    {
        DeployArtifactVerifier.Verify(request);
        var host = DeployTargetValidation.Host(request.Target.Host);
        var user = DeployTargetValidation.Identifier(request.Target.User);
        if (string.IsNullOrWhiteSpace(request.Target.KnownHostsSecret))
            throw new InvalidDataException("SSH target needs a known-hosts secret.");
        using var temp = new DeployTempDirectory();
        string? local = null;
        string? destination = null;
        if (request.Step.Step == "static-site")
        {
            destination = DeployTargetValidation.RemotePath(request.Step.Directory);
            local = await VerifiedPackage.ExtractAsync(request, temp, ct);
        }
        else if (request.Step.Step == "container")
        {
            DeployTargetValidation.RemotePath(request.Step.ComposeFile);
            DeployTargetValidation.Identifier(request.Step.Service);
            DeployTargetValidation.Image(request.Artifact);
        }
        else throw new InvalidDataException("SSH target does not support this step.");
        var key = await temp.SecretFileAsync("identity", await secrets.ResolveAsync(request.Target.CredentialSecret, ct), ct);
        var knownHosts = await temp.SecretFileAsync("known_hosts",
            await secrets.ResolveAsync(request.Target.KnownHostsSecret, ct), ct);
        var sshOptions = new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "IdentitiesOnly=yes", "-o", "UserKnownHostsFile=" + knownHosts, "-i", key };
        var remote = user + "@" + host;
        if (local is not null)
        {
            var digest = DeployArtifactVerifier.Verify(request);
            var releaseDirectory = destination + "/.releases/" + digest;
            await Run("ssh", [.. sshOptions, remote, "mkdir -p " + releaseDirectory], ct);
            await Run("scp", [.. sshOptions, "-r", local + "/.", remote + ":" + releaseDirectory + "/"], ct);
            var pending = destination + "/.current-" + digest;
            await Run("ssh", [.. sshOptions, remote,
                "ln -sfn " + releaseDirectory + " " + pending + " && mv -Tf " + pending + " " + destination + "/current"], ct);
            return;
        }

        var compose = DeployTargetValidation.RemotePath(request.Step.ComposeFile);
        var service = DeployTargetValidation.Identifier(request.Step.Service);
        var image = DeployTargetValidation.Image(request.Artifact);
        // The operator-owned compose service must use image: ${IMAGE}. Check its
        // resolved image before making any change to the running service.
        var prefix = "IMAGE=" + image + " docker compose -f " + compose;
        var configured = await Run("ssh", [.. sshOptions, remote, prefix + " config --format json"], ct);
        using var document = JsonDocument.Parse(configured.Output);
        if (!document.RootElement.TryGetProperty("services", out var services)
            || !services.TryGetProperty(service, out var selected)
            || !selected.TryGetProperty("image", out var configuredImage)
            || configuredImage.GetString() != image)
            throw new InvalidDataException("Compose service does not resolve to the release image.");
        await Run("ssh", [.. sshOptions, remote, prefix + " pull " + service], ct);
        await Run("ssh", [.. sshOptions, remote, prefix + " up -d --no-deps " + service], ct);
    }

    private Task<DeployCommandResult> Run(string executable, IReadOnlyList<string> args, CancellationToken ct)
        => commands.RunAsync(executable, args, null, ct);
}
