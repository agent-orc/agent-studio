using System.Formats.Tar;
using System.IO.Compression;
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
    private static readonly IReadOnlyDictionary<string, string> ImageBinding = new Dictionary<string, string>
    {
        ["web"] = Image,
    };

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
    public async Task StepArtifact_RejectsAnotherArtifactInTheSameSnapshot()
    {
        var other = "registry.example.test/team/other@sha256:" + new string('b', 64);
        var secrets = new FakeSecrets();
        var commands = new FakeCommands();
        var request = Container("ssh-docker") with
        {
            Artifact = other,
            ReleaseArtifacts = [Image, other],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Executor(secrets, commands).DeployAsync(request, CancellationToken.None));
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
                new Dictionary<string, string> { ["web"] = reference },
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
    public async Task SshStatic_RedeployUsesFreshDirectoryForTheSameArtifact()
    {
        var (path, reference) = Package("index.html", "verified");
        try
        {
            var request = new DeployTargetRequest("demo", "staging", reference, [reference],
                new Dictionary<string, string> { ["web"] = reference },
                Step("static-site") with { Directory = "/srv/www/demo" },
                new DeployTarget("ssh-docker", "web-vm", "secret://demo/staging/SSH_KEY",
                    Host: "web.example.test", User: "deploy",
                    KnownHostsSecret: "secret://demo/staging/SSH_HOSTS"), path);
            var commands = new FakeCommands();
            var executor = Executor(new FakeSecrets(), commands);

            await executor.DeployAsync(request, CancellationToken.None);
            await executor.DeployAsync(request, CancellationToken.None);

            Assert.Equal(6, commands.Calls.Count);
            var firstCreate = commands.Calls[0].Arguments.Last();
            var secondCreate = commands.Calls[3].Arguments.Last();
            Assert.Contains("&& mkdir -m 755 ", firstCreate);
            Assert.Contains("&& mkdir -m 755 ", secondCreate);
            var firstRelease = firstCreate[(firstCreate.LastIndexOf(' ') + 1)..];
            var secondRelease = secondCreate[(secondCreate.LastIndexOf(' ') + 1)..];
            Assert.StartsWith("/srv/www/demo/.releases/" + reference[^64..] + "-", firstRelease);
            Assert.StartsWith("/srv/www/demo/.releases/" + reference[^64..] + "-", secondRelease);
            Assert.NotEqual(firstRelease, secondRelease);
            Assert.EndsWith(secondRelease + "/", commands.Calls[4].Arguments.Last());
            Assert.Contains("ln -sfn " + secondRelease + " ", commands.Calls[5].Arguments.Last());
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
                new Dictionary<string, string> { ["web"] = reference },
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
            Assert.Equal(3, calls.Calls.Count);
            Assert.Contains("--dry-run=client", calls.Calls[0].Arguments);
            Assert.Contains("apply", calls.Calls[1].Arguments);
            Assert.Contains("rollout", calls.Calls[2].Arguments);
            Assert.All(calls.Calls, call => Assert.Contains("staging", call.Arguments));
            Assert.Contains(Image, calls.ManifestContents);
            Assert.DoesNotContain("__AGENT_STUDIO_IMAGE__", calls.ManifestContents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_NestedManifestDirectoryIsVerifiedAndAppliedRecursively()
    {
        var (path, package) = Package("deploy/nested/app.yaml",
            "kind: Deployment\nspec:\n  template:\n    spec:\n      containers:\n      - image: __AGENT_STUDIO_IMAGE__\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "deploy", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var calls = new FakeCommands();
            await Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None);
            Assert.Contains("--recursive", calls.Calls[0].Arguments);
            Assert.Contains("--recursive", calls.Calls[1].Arguments);
            Assert.Contains(Image, calls.ManifestContents);
            Assert.DoesNotContain("__AGENT_STUDIO_IMAGE__", calls.ManifestContents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_HelmAppliesExactlyTheVerifiedRendering()
    {
        var (path, package) = Package("chart/Chart.yaml", "name: web\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "chart", HelmRelease: "web", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var calls = new FakeCommands { Output = "kind: Deployment\nmetadata:\n  name: web\n"
                + "spec:\n  template:\n    spec:\n      containers:\n      - image: " + Image + "\n",
                DryRunOutput = DeploymentObject(Image) };
            await Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None);
            Assert.Equal(new[] { "helm", "kubectl", "kubectl", "kubectl" },
                calls.Calls.Select(call => call.Executable));
            Assert.Contains("template", calls.Calls[0].Arguments);
            Assert.Contains("--dry-run=client", calls.Calls[1].Arguments);
            Assert.Equal(calls.Calls[1].Arguments.Last(), calls.Calls[2].Arguments.Last());
            Assert.Equal(calls.Output, calls.ManifestContents);
            Assert.Contains("rollout", calls.Calls[3].Arguments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_HelmRefusesRenderedHookBeforeApply()
    {
        var (path, package) = Package("chart/Chart.yaml", "name: web\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "chart", HelmRelease: "web", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var hook = JsonSerializer.Serialize(new
            {
                kind = "List",
                items = new object[]
                {
                    JsonSerializer.Deserialize<JsonElement>(DeploymentObject(Image)).GetProperty("items")[0],
                    new
                    {
                        kind = "Job",
                        metadata = new Dictionary<string, object>
                        {
                            ["name"] = "migrate",
                            ["annotations"] = new Dictionary<string, string> { ["helm.sh/hook"] = "pre-upgrade" },
                        },
                    },
                },
            });
            var calls = new FakeCommands { Output = "kind: Deployment\n", DryRunOutput = hook };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None));
            Assert.Equal(new[] { "helm", "kubectl" }, calls.Calls.Select(call => call.Executable));
            Assert.Null(calls.ManifestContents);
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

    [Theory]
    [InlineData("registry.example.test/team/other@sha256:", "staging")]
    [InlineData("registry.example.test/team/other@sha256:", "production")]
    public async Task Kubernetes_RejectsUnrelatedImageOrWrongNamespace(string otherPrefix, string ns)
    {
        var (path, package) = Package("deploy/app.yaml",
            "kind: Deployment\nmetadata:\n  name: web\n  namespace: " + ns
            + "\nspec:\n  template:\n    spec:\n      containers:\n      - image: registry.example.test/team/other@sha256:"
            + new string('b', 64) + "\n---\nkind: ConfigMap\nmetadata:\n  name: decoy\ndata:\n  image: __AGENT_STUDIO_IMAGE__\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "deploy/app.yaml", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var calls = new FakeCommands
            {
                DryRunOutput = DeploymentObject(otherPrefix + new string('b', 64), ns, secondImage: Image),
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(new FakeSecrets(), calls).DeployAsync(request, CancellationToken.None));
            Assert.Single(calls.Calls);
            Assert.Contains("--dry-run=client", calls.Calls[0].Arguments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kubernetes_HelmRejectsUnrelatedImageLineAndUnresolvedPlaceholder()
    {
        var (path, package) = Package("chart/Chart.yaml", "name: web\n");
        try
        {
            var request = Container("kubernetes") with
            {
                Target = new DeployTarget("kubernetes", "web-vm", "secret://demo/staging/KUBECONFIG",
                    Namespace: "staging", ManifestPath: "chart", HelmRelease: "web", ImageResource: "web"),
                ReleaseArtifacts = [Image, package], PackagePath = path, PackageArtifact = package,
            };
            var wrongImage = "registry.example.test/team/other@sha256:" + new string('b', 64);
            var commands = new FakeCommands
            {
                Output = "kind: Deployment\nmetadata:\n  name: web\n---\nkind: ConfigMap\ndata:\n  image: " + Image,
                DryRunOutput = DeploymentObject(wrongImage),
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(new FakeSecrets(), commands).DeployAsync(request, CancellationToken.None));
            Assert.Equal(2, commands.Calls.Count);

            var unresolved = new FakeCommands { Output = "image: " + Image + "\nother: __AGENT_STUDIO_IMAGE__" };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(new FakeSecrets(), unresolved).DeployAsync(request, CancellationToken.None));
            Assert.Single(unresolved.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SshStatic_RejectsWindowsDriveQualifiedTarPath()
    {
        var (path, reference) = Package("C:\\outside\\index.html", "verified");
        try
        {
            var secrets = new FakeSecrets();
            var request = new DeployTargetRequest("demo", "staging", reference, [reference],
                new Dictionary<string, string> { ["web"] = reference },
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
    public async Task SshStatic_RejectsArchiveAboveSizeLimit()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.OpenWrite(path)) file.SetLength(VerifiedPackage.MaxArchiveBytes + 1);
            using var archive = File.OpenRead(path);
            var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var reference = "package@sha256:" + digest;
            var secrets = new FakeSecrets();
            var request = new DeployTargetRequest("demo", "staging", reference, [reference],
                new Dictionary<string, string> { ["web"] = reference },
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
    public async Task SshStatic_RejectsOversizedExpandedArchive()
    {
        var payloadPath = Path.GetTempFileName();
        var packagePath = Path.GetTempFileName();
        try
        {
            using (var payload = File.OpenWrite(payloadPath))
                payload.SetLength(VerifiedPackage.MaxExtractedBytes + 1);
            using (var package = File.Create(packagePath))
            using (var gzip = new GZipStream(package, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip))
            using (var payload = File.OpenRead(payloadPath))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "index.html")
                {
                    DataStream = payload,
                });
            using var packageForHash = File.OpenRead(packagePath);
            var digest = Convert.ToHexString(SHA256.HashData(packageForHash)).ToLowerInvariant();
            var reference = "release-package@sha256:" + digest;
            var secrets = new FakeSecrets();
            var request = new DeployTargetRequest("demo", "staging", reference, [reference],
                new Dictionary<string, string> { ["web"] = reference },
                Step("static-site") with { Directory = "/srv/www/demo" },
                new DeployTarget("ssh-docker", "web-vm", "secret://demo/staging/SSH_KEY",
                    Host: "web.example.test", User: "deploy",
                    KnownHostsSecret: "secret://demo/staging/SSH_HOSTS"), packagePath);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Executor(secrets, new FakeCommands()).DeployAsync(request, CancellationToken.None));
            Assert.Empty(secrets.References);
        }
        finally
        {
            File.Delete(payloadPath);
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task CommandOutput_RejectsOversizedInput()
    {
        using var output = new StringReader(new string('x', DeployCommandRunner.MaxCapturedOutputChars + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DeployCommandRunner.ReadBoundedAsync(output, CancellationToken.None));
    }

    [Fact]
    public async Task CommandRunner_StopsProcessWithOversizedOutput()
    {
        var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/c", "for /L %i in (1,1,120000) do @echo abcdefghijklmno"]
            : ["-c", "head -c 1048577 /dev/zero"];

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DeployCommandRunner().RunAsync(executable, arguments, null, CancellationToken.None));
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
        => new("demo", "staging", Image, [Image], ImageBinding,
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

    private static string DeploymentObject(string image, string ns = "staging", string name = "web",
        string? secondImage = null)
        => JsonSerializer.Serialize(new
        {
            kind = "List",
            items = new[] { new { kind = "Deployment", metadata = new { name, @namespace = ns },
                spec = new { template = new { spec = new
                {
                    containers = secondImage is null ? new[] { new { image } }
                        : new[] { new { image }, new { image = secondImage } },
                } } } } },
        });

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
        public string DryRunOutput { get; init; } = DeploymentObject(Image);
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
            if (executable == "kubectl" && arguments.Contains("apply") && !arguments.Contains("--dry-run=client"))
                ManifestContents = Directory.Exists(arguments.Last())
                    ? string.Concat(Directory.EnumerateFiles(arguments.Last(), "*", SearchOption.AllDirectories)
                        .Select(File.ReadAllText))
                    : File.ReadAllText(arguments.Last());
            return Task.FromResult(new DeployCommandResult(0,
                executable == "kubectl" && arguments.Contains("--dry-run=client") ? DryRunOutput : Output));
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
