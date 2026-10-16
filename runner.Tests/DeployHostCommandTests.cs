using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployHostCommandTests
{
    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task Claims_Deploys_HealthChecks_AndReports_RealLocalContainer()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The container probe uses a Linux Docker host.");
        var image = Docker("image", "inspect", "caddy:2-alpine", "--format", "{{index .RepoDigests 0}}").Trim();
        Skip.If(string.IsNullOrWhiteSpace(image), "The CI container fixture caddy:2-alpine is not cached.");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var project = "probe" + Guid.NewGuid().ToString("N")[..8];
        var name = "deploy-" + project + "-stage";
        var root = Path.Combine(Path.GetTempPath(), "deploy-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, "token");
        await File.WriteAllTextAsync(tokenFile, "test-deploy-credential");
        var reported = false;
        var reportedHealthy = false;
        using var client = new HttpClient(new FakeApi(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
                return Json(new { id = "job", project, environmentId = "stage", hostId = "test-host",
                    artifact = image, template = "container", destination = port.ToString(), containerPort = 80,
                    healthUrl = $"http://127.0.0.1:{port}/", secretRefs = Array.Empty<string>(),
                    previousArtifact = (string?)null, status = "claimed", leaseToken = "lease" });
            reported = true;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            reportedHealthy = JsonDocument.Parse(body).RootElement.GetProperty("healthy").GetBoolean();
            return Json(new { ok = true });
        }));
        try
        {
            var result = await DeployHostCommand.RunAsync(
                ["http://127.0.0.1:1", "test-host", tokenFile, root, root], CancellationToken.None, client);
            Assert.Equal(0, result);
            Assert.True(reported);
            Assert.True(reportedHealthy);
        }
        finally
        {
            Docker("rm", "-f", name);
            Directory.Delete(root, true);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(value) };

    private static string Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : string.Empty;
    }

    private sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
