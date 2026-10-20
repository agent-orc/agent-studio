using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployHostCommandTests
{
    [Fact]
    public async Task Tenant_executor_uses_distinct_containers_health_urls_and_variable_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "deploy-tenant-executor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var paths = new List<string>();
        var health = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                await using var stream = socket.GetStream();
                var buffer = new byte[1024];
                var count = await stream.ReadAsync(buffer);
                paths.Add(Encoding.ASCII.GetString(buffer, 0, count).Split('\r')[0]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            }
        });
        try
        {
            var tokenFile = Path.Combine(root, "token");
            await File.WriteAllTextAsync(tokenFile, Guid.NewGuid().ToString("N"));
            var secretValue = "value-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(root, "provider-entry"), secretValue);
            var claims = new Queue<object>();
            foreach (var (tenant, destination) in new[] { ("acme", "8181"), ("beta", "8182") })
                claims.Enqueue(new
                {
                    id = tenant, project = "demo", environmentId = "stage", hostId = "host",
                    artifact = "example/image@sha256:" + new string('a', 64), template = "container",
                    destination, healthUrl = $"http://127.0.0.1:{port}/{tenant}",
                    secretRefs = new[] { "provider-entry" }, containerPort = 8080,
                    previousArtifact = (string?)null, status = "claimed", leaseToken = tenant + "-lease", tenant,
                    variables = new Dictionary<string, string> { ["MODE"] = tenant },
                    secretVariableRefs = new Dictionary<string, string> { ["API_TOKEN"] = "provider-entry" },
                });
            var reports = new List<string>();
            using var handler = new ClaimHandler(claims, reports);
            var commands = new List<(string Name, string Publish, string Environment)>();
            async Task RunCommand(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
            {
                Assert.Equal("docker", executable);
                if (arguments[0] != "run") return;
                var args = arguments.ToArray();
                var name = args[Array.IndexOf(args, "--name") + 1];
                var publish = args[Array.IndexOf(args, "--publish") + 1];
                var envFile = args[Array.IndexOf(args, "--env-file") + 1];
                commands.Add((name, publish, await File.ReadAllTextAsync(envFile, ct)));
            }
            for (var i = 0; i < 2; i++)
            {
                using var client = new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("https://studio.test/") };
                Assert.Equal(0, await DeployHostCommand.RunAsync(
                    [client.BaseAddress.ToString(), "host", tokenFile, root, root], CancellationToken.None,
                    client, RunCommand));
            }
            await health;
            Assert.Equal(["GET /acme HTTP/1.1", "GET /beta HTTP/1.1"], paths);
            Assert.Equal(["deploy-demo-stage-acme", "deploy-demo-stage-beta"], commands.Select(item => item.Name));
            Assert.Equal(["127.0.0.1:8181:8080", "127.0.0.1:8182:8080"], commands.Select(item => item.Publish));
            Assert.Contains("MODE=acme", commands[0].Environment);
            Assert.Contains("MODE=beta", commands[1].Environment);
            Assert.All(commands, item =>
            {
                Assert.Contains("API_TOKEN=" + secretValue, item.Environment);
                Assert.DoesNotContain("PROVIDER_ENTRY=", item.Environment);
            });
            Assert.Equal(2, reports.Count);
            Assert.All(reports, report => Assert.Contains("\"healthy\":true", report));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, true);
        }
    }

    private sealed class ClaimHandler(Queue<object> claims, List<string> reports) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(claims.Dequeue()) };
            reports.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RealServer_Claim_Deploy_HealthCheck_AndReport_LocalContainer()
    {
        Assert.True(OperatingSystem.IsLinux(), "The CI container job requires Linux.");
        var image = Docker("image", "inspect", "caddy:2-alpine", "--format", "{{index .RepoDigests 0}}").Trim();
        if (string.IsNullOrWhiteSpace(image))
        {
            Assert.NotEmpty(Docker("pull", "caddy:2-alpine"));
            image = Docker("image", "inspect", "caddy:2-alpine", "--format", "{{index .RepoDigests 0}}").Trim();
        }
        Assert.Matches("@sha256:[0-9a-f]{64}$", image);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var project = "Operator Demo " + Guid.NewGuid().ToString("N")[..8];
        var projectHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project)))[..16].ToLowerInvariant();
        var containerName = "deploy-" + projectHash + "-stage";
        var root = Path.Combine(Path.GetTempPath(), "deploy-host-test-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var watch = Path.Combine(root, "workspace", "projects", project);
        var environmentDir = Path.Combine(repo, "docs", "deployments", "environments");
        Directory.CreateDirectory(environmentDir);
        Directory.CreateDirectory(watch);
        Git(repo, "init", "-b", "develop");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(repo, "initial"), "initial");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "initial");
        File.WriteAllText(Path.Combine(environmentDir, "production.json"), JsonSerializer.Serialize(new
        {
            id = "production", name = "Production", kind = "production", targetHostId = "prod-host",
            address = "http://127.0.0.1:1/", template = "container", destination = "1",
            healthUrl = "http://127.0.0.1:1/", containerPort = 80,
        }));
        var tokenFile = Path.Combine(root, "token");
        try
        {
            await using var factory = new WebApplicationFactory<ProjectEnvironmentService>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = Path.Combine(root, "workspace"),
                        ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        ["Security:Profile"] = "networked",
                        ["AllowedHosts"] = "studio.test",
                        ["Deploy:ReleaseEvidenceRoot"] = Path.Combine(root, "release-evidence"),
                        ["WatchPaths:0:Name"] = project,
                        ["WatchPaths:0:Path"] = watch,
                        ["WatchPaths:0:RootPath"] = repo,
                        ["WatchPaths:0:RepositoryPath"] = repo,
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new FakeTimeProvider());
                });
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://studio.test") });
            var security = factory.Services.GetRequiredService<AccessSecurityStore>();
            var releaseEnrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("gate",
                [RunnerScopes.Release], null, null));
            var release = security.EnrollRunner(releaseEnrollment.Code);
            var deployEnrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("deploy",
                [RunnerScopes.Deploy], null, null));
            var deploy = security.EnrollRunner(deployEnrollment.Code);
            await File.WriteAllTextAsync(tokenFile, deploy.Secret);
            File.WriteAllText(Path.Combine(environmentDir, "stage.json"), JsonSerializer.Serialize(new
            {
                id = "stage", name = "Stage", kind = "stage", targetHostId = deploy.Runner.Id,
                address = $"http://127.0.0.1:{port}/", template = "container", destination = port.ToString(),
                healthUrl = $"http://127.0.0.1:{port}/", containerPort = 80,
            }));
            Git(repo, "add", "docs/deployments/environments");
            Git(repo, "commit", "-m", "declare environments");
            var commit = Git(repo, "rev-parse", "HEAD").Trim();
            var service = factory.Services.GetRequiredService<ProjectEnvironmentService>();
            WriteReleaseEvidence(root, project, image, commit);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", release.Secret);
            using var queued = await client.PostAsJsonAsync("/api/deploy/gate-releases",
                new GateArtifactRequest(project, image, commit));
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var result = await DeployHostCommand.RunAsync(
                [client.BaseAddress!.ToString(), deploy.Runner.Id, tokenFile, root, root], CancellationToken.None, client);
            Assert.Equal(0, result);
            var state = service.Read(project)!;
            Assert.Equal(image, state.Environments.Single(item => item.Declaration.Id == "stage").DeployedArtifact);
            Assert.Equal("pending", Assert.Single(state.Promotions).Status);
            Assert.Equal("healthy", Assert.Single(state.Environments[0].History).Status);
        }
        finally
        {
            Docker("rm", "-f", containerName);
            Directory.Delete(root, true);
        }
    }

    private static void WriteReleaseEvidence(string root, string project, string artifact, string commit)
    {
        var directory = Path.Combine(root, "release-evidence", commit);
        Directory.CreateDirectory(directory);
        var archive = Encoding.UTF8.GetBytes("gate-built-release-for-" + commit);
        File.WriteAllBytes(Path.Combine(directory, "release.tar.gz"), archive);
        var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(directory, "release.sha256"), digest + "\n");
        File.WriteAllText(Path.Combine(directory, "full-gate.log"), "PROMOTION_FULL_GATE=passed\n");
        File.WriteAllText(Path.Combine(directory, "promotion-record.json"), JsonSerializer.Serialize(new
        {
            status = "promoted", gate = "passed", candidateSha = commit, atomicPush = true,
            releasePackage = new { status = "written", sha256 = digest },
        }));
        File.WriteAllText(Path.Combine(directory, "deploy-artifact.json"), JsonSerializer.Serialize(new
        {
            project, commit, artifact, sourceSha256 = digest,
        }));
    }

    private static string Git(string directory, params string[] args) => Command("git", directory, args);
    private static string Docker(params string[] args) => Command("docker", null, args);

    private static string Command(string executable, string? directory, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory ?? Environment.CurrentDirectory,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (executable == "git") Assert.True(process.ExitCode == 0, error);
        return process.ExitCode == 0 ? output : string.Empty;
    }
}
