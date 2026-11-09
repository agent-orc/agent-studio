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
    public async Task Claim_observed_after_the_queue_deadline_keeps_the_claimed_run_budget()
    {
        var clock = new FakeTimeProvider();
        var handler = new LateClaimHandler(clock);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-server/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskServer:BaseUrl"] = "http://task-server/",
        }).Build();
        var oneShot = new RemoteCliOneShot("codex", new ClientFactory(client), config,
            timeProvider: clock, pollInterval: TimeSpan.Zero);

        var result = await oneShot.RunAsync(new("codex", "gpt-6-sol", "Summarize this card.")
        {
            Timeout = TimeSpan.FromSeconds(1),
        });

        Assert.True(result.Ok);
        Assert.Equal("Late summary", result.ParsedText);
        Assert.Equal(3, handler.Polls);
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

    private sealed class LateClaimHandler(FakeTimeProvider clock) : HttpMessageHandler
    {
        public int Polls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body;
            if (request.Method == HttpMethod.Post)
            {
                body = """{"jobId":"job-1","status":"pending","capability":"model-acceptance:codex:gpt-6-sol"}""";
            }
            else
            {
                Polls++;
                body = Polls switch
                {
                    1 => """{"jobId":"job-1","status":"pending","capability":"model-acceptance:codex:gpt-6-sol"}""",
                    2 => """{"jobId":"job-1","status":"claimed","capability":"model-acceptance:codex:gpt-6-sol"}""",
                    3 => """{"jobId":"job-1","status":"completed","capability":"model-acceptance:codex:gpt-6-sol","text":"Late summary"}""",
                    _ => throw new InvalidOperationException("Unexpected extra poll."),
                };
                // The claim occurs just before the queue deadline, but its
                // response arrives just after it. Completion uses the run budget.
                clock.Advance(Polls switch
                {
                    1 => TimeSpan.FromMilliseconds(30_500),
                    2 => TimeSpan.FromMilliseconds(600),
                    _ => TimeSpan.FromMilliseconds(30_500),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
