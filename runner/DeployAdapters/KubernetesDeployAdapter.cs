namespace AgentRunner.DeployAdapters;

/// <summary>Uses a provider-issued kubeconfig for one existing namespace.</summary>
public sealed class KubernetesDeployAdapter(IDeploySecretResolver secrets, IDeployCommandRunner commands)
    : IDeployTargetAdapter
{
    public string Kind => "kubernetes";

    public async Task DeployAsync(DeployTargetRequest request, CancellationToken ct)
    {
        DeployArtifactVerifier.Verify(request);
        if (request.Step.Step != "start-container")
            throw new InvalidDataException("Kubernetes target needs a start-container step.");
        var ns = DeployTargetValidation.Identifier(request.Target.Namespace);
        var image = DeployTargetValidation.Image(request.Artifact);
        var resource = DeployTargetValidation.Identifier(request.Target.ImageResource);
        if (request.PackagePath is null || request.PackageArtifact is null)
            throw new InvalidDataException("Kubernetes needs a manifest or chart package in the release snapshot.");
        using var temp = new DeployTempDirectory();
        var extracted = await VerifiedPackage.ExtractAsync(request, temp, ct);
        var content = VerifiedPackage.RelativePath(extracted, request.Target.ManifestPath);
        var kubeconfig = await temp.SecretFileAsync("kubeconfig",
            await secrets.ResolveAsync(request.Target.CredentialSecret, ct), ct);
        if (request.Target.HelmRelease is { } release)
        {
            // Render once and apply exactly the verified bytes; a second render by helm upgrade could differ.
            release = DeployTargetValidation.Identifier(release);
            var rendered = await commands.RunAsync("helm", ["--kubeconfig", kubeconfig, "--namespace", ns,
                "template", release, content, "--set-string", "image.ref=" + image], null, ct);
            if (rendered.Output.Contains("__AGENT_STUDIO_IMAGE__", StringComparison.Ordinal))
                throw new InvalidDataException("Helm chart left an image placeholder unresolved.");
            var renderedPath = Path.Combine(temp.Path, "rendered.yaml");
            await File.WriteAllTextAsync(renderedPath, rendered.Output, ct);
            await ApplyVerifiedAsync(kubeconfig, ns, renderedPath, resource, image, ct);
            return;
        }

        // kubectl reads the same files recursively, so every replaced manifest is also applied.
        var manifestFiles = (Directory.Exists(content)
            ? Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories)
            : [content]).Where(IsYaml).ToArray();
        if (manifestFiles.Length == 0)
            throw new InvalidDataException("Manifest package needs YAML files.");
        if (Directory.Exists(content) && Directory.EnumerateFiles(content, "*.json", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("Manifest package must not mix JSON manifests with YAML.");
        var replaced = 0;
        foreach (var file in manifestFiles)
        {
            var source = await File.ReadAllTextAsync(file, ct);
            if (!source.Contains("__AGENT_STUDIO_IMAGE__", StringComparison.Ordinal)) continue;
            await File.WriteAllTextAsync(file, source.Replace("__AGENT_STUDIO_IMAGE__", image, StringComparison.Ordinal), ct);
            replaced++;
        }
        if (replaced == 0) throw new InvalidDataException("Manifest did not reference the release image.");
        await ApplyVerifiedAsync(kubeconfig, ns, content, resource, image, ct);
    }

    private async Task ApplyVerifiedAsync(string kubeconfig, string ns, string source, string resource,
        string image, CancellationToken ct)
    {
        await KubernetesManifestVerifier.VerifyAsync(commands, kubeconfig, ns, source, resource, image, ct);
        await commands.RunAsync("kubectl", ["--kubeconfig", kubeconfig, "--namespace", ns,
            "apply", "--recursive", "-f", source], null, ct);
        await commands.RunAsync("kubectl", ["--kubeconfig", kubeconfig, "--namespace", ns,
            "rollout", "status", "deployment/" + resource, "--timeout=300s"], null, ct);
    }

    private static bool IsYaml(string file) =>
        file.EndsWith(".yaml", StringComparison.Ordinal) || file.EndsWith(".yml", StringComparison.Ordinal);
}
