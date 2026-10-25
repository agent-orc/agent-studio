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
using AgentStudio.Secrets;
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
                    resolvedVariables = new Dictionary<string, string> { ["MODE"] = tenant },
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

    [Fact]
    public async Task Tenant_ssh_executor_resolves_secret_reference_under_the_workload_name()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, Guid.NewGuid().ToString("N"));
        var secretValue = "value-" + Guid.NewGuid().ToString("N");
        var artifact = "image@sha256:" + new string('a', 64);
        string? reference = null;
        string? payload = null;
        var executor = new SshDeployExecutor((_, _, input, _) =>
        {
            payload = input;
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        var step = new ProjectDeployStep("container", "app", "host", null, null, null, [], ["staging"])
        { ComposeFile = "/srv/apps/demo/stage/tenants/acme/compose.yml", Service = "demo" };
        using var client = new HttpClient(new StubHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/claim", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    id = "tenant-job", project = "demo", environmentId = "stage", hostId = "host",
                    artifact, template = "container", destination = "8181", healthUrl = (string?)null,
                    secretRefs = new[] { "provider-entry" }, containerPort = 8080,
                    previousArtifact = (string?)null, status = "claimed", leaseToken = "tenant-lease",
                    tenant = "acme", sshSteps = new[] { step },
                    variables = Array.Empty<ProjectVariableDefinition>(),
                    resolvedVariables = new Dictionary<string, string> { ["MODE"] = "tenant" },
                    secretVariableRefs = new Dictionary<string, string> { ["API_TOKEN"] = "provider-entry" },
                }) };
            if (path.EndsWith("/artifact", StringComparison.Ordinal)) return ProvenanceResponse(artifact);
            if (path.EndsWith("/resolve", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                reference = body.RootElement.GetProperty("reference").GetString();
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { value = secretValue }) };
            }
            return new(HttpStatusCode.NoContent);
        })) { BaseAddress = new Uri("https://studio.test/") };
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal(0, await DeployHostCommand.RunAsync(
                [client.BaseAddress.ToString(), "host", tokenFile, "/unused"], CancellationToken.None,
                client, executor, clock: clock));
            Assert.Equal("secret://demo/stage/provider-entry", reference);
            using var command = JsonDocument.Parse(payload!);
            var variables = command.RootElement.GetProperty("variables");
            Assert.Equal("tenant", variables.GetProperty("MODE").GetString());
            Assert.Equal(secretValue, variables.GetProperty("API_TOKEN").GetString());
            Assert.False(variables.TryGetProperty("PROVIDER_ENTRY", out _));
            Assert.StartsWith("tenant-", command.RootElement.GetProperty("project").GetString());
        }
        finally { File.Delete(tokenFile); }
    }

    private sealed class ClaimHandler(Queue<object> claims, List<string> reports) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(claims.Dequeue()) };
            if (request.RequestUri.AbsolutePath.EndsWith("/artifact", StringComparison.Ordinal))
                return ProvenanceResponse("example/image@sha256:" + new string('a', 64));
            reports.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Host_resolves_bound_references_under_its_lease_and_never_sends_values_to_the_control_plane()
    {
        var value = "runtime-" + Guid.NewGuid().ToString("N");
        string? received = null;
        using var client = new HttpClient(new ResolveHandler(async request =>
        {
            received = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { value, reference = "secret://demo/stage/DATABASE_URL" }) };
        })) { BaseAddress = new Uri("https://studio.test/") };
        var lines = await DeployHostCommand.ResolveEnvironmentAsync(client, "demo", "stage", "job-1", "lease-1",
            ["DATABASE_URL"], new Dictionary<string, string>
            { ["DATABASE_URL"] = "secret://demo/stage/DATABASE_URL" }, CancellationToken.None);

        Assert.Equal("DATABASE_URL=" + value, Assert.Single(lines));
        Assert.Contains("secret://demo/stage/DATABASE_URL", received);
        Assert.Contains("lease-1", received);
        Assert.DoesNotContain(value, received);
        using var denied = new HttpClient(new ResolveHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = JsonContent.Create(new { error = value }) }))) { BaseAddress = new Uri("https://studio.test/") };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DeployHostCommand.ResolveEnvironmentAsync(
            denied, "demo", "stage", "job-1", "lease-1", ["DATABASE_URL"],
            new Dictionary<string, string> { ["DATABASE_URL"] = "secret://demo/stage/DATABASE_URL" }, CancellationToken.None));
        Assert.DoesNotContain(value, failure.ToString());
    }

    [Fact]
    public async Task Maximum_length_environment_id_resolves_with_a_valid_purpose()
    {
        var environment = "e" + new string('a', 63);
        string? purpose = null;
        using var client = new HttpClient(new ResolveHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            purpose = body.RootElement.GetProperty("purpose").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { value = "runtime-value" }) };
        })) { BaseAddress = new Uri("https://studio.test/") };

        var lines = await DeployHostCommand.ResolveEnvironmentAsync(client, "demo", environment, "job-1", "lease-1",
            ["DATABASE_URL"], new Dictionary<string, string>
            { ["DATABASE_URL"] = $"secret://demo/{environment}/DATABASE_URL" }, CancellationToken.None);

        Assert.Equal("DATABASE_URL=runtime-value", Assert.Single(lines));
        Assert.True(SecretAccessPolicy.IsValidPurpose(purpose));
    }

    [Theory]
    [InlineData("DB-URL", "DB.URL")]
    [InlineData("Db_url", "DB_URL")]
    public async Task Queued_job_with_colliding_secret_names_fails_before_resolution(string first, string second)
    {
        var requests = 0;
        using var client = new HttpClient(new ResolveHandler(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })) { BaseAddress = new Uri("https://studio.test/") };
        var bindings = new Dictionary<string, string>
        {
            [first] = $"secret://demo/stage/{first}",
            [second] = $"secret://demo/stage/{second}",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeployHostCommand.ResolveEnvironmentAsync(
            client, "demo", "stage", "job-1", "lease-1", [first, second], bindings, CancellationToken.None));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Claimed_job_with_legacy_collision_reports_failure_without_resolving_values()
    {
        var resolved = 0;
        string? evidence = null;
        using var client = new HttpClient(new ResolveHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/claim", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "job-1", project = "demo", environmentId = "stage", hostId = "host-1",
                        artifact = "example/app", template = "container", destination = "8080",
                        healthUrl = (string?)null, secretRefs = new[] { "DB-URL", "DB.URL" },
                        containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                        leaseToken = "lease-1",
                    }),
                };
            if (path.EndsWith("/resolve", StringComparison.Ordinal)) resolved++;
            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                evidence = body.RootElement.GetProperty("evidence").GetString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("https://studio.test/") };
        var tokenFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tokenFile, "test-lease-token");
            Assert.Equal(1, await DeployHostCommand.RunAsync(
                [client.BaseAddress!.ToString(), "host-1", tokenFile, Path.GetTempPath()], CancellationToken.None, client));
            Assert.Equal("secret-reference-collision", evidence);
            Assert.Equal(0, resolved);
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    private sealed class ResolveHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    [Fact]
    public async Task UnverifiedDigest_IsRefusedBeforeTransfer()
    {
        var root = Path.Combine(Path.GetTempPath(), "deploy-unverified-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = Path.Combine(root, "token");
        await File.WriteAllTextAsync(token, "deploy-test-token");
        var artifact = "release-package@sha256:" + new string('a', 64);
        var calls = new List<string>();
        using var client = new HttpClient(new RouteHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    id = "job", project = "demo", environmentId = "stage", hostId = "host",
                    artifact, template = "static", destination = Path.Combine(root, "destination"),
                    healthUrl = (string?)null, secretRefs = Array.Empty<string>(), containerPort = 80,
                    previousArtifact = (string?)null, status = "claimed", leaseToken = "lease",
                }) };
            if (request.RequestUri.AbsolutePath.EndsWith("/artifact", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("https://studio.test/") };
        try
        {
            Assert.Equal(1, await DeployHostCommand.RunAsync(
                ["https://studio.test", "host", token, Path.Combine(root, "cache")], CancellationToken.None, client));
            Assert.Equal(3, calls.Count);
            Assert.EndsWith("/artifact", calls[1], StringComparison.Ordinal);
            Assert.EndsWith("/report", calls[2], StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, "destination")));
            Assert.False(Directory.Exists(Path.Combine(root, "cache")));
        }
        finally { Directory.Delete(root, true); }
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
                if (path.EndsWith("/artifact", StringComparison.Ordinal))
                    return ProvenanceResponse(job.artifact);
                if (path.EndsWith("/report", StringComparison.Ordinal))
                    report = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            })) { BaseAddress = new Uri("https://studio.example/") };

            var run = DeployHostCommand.RunAsync(
                [client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"], CancellationToken.None,
                client, executor, async (_, ct) =>
                {
                    healthStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                }, clock);
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

    private static HttpResponseMessage ProvenanceResponse(string artifact) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            project = "demo", commit = new string('b', 40), artifact, digest = new string('a', 64),
            gateRunId = "gate", template = "container", builderIdentity = "builder",
            location = "external", sourceDigest = "source",
        }) };

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
                if (path.EndsWith("/artifact", StringComparison.Ordinal))
                    return Task.FromResult(ProvenanceResponse(job.artifact));
                if (path.EndsWith("/renew", StringComparison.Ordinal))
                {
                    renewals++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
                }
                if (path.EndsWith("/report", StringComparison.Ordinal)) reports++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })) { BaseAddress = new Uri("https://studio.example/") };

            var run = DeployHostCommand.RunAsync([client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"],
                CancellationToken.None, client, executor, clock: clock);
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
                    ["https://studio.test", "preview-host", tokenFile, root], CancellationToken.None, first,
                    previewZone: zone));
            Assert.Equal(commit, zone.DeployedCommit);
            Assert.True(File.Exists(Path.Combine(root, "deployed", "index.html")));
            Assert.Equal(Convert.ToBase64String(png), handler.Reports[0].GetProperty("screenshotBase64").GetString());
            using (var second = new HttpClient(handler, disposeHandler: false)
                { BaseAddress = new Uri("https://studio.test/") })
                Assert.Equal(0, await DeployHostCommand.RunAsync(
                    ["https://studio.test", "preview-host", tokenFile, root], CancellationToken.None, second,
                    previewZone: zone));
            Assert.False(File.Exists(Path.Combine(root, "deployed", "index.html")));
            Assert.True(zone.Removed);
            Assert.Equal(2, handler.Reports.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryRegistryFailure_LeavesClaimForRetryWithoutTransferOrReport(bool atContent)
    {
        var root = Path.Combine(Path.GetTempPath(), "deploy-retry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = Path.Combine(root, "token");
        await File.WriteAllTextAsync(token, "deploy-test-token");
        var digest = new string('a', 64);
        var artifact = "release-package@sha256:" + digest;
        var calls = new List<string>();
        using var client = new HttpClient(new RouteHandler(request =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    id = "job", project = "demo", environmentId = "stage", hostId = "host",
                    artifact, template = "static", destination = Path.Combine(root, "destination"),
                    healthUrl = (string?)null, secretRefs = Array.Empty<string>(), containerPort = 80,
                    previousArtifact = (string?)null, status = "claimed", leaseToken = "lease",
                }) };
            if (atContent && !request.RequestUri.Query.Contains("content", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    project = "demo", commit = new string('b', 40), artifact, digest,
                    gateRunId = "gate/test", template = "static", builderIdentity = "builder",
                    location = "built-in", sourceDigest = digest,
                }) };
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        })) { BaseAddress = new Uri("https://studio.test/") };
        try
        {
            Assert.Equal(0, await DeployHostCommand.RunAsync(
                ["https://studio.test", "host", token, Path.Combine(root, "cache")], CancellationToken.None, client));
            Assert.Equal(atContent ? 3 : 2, calls.Count);
            Assert.DoesNotContain(calls, call => call.EndsWith("/report", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(root, "destination")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
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
    public async Task Legacy_health_timeout_reports_failure_even_while_lease_renews()
    {
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenFile, "test-token");
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var healthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? report = null;
            var job = new
            {
                id = "job-a", project = "demo", environmentId = "stage", hostId = "host-a",
                artifact = "image@sha256:" + new string('a', 64), template = "container",
                destination = "1", healthUrl = "https://health.example/", secretRefs = Array.Empty<string>(),
                containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                leaseToken = "lease-a", retryStep = "health",
            };
            using var client = new HttpClient(new StubHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/claim", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
                if (path.EndsWith("/report", StringComparison.Ordinal))
                    report = await request.Content!.ReadAsStringAsync();
                return new(HttpStatusCode.OK);
            })) { BaseAddress = new Uri("https://studio.example/") };

            var run = DeployHostCommand.RunAsync([client.BaseAddress.ToString(), "host-a", tokenFile, "/unused"],
                CancellationToken.None, client, checkHealth: async (_, ct) =>
                {
                    healthStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                }, clock: clock);
            await healthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("\"healthy\":false", report);
            Assert.Contains("health-http-failed", report);
        }
        finally { File.Delete(tokenFile); }
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
                if (path.EndsWith("/artifact", StringComparison.Ordinal))
                    return Task.FromResult(ProvenanceResponse(job.artifact));
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
                if (path.EndsWith("/artifact", StringComparison.Ordinal))
                    return ProvenanceResponse(job.artifact);
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
                        ["Secrets:KeyFile"] = Path.Combine(root, "keys", "secrets.kek"),
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
                secretRefs = new[] { "DATABASE_URL" },
            }));
            Git(repo, "add", "docs/deployments/environments");
            Git(repo, "commit", "-m", "declare environments");
            var commit = Git(repo, "rev-parse", "HEAD").Trim();
            var service = factory.Services.GetRequiredService<ProjectEnvironmentService>();
            var value = "runtime-" + Guid.NewGuid().ToString("N");
            await factory.Services.GetRequiredService<ISecretProvider>().PutAsync(
                SecretScope.ForEnvironment(project, "stage"), "DATABASE_URL", value, SecretActor.Human("owner"));
            factory.Services.GetRequiredService<ProjectSecretBindings>().Bind(project, "stage", "DATABASE_URL",
                $"secret://{project}/stage/DATABASE_URL", "human:owner", null);
            WriteReleaseEvidence(root, project, image, commit);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", release.Secret);
            using var queued = await client.PostAsJsonAsync("/api/deploy/gate-releases",
                new GateArtifactRequest(project, image, commit));
            Assert.True(queued.StatusCode == HttpStatusCode.Accepted,
                await queued.Content.ReadAsStringAsync());
            var result = await DeployHostCommand.RunAsync(
                [client.BaseAddress!.ToString(), deploy.Runner.Id, tokenFile, root, root], CancellationToken.None, client);
            Assert.Equal(0, result);
            var state = service.Read(project)!;
            Assert.Equal(image, state.Environments.Single(item => item.Declaration.Id == "stage").DeployedArtifact);
            Assert.Equal("pending", Assert.Single(state.Promotions).Status);
            Assert.Equal("healthy", Assert.Single(state.Environments[0].History).Status);
            Assert.Contains("DATABASE_URL=" + value, Docker("inspect", "--format", "{{range .Config.Env}}{{println .}}{{end}}", containerName));
            Assert.DoesNotContain(value, Docker("logs", containerName));
            Assert.DoesNotContain(value, JsonSerializer.Serialize(state));
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
            status = "promoted", gate = "passed", gateRunId = "release/test-gate", candidateSha = commit, atomicPush = true,
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
