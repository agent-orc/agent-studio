using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2826: Execution Hosts could not tell that a runner host lagged the
/// Stable release because the daemon never said which release it was running in
/// a comparable form. Registration and every capability heartbeat must carry the
/// release identity, and the heartbeat is the one that matters - an in-place
/// upgrade must be visible without waiting for a re-registration.
/// </summary>
public sealed class RunnerReleaseIdentityWireTests
{
    [Fact]
    public async Task Registration_and_heartbeat_both_report_the_release_identity()
    {
        var handler = new BodyRecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner",
            RunnerName = "runner",
            Hostname = "host",
            BackendName = "test",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            ClaudeCliBin = "claude",
        };
        using var client = new TaskServerClient(
            http,
            "runner",
            usesDurableTaskServer: true,
            options: options);

        await client.RegisterAsync("runner", "service", CancellationToken.None);
        await client.AdvertiseCapabilitiesAsync(
            [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
            null,
            1,
            CancellationToken.None);

        var registration = handler.Read<RegisterRunnerRequest>("/api/v1/runners/runner");
        var heartbeat = handler.Read<CapabilityAdvertisementRequest>("/api/v1/runners/runner/capabilities");

        Assert.NotNull(registration.Release);
        Assert.NotNull(heartbeat.Release);
        Assert.Equal(registration.Release, heartbeat.Release);
        Assert.False(string.IsNullOrWhiteSpace(heartbeat.Release!.ReleaseId));
        Assert.False(string.IsNullOrWhiteSpace(heartbeat.Release.Version));
        // The opaque label stays the release id, so existing readers are unchanged.
        Assert.Equal(registration.RunnerVersion, registration.Release!.ReleaseId);
    }

    private sealed class BodyRecordingHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public T Read<T>(string path)
        {
            Assert.True(_bodies.ContainsKey(path), $"No request was sent to {path}.");
            return JsonSerializer.Deserialize<T>(_bodies[path], Json)
                   ?? throw new InvalidOperationException($"Empty body for {path}.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Content is not null)
                _bodies[path] = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}
