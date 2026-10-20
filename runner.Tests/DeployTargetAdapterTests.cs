using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentRunner.DeployAdapters;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployTargetAdapterTests
{
    private const string Image = "registry.example.test/team/web@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task SnapshotRefusal_PrecedesCredentialResolutionAndCommands()
    {
        var secrets = new FakeSecrets();
        var commands = new FakeCommands();
        var request = Container("ssh-docker") with { ReleaseArtifacts = [] };
        var executor = Executor(secrets, commands);

        await Assert.ThrowsAsync<InvalidDataException>(() => executor.DeployAsync(request, CancellationToken.None));

        Assert.Empty(secrets.References);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task StepScope_RefusesAnotherEnvironmentBeforeTargetAccess()
    {
        var secrets = new FakeSecrets();
        var commands = new FakeCommands();
        var request = Container("ssh-docker") with
        {
            Environment = "production",
            Step = Step("container") with
            {
                ComposeFile = "/srv/apps/web/compose.yml", Service = "web",
                Environments = ["staging"],
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Executor(secrets, commands).DeployAsync(request, CancellationToken.None));
        Assert.Empty(secrets.References);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task WorkloadResolver_SendsClaimAndPurposeToSecretProvider()
    {
        var value = "fixture-" + Guid.NewGuid().ToString("N");
        var handler = new SecretHandler(value);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://studio.example.test/") };
        var resolver = new DeployWorkloadSecretResolver(client, "demo", "staging", "job-1", "lease-1");

        Assert.Equal(value, await resolver.ResolveAsync("secret://demo/staging/SSH_KEY", CancellationToken.None));
        Assert.Equal("/api/runner/secrets/resolve", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("deploy:staging", body.RootElement.GetProperty("purpose").GetString());
        Assert.Equal("job-1", body.RootElement.GetProperty("workload").GetProperty("jobId").GetString());
        Assert.Equal("lease-1", body.RootElement.GetProperty("workload").GetProperty("leaseToken").GetString());
    }

    [Fact]
    public async Task SshContainer_ChecksComposeImageBeforePullAndRestart()
    {
        var secrets = new FakeSecrets();
        var commands = new FakeCommands { Output = Compose(Image) };
        await Executor(secrets, commands).DeployAsync(Container("ssh-docker"), CancellationToken.None);

        Assert.Equal(3, commands.Calls.Count);
        Assert.All(commands.Calls, call => Assert.Equal("ssh", call.Executable));
        Assert.Contains("StrictHostKeyChecking=yes", commands.Calls[0].Arguments);
        Assert.Contains("config --format json", commands.Calls[0].Arguments.Last());
        Assert.Contains("pull web", commands.Calls[1].Arguments.Last());
        Assert.Contains("up -d --no-deps web", commands.Calls[2].Arguments.Last());
        Assert.Equal(2, secrets.References.Count);
    }

    [Fact]
    public async Task SshContainer_RefusesComposeThatIgnoresPinnedImage()
    {
        var commands = new FakeCommands { Output = Compose("registry.example.test/other@sha256:"
            + new string('b', 64)) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Executor(new FakeSecrets(), commands).DeployAsync(Container("ssh-docker"), CancellationToken.None));
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task SshStatic_VerifiesPackageAndPublishesDigestDirectory()
    {
        var (path, reference) = Package("index.html", "verified");
        try
        {
            var request = new DeployTargetRequest("demo", "staging", reference, [reference],
                Step("static-site") with { Directory = "/srv/www/demo" },
                new DeployTarget("ssh-docker", "web-vm", "secret://demo/staging/SSH_KEY",
                    Host: "web.example.test", User: "deploy",
                    KnownHostsSecret: "secret://demo/staging/SSH_HOSTS"), path);
            var commands = new FakeCommands();
            await Executor(new FakeSecrets(), commands).DeployAsync(request, CancellationToken.None);

            Assert.Equal(new[] { "ssh", "scp", "ssh" }, commands.Calls.Select(call => call.Executable));
            Assert.Contains("mv -Tf", commands.Calls[2].Arguments.Last());
            Assert.Contains(reference[^64..], commands.Calls[1].Arguments.Last());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TamperedPackage_IsRejectedBeforeSecretResolution()
    {
        var (path, reference) = Package("index.html", "verified");
        try
        {
            await File.AppendAllTextAsync(path, "tampered", CancellationToken.None);
            var secrets = new FakeSecrets();
            var request = new DeployTargetRequest("demo", "staging", reference, [reference],
                Step("static-site") with { Directory = "/srv/www/demo" },
                new DeployTarget("ssh-docker", "web-vm", "secret://demo/staging/SSH_KEY",
                    Host: "web.example.test", User: "deploy",
                    KnownHostsSecret: "secret://demo/staging/SSH_HOSTS"), path);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(secrets, new FakeCommands()).DeployAsync(request, CancellationToken.None));
            Assert.Empty(secrets.References);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_ApplyAndPinImageInExistingNamespace()
    {
        var (path, package) = Package("deploy/app.yaml",
            "kind: Deployment\nspec:\n  template:\n    spec:\n      containers:\n      - image: __AGENT_STUDIO_IMAGE__\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "deploy/app.yaml", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var calls = new FakeCommands();
            await Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None);
            Assert.Equal(2, calls.Calls.Count);
            Assert.Contains("apply", calls.Calls[0].Arguments);
            Assert.Contains("rollout", calls.Calls[1].Arguments);
            Assert.All(calls.Calls, call => Assert.Contains("staging", call.Arguments));
            Assert.Contains(Image, calls.ManifestContents);
            Assert.DoesNotContain("__AGENT_STUDIO_IMAGE__", calls.ManifestContents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_HelmRendersPinnedImageBeforeUpgrade()
    {
        var (path, package) = Package("chart/Chart.yaml", "name: web\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "chart", HelmRelease: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var calls = new FakeCommands { Output = "image: " + Image + "\n" };
            await Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None);
            Assert.Equal(new[] { "template", "upgrade" }, calls.Calls.Select(call => call.Arguments
                .First(argument => argument is "template" or "upgrade")));
            Assert.Contains("--atomic", calls.Calls[1].Arguments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_RefusesManifestWithoutReleaseImage()
    {
        var (path, package) = Package("deploy/app.yaml", "kind: Deployment\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "deploy/app.yaml", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var calls = new FakeCommands();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None));
            Assert.Empty(calls.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CloudRun_UsesIsolatedProviderCredentialAndPinnedImage()
    {
        var commands = new FakeCommands();
        var request = Container("cloud-run") with
        {
            Target = new DeployTarget("cloud-run", "web-vm", "secret://demo/staging/CLOUD_KEY",
                Project: "demo-prod", Region: "europe-west3", CloudService: "web",
                IdentityPoolProjectNumber: "123456789", IdentityPool: "deploy-pool",
                IdentityProvider: "studio", CloudServiceAccount: "deploy@demo-prod.iam.gserviceaccount.com"),
        };
        await Executor(new FakeSecrets(), commands).DeployAsync(request, CancellationToken.None);

        Assert.Equal(2, commands.Calls.Count);
        Assert.Equal("gcloud", commands.Calls[0].Executable);
        Assert.Contains("--cred-file", commands.Calls[0].Arguments);
        Assert.Contains(Image, commands.Calls[1].Arguments);
        Assert.Contains("--no-allow-unauthenticated", commands.Calls[1].Arguments);
        Assert.NotNull(commands.Calls[0].Environment?["CLOUDSDK_CONFIG"]);
        using var credential = JsonDocument.Parse(commands.CredentialJson!);
        Assert.Equal("external_account", credential.RootElement.GetProperty("type").GetString());
        Assert.False(credential.RootElement.TryGetProperty("private_key", out _));
        Assert.False(Directory.Exists(commands.Calls[0].Environment!["CLOUDSDK_CONFIG"]));
    }

    private static DeployTargetExecutor Executor(FakeSecrets secrets, FakeCommands commands)
        => new([
            new SshDockerDeployAdapter(secrets, commands),
            new KubernetesDeployAdapter(secrets, commands),
            new CloudRunDeployAdapter(secrets, commands),
        ]);

    private static DeployTargetRequest Container(string kind)
        => new("demo", "staging", Image, [Image],
            kind == "ssh-docker"
                ? Step("container") with { ComposeFile = "/srv/apps/web/compose.yml", Service = "web" }
                : Step("start-container"),
            new DeployTarget(kind, "web-vm", "secret://demo/staging/SSH_KEY",
                Host: "web.example.test", User: "deploy",
                KnownHostsSecret: "secret://demo/staging/SSH_HOSTS"));

    private static ProjectDeployStep Step(string kind)
        => new(kind, "web", "web-vm", null, null, null, [], []);

    private static string Compose(string image)
        => "{\"services\":{\"web\":{\"image\":\"" + image + "\"}}}";

    private static (string Path, string Reference) Package(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "deploy-package-" + Guid.NewGuid().ToString("N"));
        using (var stream = File.Create(path))
        using (var writer = new TarWriter(stream))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            };
            writer.WriteEntry(entry);
        }
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        return (path, "package@sha256:" + digest);
    }

    private sealed class FakeSecrets : IDeploySecretResolver
    {
        public List<string> References { get; } = [];
        public Task<string> ResolveAsync(string reference, CancellationToken ct)
        {
            References.Add(reference);
            return Task.FromResult("fake-" + Guid.NewGuid().ToString("N"));
        }
    }

    private sealed class FakeCommands : IDeployCommandRunner
    {
        public string Output { get; init; } = "";
        public string? CredentialJson { get; private set; }
        public string? ManifestContents { get; private set; }
        public List<(string Executable, IReadOnlyList<string> Arguments,
            IReadOnlyDictionary<string, string>? Environment)> Calls { get; } = [];

        public Task<DeployCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
        {
            Calls.Add((executable, arguments.ToArray(), environment));
            if (executable == "gcloud" && arguments.Contains("--cred-file"))
                CredentialJson = File.ReadAllText(arguments.Last());
            if (executable == "kubectl" && arguments.Contains("apply"))
                ManifestContents = File.ReadAllText(arguments.Last());
            return Task.FromResult(new DeployCommandResult(0, Output));
        }
    }

    private sealed class SecretHandler(string value) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value })),
            };
        }
    }
}
