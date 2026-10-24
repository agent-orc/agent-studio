using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployHostGuidedFailureTests
{
    [Fact]
    public async Task HealthRetry_ChecksHealthWithoutRedeployingTheArtifact()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var healthRequest = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });
        var tokenFile = Path.Combine(Path.GetTempPath(), "deploy-health-retry-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, "test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plane = new RetryPlane(new
            {
                id = "retry-job", project = "demo", environmentId = "stage", hostId = "stage-host",
                artifact = "artifact-unavailable", template = "container", destination = "invalid-port",
                healthUrl = $"http://127.0.0.1:{port}/", secretRefs = Array.Empty<string>(),
                containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                leaseToken = "lease-" + Guid.NewGuid().ToString("N"), retryStep = "health",
            });
            using var client = new HttpClient(plane) { BaseAddress = new Uri("http://studio.test/") };
            var result = await DeployHostCommand.RunAsync(
                ["http://studio.test", "stage-host", tokenFile, "/missing/artifacts"], CancellationToken.None, client);
            await healthRequest.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, result);
            Assert.True(plane.ReportedHealthy);
        }
        finally
        {
            listener.Stop();
            File.Delete(tokenFile);
        }
    }

    private sealed class RetryPlane(object job) : HttpMessageHandler
    {
        public bool ReportedHealthy { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal) == true)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
            if (request.RequestUri?.AbsolutePath.EndsWith("/report", StringComparison.Ordinal) == true)
            {
                var result = await request.Content!.ReadFromJsonAsync<Report>(cancellationToken);
                ReportedHealthy = result?.Healthy == true && result.Evidence == "health-ok";
                return new(HttpStatusCode.OK);
            }
            return new(HttpStatusCode.NotFound);
        }

        private sealed record Report(bool Healthy, string Evidence);
    }
}
