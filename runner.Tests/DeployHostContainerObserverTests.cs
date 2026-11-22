using System.Net;
using System.Text;
using AgentRunner.RunTargets;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DeployHostContainerObserverTests
{
    // clock-independent: the restarting state drives the assertion regardless of StartedAt.
    [Fact]
    public async Task ExistingContainer_ReportsCrashLoopRestartsAndMaskedStdoutAndStderr()
    {
        var secret = "credential-" + Guid.NewGuid().ToString("N");
        var shortSecret = Guid.NewGuid().ToString("N")[..6];
        var artifact = "example/app@sha256:" + new string('a', 64);
        string? report = null;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"[{{\"project\":\"demo\",\"environmentId\":\"prod\",\"container\":\"deploy-demo-prod\",\"artifact\":\"{artifact}\"}}]",
                    Encoding.UTF8, "application/json") };
            report = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("https://studio.test/") };
        Task<ProcessResult> Docker(string file, IReadOnlyList<string> args, CancellationToken _)
        {
            Assert.Equal("docker", file);
            if (args[0] == "container")
                return Task.FromResult(new ProcessResult(0,
                    "{\"State\":{\"Status\":\"restarting\",\"StartedAt\":\"2026-11-17T10:00:00Z\"},\"RestartCount\":102,\"Config\":{\"Env\":[\"API_TOKEN=" + secret + "\",\"API_KEY=" + shortSecret + "\"]}}", ""));
            Assert.Equal("logs", args[0]);
            return Task.FromResult(new ProcessResult(0, "starting\nFATAL: " + secret, "stderr failure " + shortSecret));
        }

        Assert.Equal(1, await DeployHostContainerObserver.ObserveAsync(client, "host", CancellationToken.None, Docker));
        Assert.NotNull(report);
        Assert.Contains("\"restarts\":102", report);
        Assert.Contains("\"state\":\"crash-loop\"", report);
        Assert.Contains("FATAL: [redacted]", report);
        Assert.Contains("stderr failure", report);
        Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain(shortSecret, report);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(answer(request));
    }
}
