using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3432: a failing claim poll backs off up to one minute, returns to two seconds
/// after a success, logs its status code once per change, and stops on route-unavailable.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClaimPollLoopTests
{
    [Theory]
    [InlineData(2, false, 4)]
    [InlineData(4, false, 8)]
    [InlineData(32, false, 60)]
    [InlineData(60, false, 60)]
    [InlineData(0, false, 4)]
    [InlineData(2, true, 2)]
    [InlineData(60, true, 2)]
    public void Backoff_matrix(int currentSeconds, bool succeeded, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            ClaimPollBackoff.Next(TimeSpan.FromSeconds(currentSeconds), succeeded));

    [Fact]
    public void Failure_log_names_the_status_once_per_change_and_counts_repeats()
    {
        var log = new ClaimPollFailureLog("git setup");
        var unauthorized = new TaskServerException(401, "refused", "authentication-required");

        Assert.Equal("git setup poll failed: status=401 code=authentication-required", log.Failed(unauthorized));
        Assert.Null(log.Failed(unauthorized));
        Assert.Null(log.Failed(unauthorized));
        Assert.Equal("git setup poll failed: status=503 (previous failure status=401 code=authentication-required repeated 3 times)",
            log.Failed(new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("git setup poll failed: type=TaskCanceledException (previous failure status=503 repeated 1 times)",
            log.Failed(new TaskCanceledException()));
        Assert.Equal("git setup poll recovered after type=TaskCanceledException repeated 1 times", log.Succeeded());
        Assert.Null(log.Succeeded());
    }

    [Fact]
    public async Task Failing_claims_back_off_log_once_and_reset_after_a_success()
    {
        var replies = new Queue<HttpStatusCode>(
            [.. Enumerable.Repeat(HttpStatusCode.Unauthorized, 7), HttpStatusCode.NoContent, HttpStatusCode.Unauthorized]);
        using var http = new HttpClient(new Handler(_ =>
        {
            HttpStatusCode status;
            lock (replies) status = replies.Dequeue();
            return status == HttpStatusCode.Unauthorized
                ? new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new ApiError("authentication-required", "Sign in.")),
                }
                : new HttpResponseMessage(status);
        })) { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a");
        using var stop = new CancellationTokenSource();
        var clock = new ImmediateTimeProvider(limit: 9, stop.Cancel);
        var logs = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaimPollLoop.RunAsync("git setup",
            async ct => await client.ClaimGitSetupAsync("runner-a", ct), logs.Add, clock, stop.Token));

        Assert.Equal([4, 8, 16, 32, 60, 60, 60, 2, 4], clock.Delays.Select(delay => (int)delay.TotalSeconds));
        Assert.Equal(
        [
            "git setup poll failed: status=401 code=authentication-required",
            "git setup poll recovered after status=401 code=authentication-required repeated 7 times",
            "git setup poll failed: status=401 code=authentication-required",
        ], logs);
    }

    [Fact]
    public async Task Device_login_poll_stops_once_when_the_server_does_not_offer_the_route()
    {
        var claims = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Interlocked.Increment(ref claims);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new ApiError(ApiErrorCodes.RouteUnavailable, "Not offered.")),
            };
        })) { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a");
        var logs = new List<string>();
        var directory = Path.Combine(Path.GetTempPath(), "claim-poll-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new DeviceLoginPoller(client, logs.Add, directory, clock: new ImmediateTimeProvider())
                .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        Assert.Equal(1, claims);
        Assert.Equal(["device-login poll stopped: the server does not offer this route (status=404)"],
            logs.Where(line => line.StartsWith("device-login poll", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_plain_not_found_is_no_work_and_keeps_polling()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a");

        Assert.Null(await client.ClaimDeviceLoginAsync(CancellationToken.None));
        Assert.Null(await client.ClaimGitSetupAsync("runner-a", CancellationToken.None));
    }

    /// <summary>Records every requested delay and fires it at once, up to <paramref name="limit"/> delays.</summary>
    private sealed class ImmediateTimeProvider(int limit = int.MaxValue, Action? atLimit = null) : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get { lock (_delays) return [.. _delays]; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            int count;
            lock (_delays)
            {
                _delays.Add(dueTime);
                count = _delays.Count;
            }
            if (count >= limit) atLimit?.Invoke();
            else ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
