using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3432: each host logged "device-login poll failed: HttpRequestException" and
/// "git setup deferred type=HttpRequestException" every two seconds, without the
/// status. A failing claim poll now backs off to one minute, logs its status once
/// per change with a count, and returns to two seconds after an answer.
/// </summary>
public sealed class ClaimPollPacerTests
{
    public static TheoryData<int, int> Backoffs => new()
    {
        { 0, 2 }, { 1, 4 }, { 2, 8 }, { 3, 16 }, { 4, 32 }, { 5, 60 }, { 6, 60 }, { 1000, 60 },
    };

    [Theory]
    [MemberData(nameof(Backoffs))]
    public void Backoff_doubles_up_to_one_minute(int failures, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), ClaimPollPacer.Backoff(failures));

    [Fact]
    public void The_delay_grows_while_the_poll_fails_and_resets_after_an_answer()
    {
        var pacer = new ClaimPollPacer("git setup", _ => { });
        var refused = new TaskServerException(401, "refused", "authentication-required");

        var delays = Enumerable.Range(0, 7).Select(_ => pacer.RecordFailed(refused)).ToArray();

        Assert.Equal(new[] { 4, 8, 16, 32, 60, 60, 60 }.Select(seconds => TimeSpan.FromSeconds(seconds)), delays);
        Assert.Equal(ClaimPollPacer.BaseInterval, pacer.RecordAnswered());
        Assert.Equal(0, pacer.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(4), pacer.RecordFailed(refused));
        Assert.False(pacer.Stopped);
    }

    [Fact]
    public void A_failing_poll_logs_its_status_once_per_change_with_a_count()
    {
        var logs = new List<string>();
        var pacer = new ClaimPollPacer("device-login", logs.Add);

        for (var poll = 0; poll < 30; poll++) pacer.RecordFailed(new TaskServerException(401, "refused", "authentication-required"));
        Assert.Equal(["device-login poll failed: status=401 code=authentication-required; backing off up to 60s, repeats are counted"], logs);

        for (var poll = 0; poll < 3; poll++) pacer.RecordFailed(new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway));
        Assert.Equal(2, logs.Count);
        Assert.Equal("device-login poll failed 30 times with status=401 code=authentication-required; now status=502", logs[1]);

        pacer.RecordAnswered();
        pacer.RecordAnswered();
        Assert.Equal(3, logs.Count);
        Assert.Equal("device-login poll recovered after 33 failed polls (last: status=502 x3)", logs[2]);
    }

    public static TheoryData<Exception, string> Descriptions => new()
    {
        { new TaskServerException(403, "denied", "runner-identity-denied"), "status=403 code=runner-identity-denied" },
        { new TaskServerException(500, "broken"), "status=500" },
        { new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable), "status=503" },
        { new HttpRequestException(HttpRequestError.ConnectionError, "refused"), "transport=ConnectionError" },
        { new TaskCanceledException("timeout"), "timeout" },
        { new InvalidOperationException("other"), "type=InvalidOperationException" },
    };

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void A_failure_is_described_by_its_status_not_its_message(Exception failure, string description)
        => Assert.Equal(description, ClaimPollPacer.Describe(failure));

    [Fact]
    public void A_server_that_does_not_map_the_route_stops_the_loop_after_one_line()
    {
        var logs = new List<string>();
        var pacer = new ClaimPollPacer("git setup", logs.Add);
        var absent = new TaskServerException(404, "absent", RouteAbsenceContract.ErrorCode);

        pacer.RecordFailed(absent);
        pacer.RecordFailed(absent);

        Assert.True(pacer.Stopped);
        Assert.Equal(["git setup stopped: this server does not map its route; restart the host after the server maps it"], logs);
    }

    [Fact]
    public async Task A_refused_claim_carries_its_status_and_refusal_code()
    {
        var status = HttpStatusCode.Unauthorized;
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(status)
        {
            // The monolith names its refusal in "error", the Task Server in "code".
            Content = JsonContent.Create(new { error = "authentication-required", message = "A session is required." }),
        })) { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: false);

        var device = await Assert.ThrowsAsync<TaskServerException>(() => client.ClaimDeviceLoginAsync(default));
        var git = await Assert.ThrowsAsync<TaskServerException>(() => client.ClaimGitSetupAsync("runner-a", default));

        Assert.Equal((401, "authentication-required"), (device.StatusCode, device.ErrorCode));
        Assert.Equal((401, "authentication-required"), (git.StatusCode, git.ErrorCode));
    }

    [Fact]
    public async Task An_empty_device_login_answer_is_no_work()
    {
        // The Task Server answers an empty queue with an empty 200, the monolith with 204.
        var status = HttpStatusCode.OK;
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) }))
            { BaseAddress = new Uri("https://studio.test") };
        using var client = new TaskServerClient(http, "runner-a", usesDurableTaskServer: false);

        Assert.Null(await client.ClaimDeviceLoginAsync(default));
        status = HttpStatusCode.NoContent;
        Assert.Null(await client.ClaimDeviceLoginAsync(default));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
