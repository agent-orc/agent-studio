using System.Net;
using System.Net.Http.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class GateReportOutboxTests
{
    [Fact]
    public async Task Pending_fenced_report_survives_daemon_restart_and_replays()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-outbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outbox = new GateReportOutbox(root);
            var authority = new GateAuthorityRequest("gate-a", "instance-a", "lease-a", 2, 1);
            var report = new GateReport("passed", null, new string('a', 40),
                new string('b', 40), false, false, [], "linux", "dotnet",
                new Dictionary<string, string>(), new Dictionary<string, string>(), "complete");
            await outbox.EnqueueAsync("gat_123", new SubmitGateReportRequest(
                authority, report, "report:gat_123:2"), default);
            var handler = new RestartingReportServer();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
            using var client = new TaskServerClient(http, "gate-a");

            Assert.True(await outbox.ReplayAsync(client, _ => { }, default));
            var restartedOutbox = new GateReportOutbox(root);
            Assert.False(await restartedOutbox.ReplayAsync(client, _ => { }, default));
            Assert.Equal(2, handler.Requests);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "gate-reports"), "*.json"));
        }
        finally
        {
            ResilientDirectory.Delete(root);
        }
    }

    private sealed class RestartingReportServer : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.EndsWith("/api/v1/gates/attempts/gat_123/report",
                request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (Requests == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var attempt = new GateAttempt("gat_123", "subject", 1, GateStates.Passed,
                "gate-a", "host-a", null, "passed", DateTime.UtcNow,
                DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GateAttemptView(attempt, null, null)),
            });
        }
    }
}
