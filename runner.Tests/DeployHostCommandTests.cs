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
using AgentStudio.TaskServer.Contracts;
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
    public async Task MultiStepSshChecksEachHealthUrlAndRecordsTheFailingStep()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            var commands = 0;
            JsonElement report = default;
            var steps = Enumerable.Range(0, 3).Select(index => new ProjectDeployStep(
                "container", "app", "web-vm", null, null, null, [], ["staging"])
            {
                ComposeFile = $"/srv/apps/app{index}/compose.yml", Service = "app",
                HealthUrl = $"https://health.test/{index}", HealthTimeoutSeconds = 1,
            }).ToArray();
            using var client = new HttpClient(new Handler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return JsonResponse(new
                    {
                        id = "job-1", project = "demo", environmentId = "stage", hostId = "web-vm",
                        artifact = "app@sha256:" + new string('a', 64), template = "container",
                        destination = "8080", healthUrl = "https://health.test/2", secretRefs = Array.Empty<string>(),
                        containerPort = 8080, status = "claimed", leaseToken = "lease-1", sshSteps = steps,
                    });
                if (path.EndsWith("/log", StringComparison.Ordinal)) return new(HttpStatusCode.OK);
                if (path.EndsWith("/report", StringComparison.Ordinal))
                {
                    report = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
                    return new(HttpStatusCode.OK);
                }
                if (path == "/0") return new(HttpStatusCode.OK);
                if (path == "/1") return new(HttpStatusCode.ServiceUnavailable);
                throw new InvalidOperationException("Later steps and health probes must not run.");
            })) { BaseAddress = new Uri("https://studio.test/") };
            var executor = new SshDeployExecutor((_, _, _, _) =>
            {
                commands++;
                return Task.FromResult(new ProcessResult(0, "", ""));
            });

            var result = await DeployHostCommand.RunAsync(
                ["https://studio.test", "web-vm", tokenFile, "/unused"], CancellationToken.None,
                client, executor, client);

            Assert.Equal(1, result);
            Assert.Equal(2, commands);
            Assert.Equal("health-http-failed", report.GetProperty("evidence").GetString());
            var results = report.GetProperty("steps");
            Assert.Equal(2, results.GetArrayLength());
            Assert.True(results[0].GetProperty("succeeded").GetBoolean());
            Assert.Equal("deploy[1]:container", results[1].GetProperty("name").GetString());
            Assert.False(results[1].GetProperty("succeeded").GetBoolean());
        }
        finally { File.Delete(tokenFile); }
    }

    [Fact]
    public async Task LongRunningSshRenewsItsClaim_AndStopsWhenRenewalIsFenced()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            var clock = new FakeTimeProvider();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewals = 0;
            var reports = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return Task.FromResult(JsonResponse(new
                    {
                        id = "job-2", project = "demo", environmentId = "stage", hostId = "web-vm",
                        artifact = "app@sha256:" + new string('a', 64), template = "container",
                        destination = "8080", secretRefs = Array.Empty<string>(), containerPort = 8080,
                        status = "claimed", leaseToken = "lease-2",
                        sshSteps = new[] { new ProjectDeployStep("container", "app", "web-vm", null, null, null,
                            [], ["staging"]) { ComposeFile = "/srv/apps/app/compose.yml", Service = "app" } },
                    }));
                if (path.EndsWith("/renew", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref renewals) <= 5
                        ? HttpStatusCode.OK : HttpStatusCode.Conflict));
                if (path.EndsWith("/report", StringComparison.Ordinal)) Interlocked.Increment(ref reports);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })) { BaseAddress = new Uri("https://studio.test/") };
            var executor = new SshDeployExecutor(async (_, _, _, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ProcessResult(0, "", "");
            });
            var running = DeployHostCommand.RunAsync(
                ["https://studio.test", "web-vm", tokenFile, "/unused"], CancellationToken.None,
                client, executor, timeProvider: clock);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var minute = 1; minute <= 6; minute++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref renewals) >= minute, 2000));
            }
            Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, reports);
        }
        finally { File.Delete(tokenFile); }
    }

    private static HttpResponseMessage JsonResponse(object value)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
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
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
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
