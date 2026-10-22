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
    public async Task StaticPreviewJob_UsesRunZoneBoundaryAndReportsScreenshotThenRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "static-preview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, "token");
        await File.WriteAllTextAsync(tokenFile, $"test-{Guid.NewGuid():N}");
        var commit = new string('a', 40);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");
        var zone = new StaticPreviewZone(root, png);
        var handler = new PreviewTransport(commit, root);
        try
        {
            using (var first = new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("https://studio.test/") })
                Assert.Equal(0, await DeployHostCommand.RunAsync(
                    ["https://studio.test", "preview-host", tokenFile, root], CancellationToken.None, first, zone));
            Assert.Equal(commit, zone.DeployedCommit);
            Assert.True(File.Exists(Path.Combine(root, "deployed", "index.html")));
            Assert.Equal(Convert.ToBase64String(png), handler.Reports[0].GetProperty("screenshotBase64").GetString());
            using (var second = new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("https://studio.test/") })
                Assert.Equal(0, await DeployHostCommand.RunAsync(
                    ["https://studio.test", "preview-host", tokenFile, root], CancellationToken.None, second, zone));
            Assert.False(File.Exists(Path.Combine(root, "deployed", "index.html")));
            Assert.True(zone.Removed);
            Assert.Equal(2, handler.Reports.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StaticPreviewZone(string root, byte[] png) : IPreviewRunZone
    {
        public string? DeployedCommit { get; private set; }
        public bool Removed { get; private set; }

        public Task<PreviewRunZoneResult> DeployAsync(PreviewRunZoneJob job, CancellationToken cancellationToken)
        {
            Assert.Equal("static", job.Template);
            Assert.Equal("dist", job.ArtifactPath);
            Assert.Equal(job.SourceCommit, job.SourceRef);
            Assert.Equal(root, job.SourceRepository);
            Assert.Equal(["npm run build"], job.BuildCommands);
            Assert.Empty(job.SecretRefs);
            DeployedCommit = job.SourceCommit;
            Directory.CreateDirectory(Path.Combine(root, "deployed"));
            File.WriteAllText(Path.Combine(root, "deployed", "index.html"), "<h1>Preview</h1>");
            return Task.FromResult(new PreviewRunZoneResult(true, png));
        }

        public Task RemoveAsync(PreviewRunZoneJob job, CancellationToken cancellationToken)
        {
            File.Delete(Path.Combine(root, "deployed", "index.html"));
            Removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class PreviewTransport(string commit, string root) : HttpMessageHandler
    {
        private int _claims;
        public List<JsonElement> Reports { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
            {
                var kind = Interlocked.Increment(ref _claims) == 1 ? "preview" : "preview-remove";
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = kind, project = "demo", environmentId = "preview-agt-3180",
                        hostId = "preview-host", artifact = "source:" + commit,
                        template = "static", destination = Path.Combine(root, "deployed"),
                        healthUrl = (string?)null, secretRefs = Array.Empty<string>(), containerPort = 8080,
                        previousArtifact = (string?)null, status = "claimed", leaseToken = "lease-1",
                        kind, cardKey = "agt-3180", sourceCommit = commit,
                        sourceRepository = root, sourceRef = commit, artifactPath = "dist", dockerfile = (string?)null,
                        address = "https://agt-3180.demo.preview.internal",
                        buildCommands = new[] { "npm run build" },
                    }),
                };
            }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Reports.Add(document.RootElement.Clone());
            return new(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task First_step_health_failure_stops_second_step_and_reports_failing_step()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var healthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sshCalls = 0;
            var executor = new SshDeployExecutor((_, _, _, _) =>
            {
                sshCalls++;
                return Task.FromResult(new ProcessResult(0, "", ""));
            });
            var steps = new[] { "first", "second" }.Select(name =>
                new ProjectDeployStep("container", "app", "host-a", null, null, null, [], ["staging"])
                { ComposeFile = "/srv/apps/" + name + "/compose.yml", Service = name,
                    HealthUrl = "https://health.example/" + name, HealthTimeoutSeconds = 3 }).ToArray();
            var job = new
            {
                id = "job-a", project = "demo", environmentId = "stage", hostId = "host-a",
                artifact = "image@sha256:" + new string('a', 64), template = "container",
                destination = "1", healthUrl = (string?)null, secretRefs = Array.Empty<string>(),
                containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                leaseToken = "lease-a", sshSteps = steps,
                variables = Array.Empty<ProjectVariableDefinition>(), artifacts = Array.Empty<ProjectArtifactDefinition>(),
            };
            string? report = null;
            using var client = new HttpClient(new StubHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
                if (path.EndsWith("/report", StringComparison.Ordinal))
                    report = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            })) { BaseAddress = new Uri("https://studio.example/") };

            var run = DeployHostCommand.RunAsync(
                [client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"], CancellationToken.None,
                client, sshExecutor: executor, checkHealth: async (_, ct) =>
                {
                    healthStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                }, clock: clock);
            await healthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(3));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, result);
            Assert.Equal(1, sshCalls);
            Assert.Contains("health-http-failed", report);
            Assert.Contains("deploy[0]:container", report);
            Assert.DoesNotContain("deploy[1]:container", report);
        }
        finally { File.Delete(tokenFile); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    public async Task Renewal_retry_or_old_backend_404_keeps_running_step(HttpStatusCode firstStatus,
        int expectedRenewals)
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewalCount = 0;
            var reports = 0;
            var executor = new SshDeployExecutor(async (_, _, _, ct) =>
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(ct);
                return new ProcessResult(0, "", "");
            });
            var step = new ProjectDeployStep("container", "app", "host-a", null, null, null, [], ["staging"])
                { ComposeFile = "/srv/apps/demo/compose.yml", Service = "demo" };
            var job = new
            {
                id = "job-a", project = "demo", environmentId = "stage", hostId = "host-a",
                artifact = "image@sha256:" + new string('a', 64), template = "ssh-v2",
                destination = "1", healthUrl = (string?)null, secretRefs = Array.Empty<string>(),
                containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                leaseToken = "lease-a", sshSteps = new[] { step },
                variables = Array.Empty<ProjectVariableDefinition>(), artifacts = Array.Empty<ProjectArtifactDefinition>(),
            };
            using var client = new HttpClient(new StubHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(job) });
                if (path.EndsWith("/renew", StringComparison.Ordinal))
                {
                    var count = Interlocked.Increment(ref renewalCount);
                    if (count == expectedRenewals) renewed.TrySetResult();
                    return Task.FromResult(new HttpResponseMessage(count == 1 ? firstStatus : HttpStatusCode.OK));
                }
                if (path.EndsWith("/report", StringComparison.Ordinal)) Interlocked.Increment(ref reports);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })) { BaseAddress = new Uri("https://studio.example/") };

            var run = DeployHostCommand.RunAsync([client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"],
                CancellationToken.None, client, sshExecutor: executor, clock: clock);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < expectedRenewals; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                using var waitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (Volatile.Read(ref renewalCount) < i + 1)
                    await Task.Delay(10, waitLimit.Token);
                Assert.True(renewalCount >= i + 1);
            }
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finish.SetResult();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(expectedRenewals, renewalCount);
            Assert.Equal(1, reports);
        }
        finally { File.Delete(tokenFile); }
    }

    [Fact]
    public async Task Missing_secret_value_reports_failed_step()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            string? report = null;
            var step = new ProjectDeployStep("container", "app", "host-a", null, null, null, [], ["staging"])
                { ComposeFile = "/srv/apps/demo/compose.yml", Service = "demo" };
            var job = new
            {
                id = "job-a", project = "demo", environmentId = "stage", hostId = "host-a",
                artifact = "image@sha256:" + new string('a', 64), template = "ssh-v2", destination = "1",
                healthUrl = (string?)null, secretRefs = Array.Empty<string>(), containerPort = 8080,
                previousArtifact = (string?)null, status = "claimed", leaseToken = "lease-a",
                sshSteps = new[] { step }, variables = new[] { new ProjectVariableDefinition("SECRET", null, "SECRET", []) },
                artifacts = Array.Empty<ProjectArtifactDefinition>(),
            };
            using var client = new HttpClient(new StubHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
                if (path.EndsWith("/resolve", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { other = "missing" }) };
                if (path.EndsWith("/report", StringComparison.Ordinal))
                    report = await request.Content!.ReadAsStringAsync();
                return new(HttpStatusCode.OK);
            })) { BaseAddress = new Uri("https://studio.example/") };

            Assert.Equal(1, await DeployHostCommand.RunAsync(
                [client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"], CancellationToken.None,
                client, sshExecutor: new SshDeployExecutor((_, _, _, _) => throw new Xunit.Sdk.XunitException("SSH ran"))));
            Assert.Contains("deploy[0]:container", report);
            Assert.Contains("\"healthy\":false", report);
        }
        finally { File.Delete(tokenFile); }
    }

    [Fact]
    public async Task Lost_lease_cancels_ssh_and_never_reports_stale_result()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = false;
            var reports = 0;
            var renewals = 0;
            var executor = new SshDeployExecutor(async (_, _, _, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; throw; }
                return new ProcessResult(0, "", "");
            });
            var step = new ProjectDeployStep("container", "app", "host-a", null, null, null, [], ["staging"])
            { ComposeFile = "/srv/apps/demo/compose.yml", Service = "demo" };
            var job = new
            {
                id = "job-a", project = "demo", environmentId = "stage", hostId = "host-a",
                artifact = "image@sha256:" + new string('a', 64), template = "container",
                destination = "1", healthUrl = (string?)null, secretRefs = Array.Empty<string>(),
                containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                leaseToken = "lease-a", sshSteps = new[] { step },
                variables = Array.Empty<ProjectVariableDefinition>(), artifacts = Array.Empty<ProjectArtifactDefinition>(),
            };
            using var client = new HttpClient(new StubHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(job) });
                if (path.EndsWith("/renew", StringComparison.Ordinal))
                {
                    renewals++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
                }
                if (path.EndsWith("/report", StringComparison.Ordinal)) reports++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })) { BaseAddress = new Uri("https://studio.example/") };

            var run = DeployHostCommand.RunAsync([client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"],
                CancellationToken.None, client, sshExecutor: executor, clock: clock);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(cancelled);
            Assert.Equal(1, renewals);
            Assert.Equal(0, reports);
        }
        finally { File.Delete(tokenFile); }
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
