using System.Net;
using System.Text;
using AgentStudio.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteCliOneShotTests
{
    [Fact]
    public async Task Networked_one_shot_uses_job_transport_and_surfaces_waiting_host()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-server/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskServer:BaseUrl"] = "http://task-server/",
            ["Security:Profile"] = "networked",
        }).Build();
        var oneShot = new RemoteCliOneShot("codex", new ClientFactory(client), config);
        var result = await oneShot.RunAsync(new("codex", "gpt-6-sol", "Summarize this card."));
        Assert.False(result.Ok);
        Assert.Equal("waiting for a host with model-acceptance:codex:gpt-6-sol", result.Error);
        Assert.Equal("/api/v1/one-shot-jobs", handler.Path);
        Assert.Contains("Summarize this card.", handler.Body);
    }

    [Fact]
    public async Task Completed_remote_job_returns_text_and_token_usage()
    {
        var handler = new RecordingHandler { Complete = true };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-server/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskServer:BaseUrl"] = "http://task-server/",
        }).Build();
        var result = await new RemoteCliOneShot("codex", new ClientFactory(client), config)
            .RunAsync(new("codex", "gpt-6-sol", "Summarize this card."));
        Assert.True(result.Ok);
        Assert.Equal("Remote summary", result.ParsedText);
        Assert.Equal(12, result.Usage?.InputTokens);
        Assert.Equal(3, result.Usage?.OutputTokens);
        Assert.Equal("/api/v1/one-shot-jobs/job-1", handler.Path);
    }

    [Fact]
    public async Task Late_claim_can_complete_after_the_original_queue_deadline()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var handler = new LateClaimHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-server/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskServer:BaseUrl"] = "http://task-server/",
        }).Build();
        var run = new RemoteCliOneShot("codex", new ClientFactory(client), config, clock: clock)
            .RunAsync(new("codex", "gpt-6-sol", "Summarize this card.")
            { Timeout = TimeSpan.FromSeconds(1) });
        await WaitUntilAsync(() => handler.Submitted);
        await Task.Delay(10);
        for (var poll = 1; poll <= 16; poll++)
        {
            await WaitUntilAsync(() => handler.Polls == poll - 1);
            clock.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => handler.Polls == poll);
        }

        var result = await run;
        Assert.True(result.Ok, result.Error);
        Assert.Equal("Remote summary", result.ParsedText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the fake transport poll.");
    }

    [Fact]
    public async Task Networked_one_shot_without_task_server_never_spawns_a_provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:Profile"] = "networked",
            ["CodexCli:Path"] = "/missing/provider-cli",
        }).Build();
        var oneShot = new RemoteCliOneShot("codex",
            new ClientFactory(new HttpClient(new RecordingHandler())), config);
        var result = await oneShot.RunAsync(new("codex", "gpt-6-sol", "Hello"));
        Assert.False(result.Ok);
        Assert.Contains("Task Server", result.Error);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class LateClaimHandler : HttpMessageHandler
    {
        private int _polls;
        private int _submitted;
        public int Polls => Volatile.Read(ref _polls);
        public bool Submitted => Volatile.Read(ref _submitted) != 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) Volatile.Write(ref _submitted, 1);
            var poll = request.Method == HttpMethod.Post ? 0 : Interlocked.Increment(ref _polls);
            var body = request.Method == HttpMethod.Post
                ? """{"jobId":"job-1","status":"pending","capability":"model-acceptance:codex:gpt-6-sol"}"""
                : poll < 15
                    ? """{"jobId":"job-1","status":"pending","capability":"model-acceptance:codex:gpt-6-sol"}"""
                    : poll == 15
                        ? """{"jobId":"job-1","status":"claimed","capability":"model-acceptance:codex:gpt-6-sol"}"""
                        : """{"jobId":"job-1","status":"completed","capability":"model-acceptance:codex:gpt-6-sol","text":"Remote summary"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Complete { get; init; }
        public string? Path { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? string.Empty :
                await request.Content.ReadAsStringAsync(cancellationToken);
            var responseBody = Complete
                ? request.Method == HttpMethod.Post
                    ? """{"jobId":"job-1","status":"pending","capability":"model-acceptance:codex:gpt-6-sol"}"""
                    : """{"jobId":"job-1","status":"completed","capability":"model-acceptance:codex:gpt-6-sol","text":"Remote summary","usage":{"inputTokens":12,"outputTokens":3,"cacheReadTokens":0,"cacheCreationTokens":0,"model":"gpt-6-sol"}}"""
                : """
                    {"jobId":"","status":"waiting","capability":"model-acceptance:codex:gpt-6-sol","error":"waiting for a host with model-acceptance:codex:gpt-6-sol"}
                    """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
