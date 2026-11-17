using System.Net;
using System.Text;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// AGT-3649-D12: an idle claim is "no work", never a logged JSON error.
// clock-independent: the claim expiry is a payload value the worker only parses, never compared with now.
public sealed class PullGateDaemonPollTests
{
    [Theory]
    [InlineData(HttpStatusCode.NoContent, null)]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "null")]
    public async Task Empty_claim_is_idle_without_error_and_keeps_two_second_interval(
        HttpStatusCode status, string? body)
    {
        using var http = new HttpClient(new GateHandler(() => Response(status, body)))
        { BaseAddress = new Uri("http://studio/") };
        using var stop = new CancellationTokenSource();
        var logs = new List<string>();
        var delays = new List<TimeSpan>();
        var executed = 0;

        await PullGateDaemon.PollAsync(http, "gate-a", (_, _) =>
        {
            executed++;
            return Task.CompletedTask;
        }, (interval, _) =>
        {
            delays.Add(interval);
            if (delays.Count == 3) stop.Cancel();
            return Task.CompletedTask;
        }, logs.Add, stop.Token);

        Assert.Empty(logs);
        Assert.Equal(0, executed);
        Assert.Equal([PullGateDaemon.IdleInterval, PullGateDaemon.IdleInterval,
            PullGateDaemon.IdleInterval], delays);
        Assert.Equal(TimeSpan.FromSeconds(2), PullGateDaemon.IdleInterval);
    }

    [Fact]
    public async Task Claim_with_body_is_returned()
    {
        using var http = new HttpClient(new GateHandler(() => Response(HttpStatusCode.OK,
            """{"runId":"run-1","lease":"lease-1","expiresAt":"2026-11-16T08:00:00+00:00"}""")))
        { BaseAddress = new Uri("http://studio/") };

        var claim = await PullGateDaemon.ClaimAsync(http, CancellationToken.None);

        Assert.Equal(("run-1", "lease-1"), (claim!.RunId, claim.Lease));
    }

    [Fact]
    public async Task Empty_claim_endpoint_answers_204_without_body()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging()
                .AddSingleton(NullLoggerFactory.Instance).BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();

        await PullGateEndpoints.ClaimResult(null).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? body) => new(status)
    {
        Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class GateHandler(Func<HttpResponseMessage> claim) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal)
                    ? Response(HttpStatusCode.OK, """{"id":"gate-a","name":"gate-a","slots":1,"processorCount":2}""")
                    : claim());
    }
}
