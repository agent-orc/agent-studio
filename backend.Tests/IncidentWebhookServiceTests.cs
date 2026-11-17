using System.Net;
using System.Text.Json;
using AgentStudio.Projects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IncidentWebhookServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "incident-webhook-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProductionIncidentPayload_ContainsRequiredFields_AndNoTargetCredentials()
    {
        var handler = new CaptureHandler();
        var service = Create(handler);
        var secret = Guid.NewGuid().ToString("N");
        service.Set("shop", $"https://hooks.example/incoming?token={secret}", "https://studio.example");

        // clock-independent: this fixed timestamp is only serialized and compared, never measured against now.
        Assert.True(await service.DeliverAsync("shop", "opened", "production", "Production", "api",
            new DateTimeOffset(2026, 11, 16, 1, 2, 0, TimeSpan.Zero), "health-production-1"));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://hooks.example/incoming?token=" + secret, handler.Target);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("shop", json.RootElement.GetProperty("project").GetString());
        Assert.Equal("production", json.RootElement.GetProperty("environment").GetString());
        Assert.Equal("api", json.RootElement.GetProperty("signal").GetString());
        Assert.Equal("opened", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("2026-11-16T01:02:00+00:00", json.RootElement.GetProperty("startedAt").GetString());
        Assert.Equal("https://studio.example/#/projects/shop/operations", json.RootElement.GetProperty("link").GetString());
        Assert.DoesNotContain(secret, handler.Body);
        Assert.DoesNotContain("@", handler.Body);
        Assert.False(service.Get("shop").Destination.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_RejectsUnsafeUrls_AndSurvivesRestartWithoutDisclosingToken()
    {
        var handler = new CaptureHandler();
        var service = Create(handler);
        Assert.Throws<ArgumentException>(() => service.Set("shop", "http://hooks.example/test", "https://studio.example"));
        Assert.Throws<ArgumentException>(() => service.Set("shop", "https://user:password@hooks.example/test", "https://studio.example"));
        service.Set("shop", "https://hooks.example/test?key=secret", "https://studio.example");

        var restarted = Create(handler);
        Assert.True(restarted.Get("shop").Configured);
        Assert.Equal("hooks.example", restarted.Get("shop").Destination);
    }

    private IncidentWebhookService Create(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        return new IncidentWebhookService(config, new FakeClients(handler), NullLogger<IncidentWebhookService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Target { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Target = request.RequestUri?.ToString();
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
