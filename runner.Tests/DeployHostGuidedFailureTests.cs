using System.Net;
using System.Net.Http.Json;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployHostGuidedFailureTests
{
    [Fact]
    public async Task HealthRetry_ChecksHealthWithoutRedeployingTheArtifact()
    {
        var healthRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LoopbackHttpServer(async context =>
        {
            context.Response.StatusCode = 200;
            await context.Response.CompleteAsync();
            healthRequest.TrySetResult();
        });
        var tokenFile = Path.Combine(Path.GetTempPath(), "deploy-health-retry-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, "test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plane = new RetryPlane(new
            {
                id = "retry-job", project = "demo", environmentId = "stage", hostId = "stage-host",
                artifact = "artifact-unavailable", template = "container", destination = "invalid-port",
                healthUrl = listener.BaseUrl + "/", secretRefs = new[] { "DB-URL", "DB.URL" },
                missingSecretRefs = new[] { new { name = "DB-URL", reason = "not-bound" } },
                containerPort = 8080, previousArtifact = (string?)null, status = "claimed",
                leaseToken = "lease-" + Guid.NewGuid().ToString("N"), retryStep = "health",
            });
            using var client = new HttpClient(plane) { BaseAddress = new Uri("http://studio.test/") };
            var result = await DeployHostCommand.RunAsync(
                ["http://studio.test", "stage-host", tokenFile, "/missing/artifacts"], CancellationToken.None, client);
            await healthRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, result);
            Assert.True(plane.ReportedHealthy);
        }
        finally
        {
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
